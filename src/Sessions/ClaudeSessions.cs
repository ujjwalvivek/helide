using System.IO;
using System.Text.Json;

namespace Helide.Sessions;

/// <summary>
/// Knows where Claude Code keeps a conversation and how to read turn boundaries out of it.
/// </summary>
/// <remarks>
/// Claude takes the id of the session it should use: <c>claude --session-id &lt;uuid&gt;</c>
/// starts a conversation under that id, and <c>claude --resume &lt;uuid&gt;</c> continues it
/// under the same one. So, as with OpenCode, Helide makes the id up when the pane is
/// created. It is where the pane starts, not necessarily where it stays: <c>/resume</c>
/// and <c>/clear</c> move the process onto another conversation, which is why
/// <see cref="LiveSessionId"/> exists.
/// <para>
/// The two flags are not interchangeable. <c>--resume</c> exits with "No conversation
/// found" for an id that has none, and a pane that was opened but never typed into has
/// exactly that: an id and no conversation. So whether the transcript exists is what
/// decides which flag a pane is launched with -- <c>--resume</c> when there is one to
/// continue, <c>--session-id</c> when it has yet to be started.
/// </para>
/// <para>
/// Transcripts are <c>&lt;config&gt;/projects/&lt;directory&gt;/&lt;id&gt;.jsonl</c>, where the
/// directory part is the working directory with its separators flattened. The file is
/// looked up by id across every project folder rather than by rebuilding that name, so
/// nothing here depends on how the flattening is done.
/// </para>
/// </remarks>
internal static class ClaudeSessions
{
    /// <summary>A new session id. Claude requires a UUID.</summary>
    public static string NewId() => Guid.NewGuid().ToString();

    /// <summary>
    /// The transcript for a session, or null when the session has not been used yet.
    /// </summary>
    public static string? FindTranscriptPath(string sessionId)
    {
        // The id becomes a file name, so anything that is not a plain UUID is refused
        // rather than passed to the file system as a pattern.
        if (!Guid.TryParse(sessionId, out _))
            return null;

        try
        {
            var projects = Path.Combine(ConfigDirectory, "projects");
            if (!Directory.Exists(projects))
                return null;

            foreach (var project in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(project, sessionId + ".jsonl");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception)
        {
            // An unreadable folder is treated as "not found".
        }

        return null;
    }

    /// <summary>
    /// The conversation a running Claude is in right now, or null when none of the given
    /// processes is a Claude that has said.
    /// </summary>
    /// <remarks>
    /// The id Helide hands Claude at launch is only where it starts. <c>/resume</c> and
    /// <c>/clear</c> move the same process onto another conversation, and from then on
    /// the id Helide made up names a transcript that is empty, or holds nothing but a
    /// cancelled <c>/resume</c>. Reopening that is "Resume cancelled" and the real
    /// conversation is somewhere else.
    /// <para>
    /// Claude says where it is: each running process keeps
    /// <c>&lt;config&gt;/sessions/&lt;pid&gt;.json</c> with its current <c>sessionId</c>,
    /// rewritten when the conversation changes and removed when it exits.
    /// </para>
    /// </remarks>
    public static string? LiveSessionId(IReadOnlyCollection<int> processIds)
    {
        string? found = null;
        var foundAt = long.MinValue;

        try
        {
            var folder = Path.Combine(ConfigDirectory, "sessions");
            if (!Directory.Exists(folder))
                return null;

            foreach (var pid in processIds)
            {
                var file = Path.Combine(folder, pid + ".json");
                if (!File.Exists(file))
                    continue;

                try
                {
                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    var root = document.RootElement;

                    if (!root.TryGetProperty("sessionId", out var idElement)
                        || idElement.GetString() is not { } id
                        || !Guid.TryParse(id, out _))
                        continue;

                    // A conversation that was moved to the background leaves this process
                    // on a stub that only says where it went. The stub's id names a
                    // transcript of "/resume" lines; the job it is parked on holds the
                    // conversation, so that is the id worth keeping.
                    if (Text(root, "parkedJobId") is { } parked
                        && ListBackgroundJobs().FirstOrDefault(job => job.JobId == parked) is { } target)
                        id = target.SessionId;

                    // Several processes in one chain can all be there; the most recently
                    // started is the one the user is in.
                    var startedAt = root.TryGetProperty("startedAt", out var started)
                        && started.ValueKind == JsonValueKind.Number
                        && started.TryGetInt64(out var value)
                        ? value
                        : 0;

                    if (found is null || startedAt >= foundAt)
                    {
                        found = id;
                        foundAt = startedAt;
                    }
                }
                catch (Exception)
                {
                    // Being rewritten right now, or gone: the next poll will see it.
                }
            }
        }
        catch (Exception)
        {
            // An unreadable folder says nothing.
        }

        return found;
    }

    /// <summary>A Claude session that is running in the background, and how to reach it.</summary>
    internal sealed record BackgroundJob(string JobId, string Name, string SessionId, string Status);

    /// <summary>
    /// The background sessions that are running now, optionally only those started in a
    /// given directory.
    /// </summary>
    /// <remarks>
    /// A conversation moved to the background keeps running in a process of its own, and
    /// <c>--resume</c> cannot take it over: Claude opens another copy and says so. The way
    /// to see it is <c>claude attach &lt;job id&gt;</c>. The same
    /// <c>sessions/&lt;pid&gt;.json</c> files <see cref="LiveSessionId"/> reads say which
    /// ones exist, with <c>kind</c> set to <c>bg</c>.
    /// </remarks>
    public static IReadOnlyList<BackgroundJob> ListBackgroundJobs(string? workingDirectory = null)
    {
        var jobs = new List<BackgroundJob>();

        try
        {
            var folder = Path.Combine(ConfigDirectory, "sessions");
            if (!Directory.Exists(folder))
                return jobs;

            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    using var stream = new FileStream(
                        file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    var root = document.RootElement;

                    if (Text(root, "kind") != "bg"
                        || Text(root, "sessionId") is not { } sessionId
                        || Text(root, "jobId") is not { } jobId
                        || !root.TryGetProperty("pid", out var pidElement)
                        || !pidElement.TryGetInt32(out var pid)
                        || !IsRunning(pid))
                        continue;

                    if (workingDirectory is not null
                        && !string.Equals(
                            Text(root, "cwd")?.TrimEnd('\\', '/'),
                            workingDirectory.TrimEnd('\\', '/'),
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    jobs.Add(new BackgroundJob(
                        jobId,
                        Text(root, "name") ?? jobId,
                        sessionId,
                        Text(root, "status") ?? string.Empty));
                }
                catch (Exception)
                {
                    // Being rewritten, or gone.
                }
            }
        }
        catch (Exception)
        {
            // An unreadable folder lists nothing.
        }

        return jobs;
    }

    /// <summary>
    /// The job id to attach to when this session is running in the background, or null
    /// when it is an ordinary conversation that can be resumed.
    /// </summary>
    public static string? FindBackgroundJobId(string sessionId) =>
        ListBackgroundJobs().FirstOrDefault(job =>
            string.Equals(job.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))?.JobId;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    // A session file outlives a process that was killed instead of closed.
    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// What a transcript record says about its turn.
    /// </summary>
    /// <remarks>
    /// A turn opens with a <c>user</c> record carrying the prompt and closes with an
    /// <c>assistant</c> record whose <c>stop_reason</c> is anything other than
    /// <c>tool_use</c>. In between, every tool call is an assistant record that stops for
    /// <c>tool_use</c> followed by a user record carrying only the <c>tool_result</c>;
    /// those are the turn continuing, not a new one starting.
    /// </remarks>
    public static TurnMark ReadTurn(JsonElement record)
    {
        if (!record.TryGetProperty("type", out var typeElement))
            return TurnMark.None;

        // A subagent's records are its own conversation, not the pane's turn.
        if (record.TryGetProperty("isSidechain", out var sidechain)
            && sidechain.ValueKind == JsonValueKind.True)
            return TurnMark.None;

        if (!record.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
            return TurnMark.None;

        switch (typeElement.GetString())
        {
            case "assistant":
                if (!message.TryGetProperty("stop_reason", out var stop)
                    || stop.ValueKind != JsonValueKind.String)
                    return TurnMark.None;

                return stop.GetString() is "tool_use" or "pause_turn"
                    ? TurnMark.None
                    : TurnMark.Finished;

            case "user":
                // Written by Claude itself around commands and attachments, not typed.
                if (record.TryGetProperty("isMeta", out var meta)
                    && meta.ValueKind == JsonValueKind.True)
                    return TurnMark.None;

                if (!message.TryGetProperty("content", out var content))
                    return TurnMark.None;

                if (content.ValueKind == JsonValueKind.String)
                {
                    var typed = content.GetString();
                    if (IsInterruption(typed))
                        return TurnMark.Finished;

                    return IsLocalCommand(typed) ? TurnMark.None : TurnMark.Started;
                }

                if (content.ValueKind != JsonValueKind.Array)
                    return TurnMark.None;

                var prompt = false;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object
                        || !block.TryGetProperty("type", out var blockType))
                        continue;

                    var kind = blockType.GetString();
                    if (kind == "tool_result")
                        continue;

                    if (kind == "text"
                        && block.TryGetProperty("text", out var text)
                        && IsInterruption(text.GetString()))
                        return TurnMark.Finished;

                    prompt = true;
                }

                return prompt ? TurnMark.Started : TurnMark.None;

            default:
                return TurnMark.None;
        }
    }

    // Pressing Esc mid-turn leaves no closing assistant record. Claude writes this note
    // into the transcript instead, and it is the only sign the turn is over.
    private static bool IsInterruption(string? text) =>
        text is not null
        && text.StartsWith("[Request interrupted", StringComparison.Ordinal);

    // A slash command or a ! shell line is recorded as a user message too, wrapped in a
    // tag, but Claude runs it itself and no reply follows -- so it opens no turn.
    private static bool IsLocalCommand(string? text) =>
        text is not null
        && (text.StartsWith("<command-", StringComparison.Ordinal)
            || text.StartsWith("<local-command", StringComparison.Ordinal)
            || text.StartsWith("<bash-", StringComparison.Ordinal));

    internal static string ConfigDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude")
                : configured;
        }
    }
}
