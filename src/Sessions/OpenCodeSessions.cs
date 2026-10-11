using System.IO;
using System.Security.Cryptography;
using System.Text;
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
/// Reads session state out of the OpenCode database.
/// </summary>
/// <remarks>
/// This is the user's own store, shared with every other opencode on the machine, so every
/// query here is scoped to one session id the pane already holds. Nothing in this file may
/// read the newest session in the database and call it the pane's.
/// <para>
/// The pane holds that id from the start because Helide makes it up. <c>opencode -s</c>
/// continues the session with that id, or creates it under that id if there is none, so
/// the pane never has to find out afterwards which session the TUI made for it.
/// </para>
/// <para>
/// Only the columns status needs are read. The table is OpenCode's own and can gain columns
/// or be renamed by an upgrade, so the queries are kept to the minimum and any failure is
/// treated as "no status" rather than surfaced as an error.
/// </para>
/// </remarks>
internal static class OpenCodeSessions
{
    private const string IdAlphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static readonly object IdGate = new();
    private static long _lastIdTimestamp;
    private static int _idCounter;

    /// <summary>
    /// The user's own OpenCode database, honouring XDG_DATA_HOME the same way the agent
    /// does.
    /// </summary>
    public static string DatabasePath
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var data = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share")
                : xdg;

            return Path.Combine(data, "opencode", "opencode.db");
        }
    }

    /// <summary>
    /// A new session id, in the shape OpenCode gives its own.
    /// </summary>
    /// <remarks>
    /// <c>ses_</c>, then twelve hex digits holding the creation time inverted so that newer
    /// ids sort first, then fourteen random characters. The shape is copied rather than
    /// invented because nothing says the server accepts any other, and an id that looks
    /// like every other one cannot be treated differently by something downstream.
    /// </remarks>
    public static string NewId()
    {
        long stamp;

        lock (IdGate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now != _lastIdTimestamp)
            {
                _lastIdTimestamp = now;
                _idCounter = 0;
            }

            stamp = now * 0x1000 + ++_idCounter;
        }

        var inverted = ~stamp & 0xFFFF_FFFF_FFFF;

        var id = new StringBuilder("ses_", 30);
        id.Append(inverted.ToString("x12"));

        for (var index = 0; index < 14; index++)
            id.Append(IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)]);

        return id.ToString();
    }

    /// <summary>
    /// One session's row, or null when the database or the session is not there yet. A
    /// pane that has not been sent a message has no row: the TUI creates it on first use.
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