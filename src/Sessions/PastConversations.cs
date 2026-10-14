using System.IO;
using System.Text.Json;
using Helide.Usage;

namespace Helide.Sessions;

/// <summary>One conversation an agent has on record, enough to name it and reopen it.</summary>
/// <param name="Agent">Which agent: it reopens in that one, never another.</param>
/// <param name="SessionId">The id the agent resumes it by.</param>
/// <param name="Title">Its name, or what was first asked in it.</param>
/// <param name="Directory">Where it was started, which is where it has to be reopened.</param>
internal sealed record PastConversation(string Agent, string SessionId, string Title, string Directory, DateTimeOffset LastUsed);

/// <summary>
/// The conversations all three agents have had in a project, from the indexes they keep.
/// </summary>
/// <remarks>
/// None of them is searched through: Claude writes every prompt with its project and
/// session to <c>history.jsonl</c>, Codex names its threads in <c>session_index.jsonl</c>
/// and opens each rollout with the folder it ran in, and OpenCode keeps a title and a
/// directory on every session row.
/// </remarks>
internal static class PastConversations
{
    private const int TitleLength = 80;

    public static IReadOnlyList<PastConversation> For(string project)
    {
        var all = new List<PastConversation>();

        if (AgentProviders.IsInstalled(AgentProviders.Claude))
            Try(() => all.AddRange(Claude(project)));
        if (AgentProviders.IsInstalled(AgentProviders.Codex))
            Try(() => all.AddRange(Codex(project)));
        if (AgentProviders.IsInstalled(AgentProviders.OpenCode))
            Try(() => all.AddRange(OpenCode(project)));

        return all.OrderByDescending(conversation => conversation.LastUsed).ToList();
    }

    private static void Try(Action read)
    {
        try
        {
            read();
        }
        catch (Exception)
        {
            // One agent's index being unreadable leaves the other two.
        }
    }

    private static IEnumerable<PastConversation> Claude(string project)
    {
        var file = Path.Combine(ClaudeSessions.ConfigDirectory, "history.jsonl");
        if (!File.Exists(file))
            return [];

        var sessions = new Dictionary<string, (string Title, string Directory, long Last)>();
        foreach (var line in File.ReadLines(file))
        {
            using var document = Parse(line);
            if (document is null)
                continue;

            var root = document.RootElement;
            if (Text(root, "sessionId") is not { } id
                || Text(root, "project") is not { } directory
                || !UsageLedger.IsWithin(directory, project))
                continue;

            var at = root.TryGetProperty("timestamp", out var stamp) && stamp.TryGetInt64(out var ms) ? ms : 0;
            var said = Text(root, "display") ?? string.Empty;
            // A slash command, a shell line, or a placeholder for something pasted is not
            // what the conversation was about.
            var asked = said.StartsWith('/') || said.StartsWith('!') || said.StartsWith("[Pasted") || said.StartsWith("[Image")
                ? string.Empty
                : said;

            sessions[id] = sessions.TryGetValue(id, out var known)
                ? (known.Title.Length > 0 ? known.Title : asked, known.Directory, Math.Max(known.Last, at))
                : (asked, directory, at);
        }

        // A conversation with no transcript left -- cleaned up by Claude -- cannot be resumed.
        var found = new List<PastConversation>();
        foreach (var (id, (title, directory, last)) in sessions)
        {
            if (ClaudeSessions.FindTranscriptPath(id) is not { } transcript)
                continue;

            found.Add(new PastConversation(
                AgentProviders.Claude, id, Shorten(ClaudeTitle(transcript, id) ?? title), directory,
                DateTimeOffset.FromUnixTimeMilliseconds(last)));
        }

        return found;
    }

    // The name a conversation was given -- by /rename, or by Claude itself -- kept beside
    // its transcript.
    private static string? ClaudeTitle(string transcript, string id)
    {
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(transcript)!, id, "custom-title.json");
            if (!File.Exists(file))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return Text(document.RootElement, "customTitle") is { Length: > 0 } title ? title : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<PastConversation> Codex(string project)
    {
        var names = new Dictionary<string, string>();
        var index = Path.Combine(CodexUsageReader.Home, "session_index.jsonl");
        if (File.Exists(index))
        {
            foreach (var line in File.ReadLines(index))
            {
                using var document = Parse(line);
                if (document is not null
                    && Text(document.RootElement, "id") is { } id
                    && Text(document.RootElement, "thread_name") is { Length: > 0 } name)
                    names[id] = name;
            }
        }

        var history = Path.Combine(CodexUsageReader.Home, "history.jsonl");
        if (File.Exists(history))
        {
            foreach (var line in File.ReadLines(history))
            {
                using var document = Parse(line);
                if (document is not null
                    && Text(document.RootElement, "session_id") is { } id
                    && Text(document.RootElement, "text") is { Length: > 0 } text
                    && !names.ContainsKey(id))
                    names[id] = text;
            }
        }

        // A resumed conversation has a rollout per sitting; the newest stands for it.
        var found = new Dictionary<string, PastConversation>();
        foreach (var rollout in CodexUsageReader.Files())
        {
            string? first;
            using (var reader = new StreamReader(new FileStream(rollout, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                first = reader.ReadLine();

            using var document = first is null ? null : Parse(first);
            if (document is null
                || !document.RootElement.TryGetProperty("payload", out var meta)
                || Text(meta, "cwd") is not { } directory
                || !UsageLedger.IsWithin(directory, project)
                || (Text(meta, "session_id") ?? Text(meta, "id")) is not { } id)
                continue;

            var last = new DateTimeOffset(File.GetLastWriteTimeUtc(rollout));
            if (found.TryGetValue(id, out var known) && known.LastUsed >= last)
                continue;

            found[id] = new PastConversation(
                AgentProviders.Codex, id, Shorten(names.GetValueOrDefault(id) ?? "Codex conversation"), directory, last);
        }

        return found.Values;
    }

    private static IEnumerable<PastConversation> OpenCode(string project)
    {
        var path = OpenCodeSessions.DatabasePath;
        if (!File.Exists(path))
            return [];

        using var connection = OpenCodeSessions.Connect(path);

        // Both layouts, the newer first: a session the upgrade copied is listed once.
        var found = new Dictionary<string, PastConversation>();
        foreach (var table in new[] { "session_v2", "session" })
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT id, title, directory, time_updated FROM {table} WHERE parent_id IS NULL";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var directory = reader.IsDBNull(2) ? null : reader.GetString(2);
                    if (found.ContainsKey(id) || !UsageLedger.IsWithin(directory, project))
                        continue;

                    var title = reader.IsDBNull(1) ? "OpenCode conversation" : reader.GetString(1);
                    var updated = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    found[id] = new PastConversation(
                        AgentProviders.OpenCode, id, Shorten(title), Path.GetFullPath(directory!),
                        DateTimeOffset.FromUnixTimeMilliseconds(updated));
                }
            }
            catch (Exception)
            {
                // That layout is not in this database.
            }
        }

        return found.Values;
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= TitleLength ? line : line[..(TitleLength - 1)] + "…";
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
