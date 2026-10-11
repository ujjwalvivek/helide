using System.IO;
using Microsoft.Data.Sqlite;

namespace Helide.Sessions;

/// <summary>
/// One row of an OpenCode pane's session table, reduced to what status needs.
/// </summary>
/// <param name="Id">The session id, which is what a resume is launched with.</param>
/// <param name="IdleAt">
/// When the last turn finished, or null while one is still running. OpenCode leaves this
/// null until the agent settles and stamps it alongside an outcome.
/// </param>
internal sealed record OpenCodeSessionRow(string Id, long? IdleAt);

/// <summary>
/// Reads session state out of an OpenCode pane's own database.
/// </summary>
/// <remarks>
/// Each pane owns its data directory, so this database contains that pane's conversations
/// and nothing else -- which is the whole reason status no longer has to guess which events
/// belonged to which pane.
/// <para>
/// Only the two columns status needs are read. The table is OpenCode's own and can gain
/// columns or be renamed by an upgrade, so the query is kept to the minimum and any failure
/// is treated as "no status" rather than surfaced as an error: a pane whose database cannot
/// be read still works, it just stops reporting.
/// </para>
/// </remarks>
internal static class OpenCodeSessions
{
    /// <summary>
    /// The sessions in a pane's database, most recently touched first.
    /// </summary>
    public static IReadOnlyList<OpenCodeSessionRow> Read(string databasePath)
    {
        if (!File.Exists(databasePath))
            return Array.Empty<OpenCodeSessionRow>();

        var rows = new List<OpenCodeSessionRow>();

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                // The agent writes continuously while a turn runs; a busy timeout keeps a
                // transient lock from turning into a thrown exception on a poll tick.
                DefaultTimeout = 1,
                Pooling = false,
            }.ToString());

        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, time_idle
            FROM session_v2
            ORDER BY time_updated DESC
            LIMIT 20
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
                continue;

            var id = reader.GetString(0);
            if (string.IsNullOrEmpty(id))
                continue;

            rows.Add(new OpenCodeSessionRow(id, reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        }

        return rows;
    }

    /// <summary>
    /// The most recently used session in a pane's database, which is the one a pane
    /// launched without a resume argument is on.
    /// </summary>
    public static string? Newest(string databasePath)
    {
        var rows = Read(databasePath);
        return rows.Count > 0 ? rows[0].Id : null;
    }
}