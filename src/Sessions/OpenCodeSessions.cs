using System.IO;
using Microsoft.Data.Sqlite;

namespace Helide.Sessions;

/// <summary>
/// One row of an OpenCode session, reduced to what status needs.
/// </summary>
/// <param name="Id">The session id.</param>
/// <param name="IdleAt">
/// When the last turn finished, or null while one is still running. OpenCode leaves this
/// null until the agent settles and stamps it alongside an outcome.
/// </param>
internal sealed record OpenCodeSessionRow(string Id, long? IdleAt);

/// <summary>
/// A session together with the project directory it belongs to.
/// </summary>
/// <param name="Id">The session id.</param>
/// <param name="IdleAt">When the last turn finished, or null while one is running.</param>
/// <param name="Directory">The project directory the session belongs to.</param>
/// <param name="CreatedAt">When the session was created, in Unix milliseconds.</param>
internal sealed record OpenCodeSession(string Id, long? IdleAt, string Directory, long CreatedAt);

/// <summary>
/// Reads session state out of the OpenCode database.
/// </summary>
/// <remarks>
/// This is the user's own store, shared with every other opencode on the machine, so every
/// query here is scoped to one session id the pane already holds. Nothing in this file may
/// read the newest session in the database and call it the pane's; the single exception is
/// <see cref="ReadCreatedIn"/>, which is filtered to one project and one start time, and
/// exists only to adopt a session the TUI has just created for itself.
/// <para>
/// Only the columns status needs are read. The table is OpenCode's own and can gain columns
/// or be renamed by an upgrade, so the queries are kept to the minimum and any failure is
/// treated as "no status" rather than surfaced as an error.
/// </para>
/// </remarks>
internal static class OpenCodeSessions
{
    /// <summary>
    /// One session's row, or null when the database or the session is not there yet.
    /// </summary>
    public static OpenCodeSessionRow? Read(string databasePath, string sessionId)
    {
        if (!File.Exists(databasePath))
            return null;

        using var connection = Connect(databasePath);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, time_idle
            FROM session_v2
            WHERE id = $id
            LIMIT 1
            """;

        command.Parameters.AddWithValue("$id", sessionId);

        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
            return null;

        return new OpenCodeSessionRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    /// <summary>
    /// Sessions created in one project directory since a given moment, newest first.
    /// </summary>
    /// <remarks>
    /// Used to adopt the session a freshly launched TUI creates for itself. This cannot be
    /// a bare "newest session overall" query: the store holds every session on the machine,
    /// and the newest of those belongs to whatever else the user is doing. The directory
    /// filter and the start time are what make the answer this pane's.
    /// </remarks>
    public static IReadOnlyList<OpenCodeSession> ReadCreatedIn(
        string databasePath,
        string directory,
        DateTime createdAfterUtc)
    {
        if (!File.Exists(databasePath))
            return Array.Empty<OpenCodeSession>();

        using var connection = Connect(databasePath);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.time_idle, s.time_created, pd.directory
            FROM session_v2 s
            JOIN project_directory pd ON pd.project_id = s.project_id
            WHERE s.time_created >= $after
            ORDER BY s.time_created DESC
            LIMIT 20
            """;

        command.Parameters.AddWithValue(
            "$after", new DateTimeOffset(createdAfterUtc).ToUnixTimeMilliseconds());

        var results = new List<OpenCodeSession>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(3))
                continue;

            var path = reader.GetString(3);

            // The same path is stored with either separator depending on how it was
            // written, so both forms are compared rather than guessing which one is in use.
            if (!string.Equals(
                    path.Replace('/', '\\'),
                    directory.Replace('/', '\\'),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new OpenCodeSession(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                path,
                reader.GetInt64(2)));
        }

        return results;
    }

    private static SqliteConnection Connect(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                // The agent writes continuously while a turn runs; a short busy timeout
                // keeps a transient lock from becoming a thrown exception on a poll tick.
                DefaultTimeout = 1,
                Pooling = false,
            }.ToString());

        connection.Open();
        return connection;
    }
}