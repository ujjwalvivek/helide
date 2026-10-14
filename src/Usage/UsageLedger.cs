using System.IO;
using System.Text.Json;
using Helide.Sessions;

namespace Helide.Usage;

/// <summary>
/// Adds up what the agents have used, from the records they keep themselves.
/// </summary>
/// <remarks>
/// Nothing here is asked of a network or of an agent's process. Claude's and Codex's
/// transcripts are read where they lie, OpenCode's database is queried read-only, and the
/// formats are read only as far as the figures need. Any of them can change shape in an
/// upgrade, so a file or a database that cannot be read counts as no usage rather than as
/// a failure.
/// <para>
/// Transcripts run to gigabytes, so each file is read once and afterwards only from where
/// the last read stopped. That progress is saved beside Helide's own state: the first
/// lifetime view on a machine reads everything, and later ones only what is new. It is
/// one ledger for the whole process; windows share it.
/// </para>
/// </remarks>
internal sealed class UsageLedger
{
    private const string CacheName = "usage-cache.json";

    private static UsageLedger? _shared;
    private static readonly object SharedGate = new();

    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, FileUsage>? _files;

    private ModelPrices _prices = ModelPrices.Empty;
    private DateTime _pricesStamp;

    private UsageLedger(string stateDirectory) =>
        _cachePath = Path.Combine(stateDirectory, CacheName);

    public static UsageLedger Shared(string stateDirectory)
    {
        lock (SharedGate)
            return _shared ??= new UsageLedger(stateDirectory);
    }

    /// <summary>
    /// The usage within a scope. Runs off the UI thread; the first call on a machine can
    /// take a while.
    /// </summary>
    /// <param name="project">The project folder, for the project scope.</param>
    /// <param name="agent">The pane's agent, for the session scope.</param>
    /// <param name="sessionId">The pane's conversation, for the session scope.</param>
    public Task<UsageSummary> SummarizeAsync(UsageScope scope, string? project, string? agent, string? sessionId) =>
        Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return Summarize(scope, project, agent, sessionId);
            }
            finally
            {
                _gate.Release();
            }
        });

    private UsageSummary Summarize(UsageScope scope, string? project, string? agent, string? sessionId)
    {
        var summary = new UsageSummary { Scope = scope };

        // A session belongs to one agent; there is no point reading the other two.
        bool Wanted(string candidate) => scope != UsageScope.Session || candidate == agent;

        if (scope == UsageScope.Session && string.IsNullOrEmpty(sessionId))
            return summary;

        bool InScope(string? directory) => scope switch
        {
            UsageScope.Project => IsWithin(directory, project),
            _ => true,
        };

        if (Wanted(AgentProviders.Claude) || Wanted(AgentProviders.Codex))
            Refresh(Wanted(AgentProviders.Claude), Wanted(AgentProviders.Codex));

        foreach (var type in new[] { AgentProviders.Claude, AgentProviders.Codex })
        {
            if (!Wanted(type) || _files is null)
                continue;

            var usage = new AgentUsage { Agent = type };
            var seconds = 0d;

            foreach (var file in _files.Values)
            {
                if (file.Agent != type)
                    continue;

                if (scope == UsageScope.Session
                    ? !string.Equals(file.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                    : !InScope(file.Cwd))
                    continue;

                foreach (var (model, counts) in file.Models)
                    usage.Add(model, counts);

                usage.Prompts += file.Prompts;
                seconds += file.AgentSeconds;
            }

            usage.AgentTime = TimeSpan.FromSeconds(seconds);
            if (usage.Tokens > 0 || usage.Prompts > 0)
                summary.Agents.Add(usage);
        }

        if (Wanted(AgentProviders.OpenCode)
            && OpenCodeUsageReader.Read(
                InScope,
                scope == UsageScope.Session ? sessionId : null,
                scope + "|" + (scope == UsageScope.Session ? sessionId : scope == UsageScope.Project ? project : null)) is { } openCode
            && (openCode.Tokens > 0 || openCode.Prompts > 0))
            summary.Agents.Add(openCode);

        Price(summary);
        return summary;
    }

    private void Price(UsageSummary summary)
    {
        var prices = Prices();
        summary.HasPriceCatalog = !ReferenceEquals(prices, ModelPrices.Empty);

        foreach (var agent in summary.Agents)
        {
            foreach (var (key, counts) in agent.Models)
            {
                summary.Cost += counts.StoredCost;

                // What OpenCode already charged for is its own figure; what is left is
                // priced from the list.
                var unbilled = counts.Clone();
                if (counts.StoredCostTokens > 0)
                {
                    var share = 1m - Math.Min(1m, (decimal)counts.StoredCostTokens / Math.Max(1, counts.Total));
                    unbilled = new TokenCounts
                    {
                        Input = (long)(counts.Input * share),
                        Output = (long)(counts.Output * share),
                        CacheRead = (long)(counts.CacheRead * share),
                        CacheWrite = (long)(counts.CacheWrite * share),
                    };
                }

                if (unbilled.Total == 0)
                    continue;

                var (provider, model) = agent.Agent switch
                {
                    AgentProviders.Claude => ("anthropic", key),
                    AgentProviders.Codex => ("openai", key),
                    _ => SplitProvider(key),
                };

                if (prices.Find(provider, model) is { } price)
                    summary.Cost += price.CostOf(unbilled);
                else
                    summary.UnpricedTokens += unbilled.Total;
            }
        }
    }

    private static (string Provider, string Model) SplitProvider(string key)
    {
        var slash = key.IndexOf('/');
        return slash < 0 ? ("opencode", key) : (key[..slash], key[(slash + 1)..]);
    }

    // The catalogue is five megabytes; it is read again only when OpenCode has rewritten it.
    private ModelPrices Prices()
    {
        try
        {
            var stamp = File.Exists(ModelPrices.CatalogPath)
                ? File.GetLastWriteTimeUtc(ModelPrices.CatalogPath)
                : default;

            if (stamp != _pricesStamp)
            {
                _prices = ModelPrices.Load();
                _pricesStamp = stamp;
            }
        }
        catch (Exception)
        {
            // Keep whatever was loaded last.
        }

        return _prices;
    }

    // Brings every transcript up to date: new files read from the start, grown files from
    // where they were left, unchanged files not opened at all.
    private void Refresh(bool claude, bool codex)
    {
        _files ??= LoadCache();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        if (claude)
        {
            foreach (var (path, session, subagent) in ClaudeUsageReader.Files())
            {
                seen.Add(path);
                changed |= Update(path, AgentProviders.Claude, usage =>
                {
                    usage.SessionId = session;
                    ClaudeUsageReader.Read(path, usage, subagent);
                });
            }
        }

        if (codex)
        {
            foreach (var path in CodexUsageReader.Files())
            {
                seen.Add(path);
                changed |= Update(path, AgentProviders.Codex, usage => CodexUsageReader.Read(path, usage));
            }
        }

        // A transcript that has gone -- deleted, or moved to the archive under a new
        // path -- stops counting under its old one.
        foreach (var gone in _files.Where(pair =>
                         ((claude && pair.Value.Agent == AgentProviders.Claude)
                          || (codex && pair.Value.Agent == AgentProviders.Codex))
                         && !seen.Contains(pair.Key))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _files.Remove(gone);
            changed = true;
        }

        if (changed)
            SaveCache();
    }

    private bool Update(string path, string agent, Action<FileUsage> read)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return false;
        }
        catch (Exception)
        {
            return false;
        }

        var ticks = info.LastWriteTimeUtc.Ticks;

        if (_files!.TryGetValue(path, out var usage)
            && usage.Length == info.Length
            && usage.WriteTicks == ticks)
            return false;

        // A file that got shorter was rewritten, not appended to; read it again whole.
        if (usage is null || info.Length < usage.Offset || usage.Agent != agent)
            usage = new FileUsage { Agent = agent };

        try
        {
            read(usage);
        }
        catch (Exception)
        {
            // Unreadable right now -- locked, or vanished mid-read. Whatever was counted
            // before stands, and it is tried again next time.
            return false;
        }

        usage.Length = info.Length;
        usage.WriteTicks = ticks;
        _files[path] = usage;
        return true;
    }

    private Dictionary<string, FileUsage> LoadCache()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                using var stream = File.OpenRead(_cachePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, FileUsage>>(stream);
                if (loaded is not null)
                    return new Dictionary<string, FileUsage>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            // A damaged cache only costs a full read.
        }

        return new Dictionary<string, FileUsage>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporary = _cachePath + "." + Environment.ProcessId + ".tmp";
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, _files);
            File.Move(temporary, _cachePath, true);
        }
        catch (Exception)
        {
            // Not saving it only means reading again on the next launch.
        }
    }

    /// <summary>Whether a folder is the project or somewhere inside it.</summary>
    internal static bool IsWithin(string? directory, string? project)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(project))
            return false;

        try
        {
            var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));

            return child.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || child.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
