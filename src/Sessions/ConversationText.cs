using System.IO;
using System.Text;
using System.Text.Json;

namespace Helide.Sessions;

/// <summary>
/// What was said in a conversation -- the prompts and the replies, without tool calls or
/// their output -- for searching past conversations by what is in them.
/// </summary>
/// <remarks>
/// Read from each agent's own record and kept in memory until that record changes: a
/// transcript file by its size and time, an OpenCode session by when it was last updated.
/// </remarks>
internal static class ConversationText
{
    private static readonly Dictionary<(string Agent, string Id), (string Stamp, string Text)> Cache = new();

    // Where each transcript is. Finding a Codex rollout means opening every rollout, so
    // it is done once; a file that has gone is looked for again.
    private static readonly Dictionary<(string Agent, string Id), string> Sources = new();
    private static readonly object Gate = new();

    /// <summary>The text, read now if it is not known or has changed; empty when unreadable.</summary>
    public static string Of(PastConversation conversation)
    {
        var key = (conversation.Agent, conversation.SessionId);
        var source = Source(conversation);
        var stamp = Stamp(conversation, source);

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var known) && known.Stamp == stamp)
                return known.Text;
        }

        string text;
        try
        {
            text = conversation.Agent switch
            {
                AgentProviders.Claude when source is not null => Claude(source),
                AgentProviders.Codex when source is not null => Codex(source),
                AgentProviders.OpenCode => OpenCode(conversation.SessionId),
                _ => string.Empty,
            };
        }
        catch (Exception)
        {
            text = string.Empty;
        }

        lock (Gate)
            Cache[key] = (stamp, text);

        return text;
    }

    /// <summary>Only what is already read; null for a conversation not read yet or changed since.</summary>
    public static string? Known(PastConversation conversation)
    {
        var stamp = Stamp(conversation, Source(conversation));
        lock (Gate)
            return Cache.TryGetValue((conversation.Agent, conversation.SessionId), out var known) && known.Stamp == stamp
                ? known.Text
                : null;
    }

    private static string? Source(PastConversation conversation)
    {
        if (conversation.File is { } file && File.Exists(file))
            return file;

        var key = (conversation.Agent, conversation.SessionId);
        lock (Gate)
        {
            if (Sources.TryGetValue(key, out var known) && File.Exists(known))
                return known;
        }

        var found = conversation.Agent switch
        {
            AgentProviders.Claude => ClaudeSessions.FindTranscriptPath(conversation.SessionId),
            AgentProviders.Codex => CodexSessions.FindRolloutPath(conversation.SessionId),
            _ => null,
        };

        if (found is not null)
        {
            lock (Gate)
                Sources[key] = found;
        }

        return found;
    }

    private static string Stamp(PastConversation conversation, string? source)
    {
        if (source is null)
            return conversation.LastUsed.UtcTicks.ToString();

        try
        {
            var info = new FileInfo(source);
            return info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    // ---- Claude: user and assistant records, their text blocks -------------------------

    private static string Claude(string transcript)
    {
        var said = new StringBuilder();
        foreach (var line in Lines(transcript))
        {
            // Most lines are tool calls and results; only these two kinds can hold words.
            if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
                && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
                continue;

            using var document = Parse(line);
            if (document is null)
                continue;

            var root = document.RootElement;
            if (Text(root, "type") is not ("user" or "assistant")
                || root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True
                || !root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
                continue;

            if (content.ValueKind == JsonValueKind.String)
                Add(said, content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (Text(block, "type") == "text")
                        Add(said, Text(block, "text"));
                }
            }
        }

        return said.ToString();
    }

    // ---- Codex: the messages of the conversation, without the context it is given ------

    private static string Codex(string rollout)
    {
        var said = new StringBuilder();
        foreach (var line in Lines(rollout))
        {
            if (!line.Contains("\"type\":\"message\"", StringComparison.Ordinal))
                continue;

            using var document = Parse(line);
            if (document is null
                || Text(document.RootElement, "type") != "response_item"
                || !document.RootElement.TryGetProperty("payload", out var payload)
                || Text(payload, "role") is not ("user" or "assistant")
                || !payload.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
                Add(said, Text(part, "text"));
        }

        return said.ToString();
    }

    // ---- OpenCode: both database layouts ------------------------------------------------

    private static string OpenCode(string sessionId)
    {
        var path = OpenCodeSessions.DatabasePath;
        if (!File.Exists(path))
            return string.Empty;

        var said = new StringBuilder();
        using var connection = OpenCodeSessions.Connect(path);

        // The current layout: a prompt's text on the message, a reply's in its content.
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT data FROM session_message
                WHERE session_id = $id AND type IN ('user', 'assistant')
                ORDER BY seq
                """;
            command.Parameters.AddWithValue("$id", sessionId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                using var document = Parse(reader.GetString(0));
                if (document is null)
                    continue;

                Add(said, Text(document.RootElement, "text"));
                if (document.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (Text(part, "type") == "text")
                            Add(said, Text(part, "text"));
                    }
                }
            }
        }
        catch (Exception)
        {
            // Not in this database.
        }

        if (said.Length > 0)
            return said.ToString();

        // The earlier layout, for a conversation the upgrade did not copy: text parts.
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT json_extract(data, '$.text') FROM part
                WHERE session_id = $id AND json_extract(data, '$.type') = 'text'
                ORDER BY time_created
                """;
            command.Parameters.AddWithValue("$id", sessionId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    Add(said, reader.GetString(0));
            }
        }
        catch (Exception)
        {
            // Not in this database either.
        }

        return said.ToString();
    }

    // ---- shared --------------------------------------------------------------------------

    // Context an agent is handed rather than anything said -- environment details,
    // instructions files, reminders -- comes wrapped in a tag; it is left out.
    private static void Add(StringBuilder said, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('<'))
            return;

        said.Append(text).Append('\n');
    }

    private static IEnumerable<string> Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static JsonDocument? Parse(string line)
    {
        try
        {
            return line.Length == 0 ? null : JsonDocument.Parse(line);
        }
        catch (JsonException)
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
