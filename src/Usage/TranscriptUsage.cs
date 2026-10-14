using System.IO;
using System.Text;
using System.Text.Json;
using Helide.Sessions;

namespace Helide.Usage;

/// <summary>
/// What one transcript file has added up to so far, and where reading stopped.
/// </summary>
/// <remarks>
/// Transcripts only grow, so a file is read once and then only from <see cref="Offset"/>
/// on. Everything a reader needs to carry across that boundary -- the open turn, the last
/// cumulative total Codex reported, the message ids Claude repeats -- lives here, and the
/// whole record is saved between launches so a restart does not read gigabytes again.
/// </remarks>
internal sealed class FileUsage
{
    public string Agent { get; set; } = string.Empty;
    public long Length { get; set; }
    public long WriteTicks { get; set; }
    public long Offset { get; set; }

    public string? SessionId { get; set; }
    public string? Cwd { get; set; }

    public Dictionary<string, TokenCounts> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Prompts { get; set; }
    public double AgentSeconds { get; set; }

    // The turn being timed: open while TurnLast is set.
    public DateTimeOffset? TurnLast { get; set; }

    // Claude writes one line per content block and repeats the message's usage on each,
    // so usage is taken once per message id. They arrive together, so a short memory is
    // enough.
    public List<string> RecentMessageIds { get; set; } = [];

    // Codex: the model of the turn in progress, and the running total it last reported,
    // which every later report is measured from.
    public string? Model { get; set; }
    public CodexTotals? LastTotals { get; set; }

    public void AddTokens(string model, TokenCounts counts)
    {
        if (!Models.TryGetValue(model, out var existing))
            Models[model] = counts.Clone();
        else
            existing.Add(counts);
    }

    // Time between two events of a turn counts as the agent working, up to a point: a
    // turn left waiting on a permission prompt overnight is not eight hours of work.
    private static readonly TimeSpan LongestGap = TimeSpan.FromMinutes(5);

    public void BeginTurn(DateTimeOffset at) => TurnLast = at;

    public void Tick(DateTimeOffset at)
    {
        if (TurnLast is not { } last)
            return;

        var gap = at - last;
        if (gap > TimeSpan.Zero)
            AgentSeconds += Math.Min(gap.TotalSeconds, LongestGap.TotalSeconds);

        if (at > last)
            TurnLast = at;
    }

    public void EndTurn(DateTimeOffset at)
    {
        Tick(at);
        TurnLast = null;
    }
}

/// <summary>Codex's cumulative token figures, as it reports them.</summary>
internal sealed class CodexTotals
{
    public long Input { get; set; }
    public long Cached { get; set; }
    public long CacheWrite { get; set; }
    public long Output { get; set; }
}

/// <summary>
/// Reads a transcript from a byte offset, one complete line at a time.
/// </summary>
/// <remarks>
/// Codex rollouts run to hundreds of megabytes, almost all of it tool output and
/// reasoning that no figure here needs. Lines are handed over as bytes so a reader can
/// rule a line out with a substring check before paying for a JSON parse. A line still
/// being written -- no newline yet -- is left for the next read.
/// </remarks>
internal static class JsonlScanner
{
    /// <summary>Whether a line is worth parsing, judged from its bytes.</summary>
    public delegate bool LineFilter(ReadOnlySpan<byte> line);

    public static long Scan(string path, long offset, LineFilter wants, Action<JsonElement> line)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.SequentialScan);

        if (offset > stream.Length)
            offset = 0;

        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[1 << 20];
        var filled = 0;
        var consumed = offset;

        while (true)
        {
            if (filled == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);

            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
                break;

            filled += read;

            var start = 0;
            while (true)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', start, filled - start);
                if (newline < 0)
                    break;

                var span = new ReadOnlySpan<byte>(buffer, start, newline - start);
                if (span.Length > 0 && wants(span))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(buffer.AsMemory(start, newline - start));
                        line(document.RootElement);
                    }
                    catch (JsonException)
                    {
                        // A damaged line is skipped; the rest of the file is still good.
                    }
                }

                consumed += newline - start + 1;
                start = newline + 1;
            }

            Buffer.BlockCopy(buffer, start, buffer, 0, filled - start);
            filled -= start;
        }

        return consumed;
    }

    public static bool Contains(ReadOnlySpan<byte> line, ReadOnlySpan<byte> marker) => line.IndexOf(marker) >= 0;

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    public static DateTimeOffset? Timestamp(JsonElement element) =>
        Text(element, "timestamp") is { } text && DateTimeOffset.TryParse(text, out var at) ? at : null;
}

/// <summary>Claude Code's transcripts: <c>&lt;config&gt;/projects/&lt;dir&gt;/&lt;session&gt;.jsonl</c>.</summary>
internal static class ClaudeUsageReader
{
    private static readonly byte[] AssistantMarker = Encoding.ASCII.GetBytes("\"type\":\"assistant\"");
    private static readonly byte[] UserMarker = Encoding.ASCII.GetBytes("\"type\":\"user\"");

    /// <summary>
    /// Every transcript, with the session each belongs to. A subagent's transcript sits in
    /// a folder named after the conversation that started it, and is counted as part of it.
    /// </summary>
    public static IEnumerable<(string Path, string SessionId, bool Subagent)> Files()
    {
        var projects = Path.Combine(ClaudeSessions.ConfigDirectory, "projects");
        if (!Directory.Exists(projects))
            yield break;

        foreach (var project in SafeDirectories(projects))
        {
            foreach (var file in SafeFiles(project, "*.jsonl"))
                yield return (file, Path.GetFileNameWithoutExtension(file), false);

            foreach (var session in SafeDirectories(project))
            {
                var subagents = Path.Combine(session, "subagents");
                if (!Directory.Exists(subagents))
                    continue;

                foreach (var file in SafeFiles(subagents, "*.jsonl"))
                    yield return (file, Path.GetFileName(session), true);
            }
        }
    }

    public static void Read(string path, FileUsage usage, bool subagent)
    {
        usage.Offset = JsonlScanner.Scan(
            path,
            usage.Offset,
            line => JsonlScanner.Contains(line, AssistantMarker) || JsonlScanner.Contains(line, UserMarker),
            record => Apply(record, usage, subagent));
    }

    private static void Apply(JsonElement record, FileUsage usage, bool subagent)
    {
        var type = JsonlScanner.Text(record, "type");
        if (type is not ("assistant" or "user"))
            return;

        usage.Cwd ??= JsonlScanner.Text(record, "cwd");
        var at = JsonlScanner.Timestamp(record);

        if (type == "user")
        {
            if (subagent)
                return;

            if (IsPrompt(record))
            {
                usage.Prompts++;
                if (at is { } start)
                    usage.BeginTurn(start);
            }
            else if (at is { } now)
            {
                usage.Tick(now);
            }

            return;
        }

        if (!subagent && at is { } then)
            usage.Tick(then);

        if (!record.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("usage", out var counts)
            || counts.ValueKind != JsonValueKind.Object)
            return;

        if (JsonlScanner.Text(message, "id") is { } id)
        {
            if (usage.RecentMessageIds.Contains(id))
                return;

            usage.RecentMessageIds.Add(id);
            if (usage.RecentMessageIds.Count > 16)
                usage.RecentMessageIds.RemoveAt(0);
        }

        var model = JsonlScanner.Text(message, "model") ?? "claude";

        // Claude's own placeholder for a turn that never reached the API.
        if (model == "<synthetic>")
            return;

        usage.AddTokens(model, new TokenCounts
        {
            Input = JsonlScanner.Number(counts, "input_tokens"),
            Output = JsonlScanner.Number(counts, "output_tokens"),
            CacheRead = JsonlScanner.Number(counts, "cache_read_input_tokens"),
            CacheWrite = JsonlScanner.Number(counts, "cache_creation_input_tokens"),
        });
    }

    // Something the person typed, as opposed to a tool result coming back, a meta line
    // Claude injects, or the output of a local command echoed into the conversation.
    private static bool IsPrompt(JsonElement record)
    {
        if (record.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return false;

        if (record.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
            return false;

        if (!record.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
            return false;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? string.Empty;
            return !text.StartsWith("<local-command-stdout>", StringComparison.Ordinal)
                   && !text.StartsWith("[Request interrupted", StringComparison.Ordinal);
        }

        if (content.ValueKind != JsonValueKind.Array)
            return false;

        var said = false;
        foreach (var block in content.EnumerateArray())
        {
            switch (JsonlScanner.Text(block, "type"))
            {
                case "tool_result":
                    return false;
                case "text":
                    if (JsonlScanner.Text(block, "text") is { } text
                        && text.StartsWith("[Request interrupted", StringComparison.Ordinal))
                        return false;
                    said = true;
                    break;
                case "image":
                    said = true;
                    break;
            }
        }

        return said;
    }

    internal static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal static IEnumerable<string> SafeFiles(string path, string pattern, bool recursive = false)
    {
        try
        {
            return Directory.EnumerateFiles(
                path, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }
}

/// <summary>Codex's rollouts: <c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>.</summary>
internal static class CodexUsageReader
{
    // The record kinds this reads. They are named in the first few dozen bytes of a line,
    // ahead of the payload, so a line can be passed over without looking at the rest.
    private static readonly byte[][] Markers =
    [
        .. new[]
        {
            "\"session_meta\"", "\"turn_context\"", "\"token_count\"",
            "\"task_started\"", "\"task_complete\"", "\"turn_aborted\"",
        }.Select(Encoding.ASCII.GetBytes),
    ];

    private const int HeadLength = 256;

    public static string Home
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : configured;
        }
    }

    /// <summary>Live rollouts and archived ones; an archived conversation was still used.</summary>
    public static IEnumerable<string> Files()
    {
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(Home, folder);
            if (Directory.Exists(root))
            {
                foreach (var file in ClaudeUsageReader.SafeFiles(root, "rollout-*.jsonl", recursive: true))
                    yield return file;
            }
        }
    }

    public static void Read(string path, FileUsage usage)
    {
        usage.Offset = JsonlScanner.Scan(path, usage.Offset, Wants, record => Apply(record, usage));
    }

    private static bool Wants(ReadOnlySpan<byte> line)
    {
        var head = line.Length > HeadLength ? line[..HeadLength] : line;
        foreach (var marker in Markers)
        {
            if (head.IndexOf(marker) >= 0)
                return true;
        }

        return false;
    }

    private static void Apply(JsonElement record, FileUsage usage)
    {
        if (!record.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return;

        var at = JsonlScanner.Timestamp(record);

        switch (JsonlScanner.Text(record, "type"))
        {
            case "session_meta":
                // session_id is the conversation a resumed or forked rollout continues; it
                // is also what Helide saves on a Codex pane.
                usage.SessionId ??= JsonlScanner.Text(payload, "session_id") ?? JsonlScanner.Text(payload, "id");
                usage.Cwd ??= JsonlScanner.Text(payload, "cwd");
                return;

            case "turn_context":
                usage.Model = JsonlScanner.Text(payload, "model") ?? usage.Model;
                usage.Cwd ??= JsonlScanner.Text(payload, "cwd");
                return;

            case "event_msg":
                break;

            default:
                return;
        }

        switch (JsonlScanner.Text(payload, "type"))
        {
            case "task_started":
                if (at is { } start)
                    usage.BeginTurn(start);

                if (!payload.TryGetProperty("turn_attribution", out var attribution)
                    || JsonlScanner.Text(attribution, "turn_trigger") is null or "user")
                    usage.Prompts++;
                break;

            case "task_complete":
            case "turn_aborted":
                if (at is { } end)
                    usage.EndTurn(end);
                break;

            case "token_count":
                if (at is { } now)
                    usage.Tick(now);

                if (payload.TryGetProperty("info", out var info)
                    && info.ValueKind == JsonValueKind.Object
                    && info.TryGetProperty("total_token_usage", out var total)
                    && total.ValueKind == JsonValueKind.Object)
                    AddTotal(usage, total);
                break;
        }
    }

    // Codex reports a running total for the rollout. What a report adds is its difference
    // from the one before, charged to the model of the turn it arrived in. A total that
    // went down means the count started over, so the new figure is all new.
    private static void AddTotal(FileUsage usage, JsonElement total)
    {
        var now = new CodexTotals
        {
            Input = JsonlScanner.Number(total, "input_tokens"),
            Cached = JsonlScanner.Number(total, "cached_input_tokens"),
            CacheWrite = JsonlScanner.Number(total, "cache_write_input_tokens"),
            Output = JsonlScanner.Number(total, "output_tokens"),
        };

        var before = usage.LastTotals;
        if (before is not null && (now.Input < before.Input || now.Output < before.Output))
            before = null;

        long Delta(long current, long previous) => Math.Max(0, current - previous);

        var input = Delta(now.Input, before?.Input ?? 0);
        var cached = Delta(now.Cached, before?.Cached ?? 0);
        var written = Delta(now.CacheWrite, before?.CacheWrite ?? 0);
        var output = Delta(now.Output, before?.Output ?? 0);

        usage.LastTotals = now;

        if (input + output == 0)
            return;

        // Codex's input figure includes what was served from cache.
        usage.AddTokens(usage.Model ?? "codex", new TokenCounts
        {
            Input = Math.Max(0, input - cached - written),
            CacheRead = cached,
            CacheWrite = written,
            Output = output,
        });
    }
}
