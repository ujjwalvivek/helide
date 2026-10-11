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
/// created and there is never anything to find out afterwards.
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

    private static string ConfigDirectory
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
