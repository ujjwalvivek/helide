using System.IO;
using System.Text;
using System.Text.Json;
using Helide.Sessions;

namespace Helide.Usage;

/// <summary>
/// The usage limits each agent last wrote down about itself.
/// </summary>
/// <remarks>
/// Neither agent is asked. Claude Code fetches its limits when it starts and keeps the
/// answer in its global config as <c>cachedUsageUtilization</c>; Codex reports them on
/// every token count in the rollout it is writing, and records a turn it refused for
/// being over the limit, which is the most recent word on a spent window. Both are read from disk, and nothing
/// here goes near a credential or an endpoint: a number already on disk is not worth a
/// token on the wire.
/// <para>
/// The cost is freshness. Claude's reading is only as recent as the last time a Claude
/// started, so every reading carries the moment it was taken, and a window whose reset
/// has passed since is shown as reset rather than as the stale share.
/// </para>
/// </remarks>
internal static class UsageLimits
{
    // How far back from the end of a rollout to look for the latest report. Codex
    // reports on every turn, so the last one is never far from the end.
    private const int TailBytes = 512 * 1024;

    // Rollouts to try, newest first, before concluding Codex has said nothing recently.
    // A rollout that ended on an error, or a conversation that never reached the model,
    // reports no limits, and several of those in a row are common after hitting one.
    private const int RolloutsToTry = 40;

    // What each rollout's tail said, by its length then. Asked every minute, and almost
    // every rollout is finished, so only one that has grown is read again.
    private static readonly Dictionary<string, (long Length, CodexTail? Tail)> TailCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly byte[] RateMarker = Encoding.ASCII.GetBytes("\"used_percent\"");
    private static readonly byte[] RefusalMarker = Encoding.ASCII.GetBytes("usage_limit_exceeded");

    /// <param name="stateDirectory">Where Helide's Claude panes relay their status line to.</param>
    public static IReadOnlyList<AgentLimits> Read(string stateDirectory)
    {
        var limits = new List<AgentLimits>();

        // The relay is live while a Helide Claude pane is in use; the cache is what Claude
        // last wrote for itself, from anywhere. Whichever was read more recently stands.
        if (AgentProviders.IsInstalled(AgentProviders.Claude)
            && new[] { ClaudeStatusRelay.Read(stateDirectory), ReadClaude() }
                .OfType<AgentLimits>()
                .MaxBy(reading => reading.ReadAt) is { } claude)
            limits.Add(claude);

        if (AgentProviders.IsInstalled(AgentProviders.Codex) && ReadCodex() is { } codex)
            limits.Add(codex);

        return limits;
    }

    /// <summary>Claude Code's global config: beside its config folder unless that was moved.</summary>
    private static string ClaudeConfigFile
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
                : Path.Combine(configured, ".claude.json");
        }
    }

    internal static AgentLimits? ReadClaude()
    {
        try
        {
            if (!File.Exists(ClaudeConfigFile))
                return null;

            using var stream = new FileStream(
                ClaudeConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);

            // Only this one entry is looked at. The file holds the account and much
            // else, and none of it is any of Helide's business.
            return document.RootElement.TryGetProperty("cachedUsageUtilization", out var cached)
                ? ParseClaude(cached)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static AgentLimits? ParseClaude(JsonElement cached)
    {
        if (cached.ValueKind != JsonValueKind.Object
            || !cached.TryGetProperty("utilization", out var utilization)
            || utilization.ValueKind != JsonValueKind.Object)
            return null;

        var readAt = cached.TryGetProperty("fetchedAtMs", out var fetched) && fetched.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.MinValue;

        var windows = new List<LimitWindow>();

        // The windows that are null for an account are simply not offered to it.
        foreach (var (key, label) in new[]
                 {
                     ("five_hour", "5 hour"),
                     ("seven_day", "week"),
                     ("seven_day_opus", "week · Opus"),
                     ("seven_day_sonnet", "week · Sonnet"),
                 })
        {
            if (!utilization.TryGetProperty(key, out var window)
                || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("utilization", out var percent)
                || percent.ValueKind != JsonValueKind.Number)
                continue;

            DateTimeOffset? resets = JsonlScanner.Text(window, "resets_at") is { } text
                                     && DateTimeOffset.TryParse(text, out var at)
                ? at
                : null;

            windows.Add(new LimitWindow(label, Math.Clamp(percent.GetDouble(), 0, 100), resets));
        }

        return windows.Count == 0 ? null : new AgentLimits(AgentProviders.Claude, readAt, windows);
    }

    internal static AgentLimits? ReadCodex()
    {
        var rollouts = CodexUsageReader.Files()
            .Select(path =>
            {
                try
                {
                    return (Path: path, At: File.GetLastWriteTimeUtc(path));
                }
                catch (Exception)
                {
                    return (Path: path, At: DateTime.MinValue);
                }
            })
            .OrderByDescending(file => file.At)
            .Take(RolloutsToTry);

        AgentLimits? reading = null;
        CodexRefusal? refusal = null;

        // Newest rollout first, until both a reading and a refusal have turned up: a
        // refusal says the limit is spent, and the reading before it says which window.
        foreach (var (path, _) in rollouts)
        {
            if (Tail(path) is not { } tail)
                continue;

            if (tail.Reading is { } found && (reading is null || found.ReadAt > reading.ReadAt))
                reading = found;
            if (tail.Refusal is { } refused && (refusal is null || refused.At > refusal.At))
                refusal = refused;

            if (reading is not null && refusal is not null)
                break;
        }

        if (refusal is null || (reading is not null && reading.ReadAt >= refusal.At))
            return reading;

        return Spent(reading, refusal);
    }

    /// <summary>
    /// Codex turned a turn away for being over its limit. That is a reading in its own
    /// right, and a newer one than any share it reported before: the window is full.
    /// </summary>
    /// <param name="At">When the turn was refused.</param>
    /// <param name="ResetsAt">When Codex said to try again, if it could be read.</param>
    internal sealed record CodexRefusal(DateTimeOffset At, DateTimeOffset? ResetsAt);

    // The window that ran out is the one resetting when Codex said to come back; any
    // other window keeps what it last reported. With no earlier reading there is only
    // the one fact, a spent limit.
    internal static AgentLimits Spent(AgentLimits? reading, CodexRefusal refusal)
    {
        if (reading is null || reading.Windows.Count == 0)
            return new AgentLimits(AgentProviders.Codex, refusal.At, [new LimitWindow("limit", 100, refusal.ResetsAt)]);

        var spent = refusal.ResetsAt is { } back
            ? reading.Windows.MinBy(window => window.ResetsAt is { } at ? Math.Abs((at - back).TotalSeconds) : double.MaxValue)!
            : reading.Windows.MaxBy(window => window.Percent)!;

        var windows = reading.Windows
            .Select(window => ReferenceEquals(window, spent)
                ? window with { Percent = 100, ResetsAt = refusal.ResetsAt ?? window.ResetsAt }
                : window)
            .ToList();

        return new AgentLimits(AgentProviders.Codex, refusal.At, windows);
    }

    private sealed record CodexTail(AgentLimits? Reading, CodexRefusal? Refusal);

    private static CodexTail? Tail(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            lock (TailCache)
            {
                if (TailCache.TryGetValue(path, out var known) && known.Length == stream.Length)
                    return known.Tail;
            }

            var found = ReadTail(stream);

            lock (TailCache)
                TailCache[path] = (stream.Length, found);

            return found;
        }
        catch (Exception)
        {
            // Unreadable: try the next rollout.
            return null;
        }
    }

    private static CodexTail ReadTail(FileStream stream)
    {
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);

        AgentLimits? reading = null;
        CodexRefusal? refusal = null;

        // Newest line first. The first, partial line of the tail is never complete
        // enough to parse and is skipped by the JSON failing.
        var end = buffer.Length;
        while (end > 0 && (reading is null || refusal is null))
        {
            var newline = Array.LastIndexOf(buffer, (byte)'\n', end - 1);
            var lineStart = newline + 1;
            var line = new ReadOnlySpan<byte>(buffer, lineStart, end - lineStart);
            var memory = buffer.AsMemory(lineStart, end - lineStart);

            if (reading is null && line.IndexOf(RateMarker) >= 0)
                reading = ParseCodexLine(memory);

            if (refusal is null && line.IndexOf(RefusalMarker) >= 0)
                refusal = ParseRefusalLine(memory);

            if (newline < 0)
                break;

            end = newline;
        }

        return new CodexTail(reading, refusal);
    }

    private static AgentLimits? ParseCodexLine(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("rate_limits", out var rates)
                || rates.ValueKind != JsonValueKind.Object)
                return null;

            return ParseCodex(rates, JsonlScanner.Timestamp(root) ?? DateTimeOffset.MinValue);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A turn Codex ended on its usage-limit error. Only the event itself counts: the
    // same words can turn up in a conversation that merely talks about them.
    private static CodexRefusal? ParseRefusalLine(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (JsonlScanner.Text(root, "type") != "event_msg"
                || !root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || JsonlScanner.Text(error, "codex_error_info") != "usage_limit_exceeded")
                return null;

            var at = JsonlScanner.Timestamp(root) ?? DateTimeOffset.MinValue;
            return new CodexRefusal(at, RetryTime(JsonlScanner.Text(error, "message"), at));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RetryPattern = new(
        @"try again at (?<month>[A-Z][a-z]{2})[a-z]* (?<day>\d{1,2})(?:st|nd|rd|th)?,? (?<year>\d{4}) (?<hour>\d{1,2}):(?<minute>\d{2}) ?(?<half>AM|PM)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The moment in "try again at Oct 22nd, 2026 4:06 PM". Codex writes it in the
    /// machine's local time, and says it nowhere else.
    /// </summary>
    internal static DateTimeOffset? RetryTime(string? message, DateTimeOffset refusedAt)
    {
        if (message is null || RetryPattern.Match(message) is not { Success: true } match)
            return null;

        var month = Array.IndexOf(
            System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
            match.Groups["month"].Value) + 1;
        if (month <= 0)
            return null;

        var hour = int.Parse(match.Groups["hour"].Value) % 12 + (match.Groups["half"].Value == "PM" ? 12 : 0);

        try
        {
            var local = new DateTime(
                int.Parse(match.Groups["year"].Value), month, int.Parse(match.Groups["day"].Value),
                hour, int.Parse(match.Groups["minute"].Value), 0, DateTimeKind.Local);
            var resets = new DateTimeOffset(local);
            return resets > refusedAt ? resets : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static AgentLimits? ParseCodex(JsonElement rates, DateTimeOffset readAt)
    {
        var windows = new List<LimitWindow>();

        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!rates.TryGetProperty(key, out var window)
                || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("used_percent", out var percent)
                || percent.ValueKind != JsonValueKind.Number)
                continue;

            var minutes = JsonlScanner.Number(window, "window_minutes");
            DateTimeOffset? resets = window.TryGetProperty("resets_at", out var at) && at.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

            windows.Add(new LimitWindow(WindowLabel(minutes), Math.Clamp(percent.GetDouble(), 0, 100), resets));
        }

        return windows.Count == 0 ? null : new AgentLimits(AgentProviders.Codex, readAt, windows);
    }

    // Codex says how long a window is; the name follows from it.
    internal static string WindowLabel(long minutes) => minutes switch
    {
        <= 0 => "limit",
        10080 => "week",
        >= 40320 and <= 44640 => "month",
        _ when minutes % 1440 == 0 => $"{minutes / 1440} day",
        _ when minutes % 60 == 0 => $"{minutes / 60} hour",
        _ => $"{minutes} min",
    };
}
