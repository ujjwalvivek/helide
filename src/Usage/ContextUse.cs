using System.IO;
using System.Text;
using System.Text.Json;
using Helide.Sessions;

namespace Helide.Usage;

/// <summary>How much of a conversation's context window its last request took up.</summary>
/// <param name="Model">The model, when the record names one.</param>
/// <param name="Source">Where the figure was read, for the tooltip.</param>
internal sealed record ContextFill(long Used, long Window, string? Model, string Source)
{
    public double Percent => Window <= 0 ? 0 : Math.Clamp(100.0 * Used / Window, 0, 100);
}

/// <summary>
/// Reads how full a conversation's context window is, from what each agent writes down.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Claude: the status-line relay carries <c>context_window</c> for the pane that
/// last updated it. Each reading is kept by session, since every pane writes to the one
/// relay file. Until a pane has updated, the last request's input in its transcript, out
/// of the model's window in the models.dev catalog.</item>
/// <item>Codex: the last <c>token_count</c> in the rollout, which names the window.</item>
/// <item>OpenCode: the tokens of the last assistant message, out of the model's window in
/// the catalog.</item>
/// </list>
/// </remarks>
internal static class ContextUse
{
    private static readonly Dictionary<string, ContextFill> Relayed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static ModelPrices? _catalog;
    private static DateTime _catalogStamp;

    public static ContextFill? Read(string agent, string sessionId, string stateDirectory)
    {
        try
        {
            return agent switch
            {
                AgentProviders.Claude => Claude(sessionId, stateDirectory),
                AgentProviders.Codex => Codex(sessionId),
                AgentProviders.OpenCode => OpenCode(sessionId),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Claude ----------------------------------------------------------------------

    private static ContextFill? Claude(string sessionId, string stateDirectory)
    {
        KeepRelayed(stateDirectory);

        lock (Gate)
        {
            if (Relayed.TryGetValue(sessionId, out var relayed))
                return relayed;
        }

        if (ClaudeSessions.FindTranscriptPath(sessionId) is not { } transcript)
            return null;

        var last = LastMatching<(long Used, string Model)?>(transcript, record =>
        {
            if (JsonlScanner.Text(record, "type") != "assistant"
                || record.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True
                || !record.TryGetProperty("message", out var message)
                || !message.TryGetProperty("usage", out var usage)
                || JsonlScanner.Text(message, "model") is not { } model)
                return null;

            var used = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens");
            return used > 0 ? (used, model) : null;
        });

        if (last is not { } found || Catalog().ContextWindow("anthropic", found.Model) is not { } window)
            return null;

        return new ContextFill(found.Used, window, found.Model, "Claude's transcript, last request");
    }

    // Takes the relay's reading into the session it belongs to.
    private static void KeepRelayed(string stateDirectory)
    {
        var relay = ClaudeStatusRelay.RelayFile(stateDirectory);
        if (!File.Exists(relay))
            return;

        try
        {
            using var stream = new FileStream(relay, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            if (JsonlScanner.Text(root, "session_id") is not { } session
                || !root.TryGetProperty("context_window", out var context)
                || context.ValueKind != JsonValueKind.Object
                || !context.TryGetProperty("current_usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
                return;

            var window = Number(context, "context_window_size");
            var used = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens");
            if (window <= 0)
                return;

            var model = root.TryGetProperty("model", out var named) ? JsonlScanner.Text(named, "display_name") : null;

            lock (Gate)
                Relayed[session] = new ContextFill(used, window, model, "Claude's status line");
        }
        catch (Exception)
        {
            // Half-written: the next update has it.
        }
    }

    // ---- Codex -----------------------------------------------------------------------

    private static ContextFill? Codex(string sessionId)
    {
        if (CodexSessions.FindRolloutPath(sessionId) is not { } rollout)
            return null;

        return LastMatching<ContextFill>(rollout, record =>
        {
            if (JsonlScanner.Text(record, "type") != "event_msg"
                || !record.TryGetProperty("payload", out var payload)
                || JsonlScanner.Text(payload, "type") != "token_count"
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("last_token_usage", out var last))
                return null;

            var window = Number(info, "model_context_window");
            return window > 0 ? new ContextFill(Number(last, "total_tokens"), window, null, "Codex's token count") : null;
        });
    }

    // ---- OpenCode --------------------------------------------------------------------

    private const string CurrentQuery = """
        SELECT
            json_extract(data, '$.model.providerID'),
            json_extract(data, '$.model.id'),
            COALESCE(json_extract(data, '$.tokens.input'), 0)
                + COALESCE(json_extract(data, '$.tokens.output'), 0)
                + COALESCE(json_extract(data, '$.tokens.reasoning'), 0)
                + COALESCE(json_extract(data, '$.tokens.cache.read'), 0)
                + COALESCE(json_extract(data, '$.tokens.cache.write'), 0) AS used
        FROM session_message
        WHERE session_id = $id AND type = 'assistant'
        ORDER BY time_created DESC
        """;

    private const string LegacyQuery = """
        SELECT
            json_extract(data, '$.providerID'),
            json_extract(data, '$.modelID'),
            COALESCE(json_extract(data, '$.tokens.input'), 0)
                + COALESCE(json_extract(data, '$.tokens.output'), 0)
                + COALESCE(json_extract(data, '$.tokens.reasoning'), 0)
                + COALESCE(json_extract(data, '$.tokens.cache.read'), 0)
                + COALESCE(json_extract(data, '$.tokens.cache.write'), 0) AS used
        FROM message
        WHERE session_id = $id AND json_extract(data, '$.role') = 'assistant'
        ORDER BY time_created DESC
        """;

    private static ContextFill? OpenCode(string sessionId)
    {
        var path = OpenCodeSessions.DatabasePath;
        if (!File.Exists(path))
            return null;

        using var connection = OpenCodeSessions.Connect(path);

        foreach (var query in new[] { CurrentQuery, LegacyQuery })
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = query;
                command.Parameters.AddWithValue("$id", sessionId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    // A message still being written has no tokens yet; the one before it does.
                    var used = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    if (used <= 0 || reader.IsDBNull(0) || reader.IsDBNull(1))
                        continue;

                    var model = reader.GetString(1);
                    return Catalog().ContextWindow(reader.GetString(0), model) is { } window
                        ? new ContextFill(used, window, model, "OpenCode's last message, window from models.dev")
                        : null;
                }
            }
            catch (Exception)
            {
                // That layout is not there; the other one may be.
            }
        }

        return null;
    }

    // ---- reading ---------------------------------------------------------------------

    private static ModelPrices Catalog()
    {
        var stamp = File.Exists(ModelPrices.CatalogPath) ? File.GetLastWriteTimeUtc(ModelPrices.CatalogPath) : default;
        lock (Gate)
        {
            if (_catalog is null || stamp != _catalogStamp)
            {
                _catalog = ModelPrices.Load();
                _catalogStamp = stamp;
            }

            return _catalog;
        }
    }

    private const int TailBytes = 1 << 20;

    // The last record the reader makes something of. The end of the file is read first,
    // which nearly always has it; only a file whose last megabyte has none is read whole.
    private static T? LastMatching<T>(string path, Func<JsonElement, T?> read)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);

        using var text = new StreamReader(stream, Encoding.UTF8);
        if (start > 0)
            text.ReadLine(); // Most likely the middle of a line.

        var found = default(T);
        var any = false;
        while (text.ReadLine() is { } line)
        {
            if (Parse(line, read) is { } value)
            {
                found = value;
                any = true;
            }
        }

        if (any || start == 0)
            return found;

        foreach (var line in File.ReadLines(path))
        {
            if (Parse(line, read) is { } value)
                found = value;
        }

        return found;
    }

    private static T? Parse<T>(string line, Func<JsonElement, T?> read)
    {
        if (line.Length == 0)
            return default;

        try
        {
            using var document = JsonDocument.Parse(line);
            return read(document.RootElement);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;
}
