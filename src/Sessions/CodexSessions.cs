using System.IO;
using System.Text.Json;

namespace Helide.Sessions;

/// <summary>
/// Reports the session ids of codex rollout files that appeared while Helide was
/// running, so a pane can learn which conversation it ended up on.
/// </summary>
/// <remarks>
/// Codex writes <c>rollout-*.jsonl</c> under <c>~/.codex/sessions/YYYY/MM/DD/</c>,
/// and the first line is a <c>session_meta</c> record whose
/// <c>payload.session_id</c> is the id that <c>codex resume</c> takes.
/// <para>
/// The id has to come from that record, not from the filename. Resuming a session
/// writes a rollout whose name does not end in the session id -- the name carries
/// the resumed id alongside the new one -- so taking the last dash-separated
/// segment of the filename yields a fragment that <c>codex resume</c> rejects. Those
/// fragments were being saved as pane ids and came back as "unknown session id" on
/// the next launch, and the bad id was then saved again, so a pane could stay
/// broken across restarts.
/// </para>
/// <para>
/// The TUI has no queryable surface, so a file appearing is the only per-pane
/// signal there is. Which pane a file belongs to is left to the caller, which knows
/// when each pane started.
/// </para>
/// </remarks>
internal static class CodexSessions
{
    private static readonly string SessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex", "sessions");

    private static DateTime _snapshotTime = DateTime.UtcNow;

    // Files already handed to a pane. A file is only recorded once a pane has taken
    // it: marking it on enumeration would burn rollouts that appeared before any pane
    // was waiting, and those would then never be offered again -- not by the timer and
    // not by the final pass at close, which is the only other chance a slow pane gets.
    private static readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A rollout that appeared after <see cref="Initialize"/> was called.</summary>
    internal readonly record struct Rollout(string Path, string SessionId, DateTime CreatedUtc);

    /// <summary>
    /// Marks the current time; only files created after this are reported. Must be
    /// called before any pane is launched, since a pane's rollout file is written by
    /// the codex process a moment after it starts -- taking the snapshot afterwards
    /// would put it past those files and discard them for the whole session.
    /// </summary>
    public static void Initialize()
    {
        _snapshotTime = DateTime.UtcNow;
        _consumed.Clear();
    }

    /// <summary>
    /// Rollouts created since <see cref="Initialize"/>, oldest first, skipping any a
    /// pane has already taken. Call <see cref="MarkConsumed"/> for the ones used, or
    /// they stay available for a pane that is not waiting yet.
    /// </summary>
    public static IEnumerable<Rollout> NewRollouts()
    {
        if (!Directory.Exists(SessionsDir))
            yield break;

        string[] files;
        try
        {
            files = Directory.GetFiles(SessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var file in files.OrderBy(File.GetCreationTimeUtc))
        {
            var created = File.GetCreationTimeUtc(file);
            if (created <= _snapshotTime)
                continue;
            if (_consumed.Contains(file))
                continue;

            var id = ReadSessionId(file);

            // A null id is expected rather than exceptional: the TUI can be mid-write,
            // leaving the record incomplete for a moment. Leaving the file unconsumed
            // means the next poll retries it once the write settles.
            if (id is not null)
                yield return new Rollout(file, id, created);
        }
    }

    /// <summary>Records that a pane has taken this rollout, so it is not offered again.</summary>
    public static void MarkConsumed(Rollout rollout) => _consumed.Add(rollout.Path);

    // The session_meta record is the authority on the id. A file whose first line is
    // missing or unparseable is skipped rather than guessed at, because the two
    // outcomes are not equivalent: no id starts a fresh conversation, whereas a
    // guessed id resumes a thread the pane was never in.
    private static string? ReadSessionId(string file)
    {
        try
        {
            // Share the read with the writer. Codex holds its rollout file open for as
            // long as the TUI is up, so the default exclusive open throws and the file
            // reads as unparseable -- permanently, since the lock lasts the whole
            // session. Nothing is lost by sharing: we only read the first line, and a
            // torn read is caught by the parse below and retried on the next poll.
            string? first;
            using (var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                first = reader.ReadLine();
            }

            if (string.IsNullOrWhiteSpace(first))
                return null;

            using var document = JsonDocument.Parse(first);
            if (!document.RootElement.TryGetProperty("payload", out var payload))
                return null;

            // session_id is the field newer builds write; id is the older spelling and
            // the two agree where both are present.
            foreach (var name in new[] { "session_id", "id" })
            {
                if (!payload.TryGetProperty(name, out var value))
                    continue;
                var id = value.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
