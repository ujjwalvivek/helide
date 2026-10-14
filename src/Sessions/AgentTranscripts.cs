using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helide.Sessions;

/// <summary>
/// What an agent did in its last turn, read from its own record of it.
/// </summary>
/// <remarks>
/// Each agent writes down the edits it makes as tool calls -- Claude's Edit and Write,
/// Codex's patches, OpenCode's edit and write steps -- so which files a turn changed is
/// a fact read back, not a guess from the file system, where your own saves and any
/// other agent's writes look the same. An edit made by running a shell command is not a
/// tool call of that kind and is not seen.
/// <para>
/// A turn is everything after the last thing the person typed. Every reader keeps
/// starting over at each prompt, so what is left at the end is the last turn.
/// </para>
/// </remarks>
internal static class AgentTranscripts
{
    /// <summary>The files the last turn edited, full paths, in the order first touched.</summary>
    public static IReadOnlyList<string> LastTurnFiles(string agent, string sessionId, string workingDirectory)
    {
        var files = new List<string>();

        try
        {
            switch (agent)
            {
                case AgentProviders.Claude when ClaudeSessions.FindTranscriptPath(sessionId) is { } claude:
                    ReadLines(claude, record => ClaudeTurn(record, files));
                    break;
                case AgentProviders.Codex when CodexSessions.FindRolloutPath(sessionId) is { } codex:
                    ReadLines(codex, record => CodexTurn(record, files));
                    break;
                case AgentProviders.OpenCode:
                    OpenCodeTurn(sessionId, files);
                    break;
            }
        }
        catch (Exception)
        {
            // A transcript mid-write or in a shape this does not know: nothing to list.
            return [];
        }

        return files
            .Select(path => Resolve(path, workingDirectory))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The text of the agent's last reply, or null.</summary>
    public static string? LastReply(string agent, string sessionId)
    {
        string? reply = null;

        try
        {
            switch (agent)
            {
                case AgentProviders.Claude when ClaudeSessions.FindTranscriptPath(sessionId) is { } claude:
                    ReadLines(claude, record => reply = ClaudeReply(record) ?? reply);
                    break;
                case AgentProviders.Codex when CodexSessions.FindRolloutPath(sessionId) is { } codex:
                    ReadLines(codex, record => reply = CodexReply(record) ?? reply);
                    break;
                case AgentProviders.OpenCode:
                    reply = OpenCodeReply(sessionId);
                    break;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(reply) ? null : reply;
    }

    // ---- Claude --------------------------------------------------------------------

    private static readonly string[] ClaudeEditTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    private static void ClaudeTurn(JsonElement record, List<string> files)
    {
        if (ClaudeSessions.ReadTurn(record) == TurnMark.Started)
        {
            files.Clear();
            return;
        }

        if (Text(record, "type") != "assistant" || !Content(record, out var content))
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (Text(block, "type") == "tool_use"
                && ClaudeEditTools.Contains(Text(block, "name"))
                && block.TryGetProperty("input", out var input)
                && (Text(input, "file_path") ?? Text(input, "notebook_path")) is { } path)
                files.Add(path);
        }
    }

    private static string? ClaudeReply(JsonElement record)
    {
        if (Text(record, "type") != "assistant" || !Content(record, out var content))
            return null;

        var text = string.Join("\n", content.EnumerateArray()
            .Where(block => Text(block, "type") == "text")
            .Select(block => Text(block, "text"))
            .OfType<string>());

        return text.Length == 0 ? null : text;
    }

    private static bool Content(JsonElement record, out JsonElement content)
    {
        content = default;
        return record.TryGetProperty("message", out var message)
               && message.ValueKind == JsonValueKind.Object
               && message.TryGetProperty("content", out content)
               && content.ValueKind == JsonValueKind.Array;
    }

    // ---- Codex ---------------------------------------------------------------------

    private static readonly Regex PatchFile = new(
        @"^\*\*\* (?:Update|Add|Delete) File: (?<path>.+?)\s*$|^\*\*\* Move to: (?<path>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static void CodexTurn(JsonElement record, List<string> files)
    {
        if (!record.TryGetProperty("payload", out var payload))
            return;

        // A turn the person started: one Codex started on its own carries on the last.
        if (Text(record, "type") == "event_msg" && Text(payload, "type") == "task_started")
        {
            if (!payload.TryGetProperty("turn_attribution", out var attribution)
                || Text(attribution, "turn_trigger") is null or "user")
                files.Clear();
            return;
        }

        if (Text(record, "type") != "response_item"
            || Text(payload, "name") != "apply_patch"
            || (Text(payload, "input") ?? Text(payload, "arguments")) is not { } patch)
            return;

        foreach (Match match in PatchFile.Matches(patch))
            files.Add(match.Groups["path"].Value);
    }

    private static string? CodexReply(JsonElement record)
    {
        if (Text(record, "type") != "response_item"
            || !record.TryGetProperty("payload", out var payload)
            || Text(payload, "type") != "message"
            || Text(payload, "role") != "assistant"
            || !payload.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return null;

        var text = string.Join("\n", content.EnumerateArray()
            .Select(part => Text(part, "text"))
            .OfType<string>());

        return text.Length == 0 ? null : text;
    }

    // ---- OpenCode ------------------------------------------------------------------

    private static readonly string[] OpenCodeEditTools = ["edit", "write", "patch", "multiedit"];

    private static IEnumerable<(string Type, JsonElement Data)> OpenCodeMessages(string sessionId)
    {
        var path = OpenCodeSessions.DatabasePath;
        if (!File.Exists(path))
            yield break;

        using var connection = OpenCodeSessions.Connect(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, data FROM session_message
            WHERE session_id = $id AND type IN ('user', 'assistant')
            ORDER BY seq
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            using var document = JsonDocument.Parse(reader.GetString(1));
            yield return (reader.GetString(0), document.RootElement.Clone());
        }
    }

    private static void OpenCodeTurn(string sessionId, List<string> files)
    {
        foreach (var (type, data) in OpenCodeMessages(sessionId))
        {
            if (type == "user")
            {
                files.Clear();
                continue;
            }

            if (!data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (Text(part, "type") != "tool"
                    || !OpenCodeEditTools.Contains(Text(part, "name"))
                    || !part.TryGetProperty("state", out var state)
                    || Text(state, "status") != "completed"
                    || !state.TryGetProperty("input", out var input))
                    continue;

                if ((Text(input, "path") ?? Text(input, "filePath") ?? Text(input, "file_path")) is { } path)
                    files.Add(path);
            }
        }
    }

    private static string? OpenCodeReply(string sessionId)
    {
        string? reply = null;
        foreach (var (type, data) in OpenCodeMessages(sessionId))
        {
            if (type != "assistant" || !data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            var text = string.Join("\n", content.EnumerateArray()
                .Where(part => Text(part, "type") == "text")
                .Select(part => Text(part, "text"))
                .OfType<string>());

            if (text.Length > 0)
                reply = text;
        }

        return reply;
    }

    // ---- shared --------------------------------------------------------------------

    private static void ReadLines(string path, Action<JsonElement> line)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } text)
        {
            if (text.Length == 0)
                continue;

            try
            {
                using var document = JsonDocument.Parse(text);
                line(document.RootElement);
            }
            catch (JsonException)
            {
                // The line still being written.
            }
        }
    }

    private static string? Resolve(string path, string workingDirectory)
    {
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
