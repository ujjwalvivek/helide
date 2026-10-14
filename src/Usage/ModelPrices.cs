using System.IO;
using System.Text.Json;

namespace Helide.Usage;

/// <summary>API list price of one model, in dollars per million tokens.</summary>
internal sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)
{
    public decimal CostOf(TokenCounts counts) =>
        (counts.Input * Input
         + counts.Output * Output
         + counts.CacheRead * CacheRead
         + counts.CacheWrite * CacheWrite) / 1_000_000m;

    public bool IsFree => Input == 0 && Output == 0 && CacheRead == 0 && CacheWrite == 0;
}

/// <summary>
/// Prices models from the models.dev catalogue OpenCode keeps on disk.
/// </summary>
/// <remarks>
/// OpenCode downloads the whole catalogue to <c>~/.cache/opencode/models.json</c> and
/// keeps it current, and it lists every provider's prices, Anthropic's and OpenAI's
/// included. Reading that copy means Helide carries no price table of its own to go
/// stale and asks nothing of the network. Without OpenCode installed there is no
/// catalogue, and every figure is reported as unpriced rather than guessed.
/// <para>
/// The point of the figure is "what this would have cost on the API", so a model that is
/// free where it was used -- a <c>-free</c> preview on OpenCode Zen, a <c>:free</c> route on
/// OpenRouter -- is priced as the same model sold elsewhere. Only when no provider sells it
/// is it unpriced.
/// </para>
/// </remarks>
internal sealed class ModelPrices
{
    // Asked first when several providers sell a model under one id. The labs' own prices
    // are the list prices; resellers mark up or discount.
    private static readonly string[] FirstParty =
    [
        "anthropic", "openai", "google", "deepseek", "moonshotai", "zai", "zhipuai",
        "alibaba", "xai", "mistral", "minimax", "xiaomi",
    ];

    private readonly Dictionary<string, ModelPrice> _byProviderModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelPrice> _byModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _contextByProviderModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _contextByModel = new(StringComparer.OrdinalIgnoreCase);

    public static readonly ModelPrices Empty = new();

    public static string CatalogPath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var cache = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
                : xdg;
            return Path.Combine(cache, "opencode", "models.json");
        }
    }

    public static ModelPrices Load()
    {
        try
        {
            if (!File.Exists(CatalogPath))
                return Empty;

            using var stream = new FileStream(
                CatalogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return FromCatalog(document.RootElement);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    internal static ModelPrices FromCatalog(JsonElement root)
    {
        var prices = new ModelPrices();
        if (root.ValueKind != JsonValueKind.Object)
            return prices;

        var providers = root.EnumerateObject()
            .OrderBy(provider => Array.IndexOf(FirstParty, provider.Name) is var rank && rank < 0 ? int.MaxValue : rank)
            .ThenBy(provider => provider.Name, StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            if (provider.Value.ValueKind != JsonValueKind.Object
                || !provider.Value.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var model in models.EnumerateObject())
            {
                if (model.Value.ValueKind == JsonValueKind.Object
                    && model.Value.TryGetProperty("limit", out var limit)
                    && limit.ValueKind == JsonValueKind.Object
                    && limit.TryGetProperty("context", out var context)
                    && context.ValueKind == JsonValueKind.Number
                    && context.TryGetInt64(out var tokens)
                    && tokens > 0)
                {
                    prices._contextByProviderModel[provider.Name + "/" + model.Name] = tokens;
                    prices._contextByModel.TryAdd(Normalize(model.Name), tokens);
                }

                if (ReadPrice(model.Value) is not { } price)
                    continue;

                prices._byProviderModel[provider.Name + "/" + model.Name] = price;

                // The first provider to sell a model for money sets its price; the order
                // above makes that the lab where there is one.
                if (!price.IsFree)
                    prices._byModel.TryAdd(Normalize(model.Name), price);
            }
        }

        return prices;
    }

    /// <summary>
    /// The price to charge a model at, or null when nobody sells it.
    /// </summary>
    /// <param name="provider">Where it was used: "anthropic", "openai", or OpenCode's provider id.</param>
    /// <param name="model">The model id as the agent recorded it.</param>
    public ModelPrice? Find(string provider, string model)
    {
        if (_byProviderModel.TryGetValue(provider + "/" + model, out var exact) && !exact.IsFree)
            return exact;

        return _byModel.TryGetValue(Normalize(model), out var elsewhere) ? elsewhere : null;
    }

    /// <summary>How many tokens the model's context window holds, or null when unknown.</summary>
    public long? ContextWindow(string provider, string model)
    {
        if (_contextByProviderModel.TryGetValue(provider + "/" + model, out var exact))
            return exact;

        return _contextByModel.TryGetValue(Normalize(model), out var elsewhere) ? elsewhere : null;
    }

    // "openrouter/z-ai/glm-5.2:free" and "glm-5.2-free" are both glm-5.2. A dated
    // snapshot ("claude-opus-4-5-20251101") is the model it snapshots.
    internal static string Normalize(string model)
    {
        var id = model.Trim().ToLowerInvariant();

        var slash = id.LastIndexOf('/');
        if (slash >= 0)
            id = id[(slash + 1)..];

        foreach (var suffix in new[] { ":free", "-free" })
        {
            if (id.EndsWith(suffix, StringComparison.Ordinal))
                id = id[..^suffix.Length];
        }

        var dash = id.LastIndexOf('-');
        if (dash > 0 && id.Length - dash - 1 == 8 && id[(dash + 1)..].All(char.IsAsciiDigit))
            id = id[..dash];

        return id;
    }

    private static ModelPrice? ReadPrice(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object
            || !model.TryGetProperty("cost", out var cost)
            || cost.ValueKind != JsonValueKind.Object)
            return null;

        decimal Read(string name) =>
            cost.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDecimal(out var number)
                ? number
                : 0m;

        var input = Read("input");
        var output = Read("output");

        // A provider that lists no cache prices charges cache reads as input and writes
        // as input too, which is what pricing them at zero would get wrong.
        var cacheRead = cost.TryGetProperty("cache_read", out _) ? Read("cache_read") : input;
        var cacheWrite = cost.TryGetProperty("cache_write", out _) ? Read("cache_write") : input;

        return new ModelPrice(input, output, cacheRead, cacheWrite);
    }
}
