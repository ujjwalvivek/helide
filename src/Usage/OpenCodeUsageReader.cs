using System.IO;
using Helide.Sessions;
using Microsoft.Data.Sqlite;

namespace Helide.Usage;

/// <summary>
/// Adds up OpenCode's usage straight from its database.
/// </summary>
/// <remarks>
/// OpenCode stores every message with its token counts and, for paid providers, what it
/// was charged. There is nothing to read incrementally: the database answers each scope
/// in one query, and it is opened read-only like everywhere else Helide touches it.
/// <para>
/// It keeps conversations in two layouts. OpenCode 2 writes <c>session_v2</c> and
/// <c>session_message</c>; earlier versions wrote <c>session</c> and <c>message</c>, and
/// the upgrade copied only some of those across -- merging a turn's steps as it went, so
/// the two never agree row for row. A conversation is therefore read from the new tables
/// when it is there and from the old ones only when it is not, and counted once either
/// way.
/// </para>
/// <para>
/// A session started by another -- a subagent's task -- has a parent. Its tokens count
/// towards the conversation that started it, but the message that opened it was written
/// by the agent, not the person, so it is not a prompt.
/// </para>
/// </remarks>
internal static class OpenCodeUsageReader
{
    // An assistant step that never recorded its end, or ran for longer than this, is
    // counted as this long rather than as the hours it appears to span.
    private static readonly TimeSpan LongestStep = TimeSpan.FromMinutes(30);

    // Both layouts read into the same columns, so one loop adds them up.
    private const string CurrentQuery = """
        SELECT
            s.directory,
            s.parent_id,
            m.type,
            json_extract(m.data, '$.model.providerID'),
            json_extract(m.data, '$.model.id'),
            json_extract(m.data, '$.tokens.input'),
            json_extract(m.data, '$.tokens.output'),
            json_extract(m.data, '$.tokens.reasoning'),
            json_extract(m.data, '$.tokens.cache.read'),
            json_extract(m.data, '$.tokens.cache.write'),
            json_extract(m.data, '$.cost'),
            json_extract(m.data, '$.time.created'),
            json_extract(m.data, '$.time.completed')
        FROM session_message m
        JOIN session_v2 s ON s.id = m.session_id
        WHERE m.type IN ('user', 'assistant')
        """;

    private const string LegacyQuery = """
        SELECT
            s.directory,
            s.parent_id,
            json_extract(m.data, '$.role'),
            json_extract(m.data, '$.providerID'),
            json_extract(m.data, '$.modelID'),
            json_extract(m.data, '$.tokens.input'),
            json_extract(m.data, '$.tokens.output'),
            json_extract(m.data, '$.tokens.reasoning'),
            json_extract(m.data, '$.tokens.cache.read'),
            json_extract(m.data, '$.tokens.cache.write'),
            json_extract(m.data, '$.cost'),
            json_extract(m.data, '$.time.created'),
            json_extract(m.data, '$.time.completed')
        FROM message m
        JOIN session s ON s.id = m.session_id
        """;

    // Old rows of a conversation the upgrade already copied are the same turns again.
    private const string NotCopied = " m.session_id NOT IN (SELECT DISTINCT session_id FROM session_message)";

    /// <summary>
    /// OpenCode's usage within a scope, or null when there is no database to read.
    /// </summary>
    /// <param name="matchesDirectory">Whether a session's directory is in the scope.</param>
    /// <param name="sessionId">For the session scope: the conversation and its subagents.</param>
    /// <param name="scopeKey">Names the scope, so an unchanged database can answer from memory.</param>
    public static AgentUsage? Read(Func<string?, bool> matchesDirectory, string? sessionId, string scopeKey)
    {
        var path = OpenCodeSessions.DatabasePath;
        if (!File.Exists(path))
            return null;

        // The messages carry their whole text, so adding them up takes the better part of
        // a second on a large store. Nothing has changed while neither the database nor
        // its write-ahead log has, and then the last answer still stands.
        var stamp = Stamp(path);
        lock (Answers)
        {
            if (Answers.TryGetValue(scopeKey, out var known) && known.Stamp == stamp)
                return known.Usage;
        }

        var usage = ReadFresh(path, matchesDirectory, sessionId);

        lock (Answers)
            Answers[scopeKey] = (stamp, usage);

        return usage;
    }

    private static readonly Dictionary<string, (string Stamp, AgentUsage? Usage)> Answers = new();

    private static string Stamp(string path)
    {
        static string Of(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "-";
        }

        return Of(path) + "|" + Of(path + "-wal");
    }

    private static AgentUsage? ReadFresh(string path, Func<string?, bool> matchesDirectory, string? sessionId)
    {
        try
        {
            using var connection = OpenCodeSessions.Connect(path);
            var hasCurrent = HasTable(connection, "session_message") && HasTable(connection, "session_v2");
            var hasLegacy = HasTable(connection, "message") && HasTable(connection, "session");

            var usage = new AgentUsage { Agent = AgentProviders.OpenCode };
            var steps = 0L;

            if (hasCurrent)
                steps += Add(connection, CurrentQuery, sessionId, matchesDirectory, usage, legacy: false, hasCurrent);

            if (hasLegacy)
                steps += Add(connection, LegacyQuery, sessionId, matchesDirectory, usage, legacy: true, hasCurrent);

            usage.AgentTime = TimeSpan.FromMilliseconds(steps);
            return usage;
        }
        catch (Exception)
        {
            // A locked, migrated or half-written database is "no figures", not an error.
            return null;
        }
    }

    private static long Add(
        SqliteConnection connection,
        string query,
        string? sessionId,
        Func<string?, bool> matchesDirectory,
        AgentUsage usage,
        bool legacy,
        bool hasCurrent)
    {
        var conditions = new List<string>();
        if (legacy && hasCurrent)
            conditions.Add(NotCopied);
        if (sessionId is not null)
            conditions.Add(" (m.session_id = $id OR s.parent_id = $id)");

        using var command = connection.CreateCommand();
        command.CommandText = query
            + (conditions.Count == 0 ? string.Empty : (legacy ? " WHERE" : " AND") + string.Join(" AND", conditions));

        if (sessionId is not null)
            command.Parameters.AddWithValue("$id", sessionId);

        var steps = 0L;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var directory = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (sessionId is null && !matchesDirectory(directory))
                continue;

            var isChild = !reader.IsDBNull(1);
            var role = reader.IsDBNull(2) ? null : reader.GetString(2);

            if (role == "user")
            {
                if (!isChild)
                    usage.Prompts++;
                continue;
            }

            if (role != "assistant")
                continue;

            long Number(int column) => reader.IsDBNull(column) ? 0 : reader.GetInt64(column);

            var provider = reader.IsDBNull(3) ? "opencode" : reader.GetString(3);
            var model = reader.IsDBNull(4) ? "unknown" : reader.GetString(4);
            var cost = reader.IsDBNull(10) ? 0m : (decimal)reader.GetDouble(10);

            var counts = new TokenCounts
            {
                Input = Number(5),
                Output = Number(6) + Number(7),
                CacheRead = Number(8),
                CacheWrite = Number(9),
            };

            if (cost > 0)
            {
                counts.StoredCost = cost;
                counts.StoredCostTokens = counts.Total;
            }

            usage.Add(provider + "/" + model, counts);

            var created = Number(11);
            var completed = Number(12);
            if (created > 0 && completed > created)
                steps += Math.Min(completed - created, (long)LongestStep.TotalMilliseconds);
        }

        return steps;
    }

    /// <summary>What OpenCode's paid providers have charged, as far as its database says.</summary>
    /// <param name="Today">Since local midnight.</param>
    /// <param name="Month">Since the first of this month.</param>
    /// <param name="EverBilled">
    /// Whether anything was ever charged. Without it there is no paying account on record,
    /// and nothing to show but that.
    /// </param>
    internal sealed record Billing(decimal Today, decimal Month, bool EverBilled);

    /// <summary>OpenCode's spend, or null when there is no database.</summary>
    public static Billing? ReadBilling()
    {
        var path = OpenCodeSessions.DatabasePath;
        if (!File.Exists(path))
            return null;

        try
        {
            using var connection = OpenCodeSessions.Connect(path);
            var hasCurrent = HasTable(connection, "session_message");
            var hasLegacy = HasTable(connection, "message");

            var now = DateTimeOffset.Now;
            var today = new DateTimeOffset(now.Date, now.Offset);
            var month = new DateTimeOffset(new DateTime(now.Year, now.Month, 1), now.Offset);

            decimal Sum(DateTimeOffset since)
            {
                var total = 0m;
                if (hasCurrent)
                    total += Scalar(connection, """
                        SELECT COALESCE(SUM(json_extract(data, '$.cost')), 0) FROM session_message
                        WHERE time_created >= $since AND type = 'assistant'
                        """, since);
                if (hasLegacy)
                    total += Scalar(connection, """
                        SELECT COALESCE(SUM(json_extract(m.data, '$.cost')), 0) FROM message m
                        WHERE m.time_created >= $since AND json_extract(m.data, '$.role') = 'assistant'
                        """ + (hasCurrent ? " AND" + NotCopied : string.Empty), since);
                return total;
            }

            // The first charge on record says an account was used; when is not needed.
            var ever = (hasCurrent && Scalar(connection, """
                            SELECT COUNT(*) FROM (SELECT 1 FROM session_message
                            WHERE type = 'assistant' AND json_extract(data, '$.cost') > 0 LIMIT 1)
                            """, null) > 0)
                       || (hasLegacy && Scalar(connection, """
                            SELECT COUNT(*) FROM (SELECT 1 FROM message
                            WHERE json_extract(data, '$.cost') > 0 LIMIT 1)
                            """, null) > 0);

            return new Billing(Sum(today), Sum(month), ever);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static decimal Scalar(SqliteConnection connection, string sql, DateTimeOffset? since)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (since is { } at)
            command.Parameters.AddWithValue("$since", at.ToUnixTimeMilliseconds());
        return Convert.ToDecimal(command.ExecuteScalar() ?? 0m);
    }

    private static bool HasTable(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }
}
