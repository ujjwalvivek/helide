using System.Windows.Threading;

namespace Helide.Sessions;

/// <summary>
/// Reports whether an OpenCode pane is mid-turn, by watching the session rows in that
/// pane's own database.
/// </summary>
/// <remarks>
/// Each pane owns its data directory, so its database holds its sessions and nobody
/// else's. That makes the question Helide actually asks -- is <i>this</i> conversation
/// still going -- a lookup in one known file rather than a guess about which events on a
/// shared stream belonged to this pane. A pane resumed with a known id asks about that id;
/// otherwise it watches the most recently touched session in its own database, which is
/// necessarily its own because no other pane can write here.
/// <para>
/// A turn in progress has no <c>time_idle</c>; finishing sets it, along with an
/// <c>idle_outcome</c>. The transition from "no idle time" to "has one" is therefore the
/// moment the agent stopped, which is exactly when a backgrounded pane is worth marking.
/// The database is in WAL mode, so these reads do not contend with the agent's writes.
/// </para>
/// </remarks>
internal sealed class OpenCodeDatabaseStatusSource : ISessionStatusSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private readonly string _databasePath;
    private readonly string? _pinnedSessionId;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private long? _observedIdleAt;
    private string? _sessionId;
    private string? _baselineSessionId;
    private bool _sawRunning;

    public OpenCodeDatabaseStatusSource(
        string databasePath,
        string? pinnedSessionId,
        Dispatcher dispatcher)
    {
        _databasePath = databasePath;
        _pinnedSessionId = pinnedSessionId;
        _dispatcher = dispatcher;

        // A pane resumed with an id already knows which conversation it is on; a fresh one
        // has to be told which session to follow once its database produces one.
        _sessionId = pinnedSessionId;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = PollInterval,
        };

        _timer.Tick += (_, _) => Poll();
    }

    public bool IsBusy => false;

    /// <summary>
    /// The conversation this pane is on. A pane resumed with an id knows it from the
    /// start; a fresh one learns it from the first session to appear in its own database,
    /// which no other pane can have written.
    /// </summary>
    public string? SessionId => _sessionId;

    public event Action? BecameIdle;

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    private void Poll()
    {
        try
        {
            // OpenCode creates the database on the pane's first turn, so for the first
            // moments of a fresh pane there is genuinely nothing to read yet.
            var rows = OpenCodeSessions.Read(_databasePath);
            if (rows.Count == 0)
                return;

            // A resumed pane watches its own thread. A fresh one has no id yet, so
            // the newest session in its private database is necessarily its own.
            var session = _sessionId is { Length: > 0 } known
                ? rows.FirstOrDefault(row => row.Id == known)
                : rows[0];

            if (session is null)
                return;

            _sessionId ??= session.Id;

            // First sighting of this conversation. Whatever state it is in now is a
            // starting point, not an event: a pane restored onto a session that finished
            // before Helide was launched must not light up, or every restored tab would
            // come back marked. Record it and wait for something to actually change.
            if (_baselineSessionId != session.Id)
            {
                _baselineSessionId = session.Id;
                _observedIdleAt = session.IdleAt;
                _sawRunning = false;
                return;
            }

            // No idle timestamp means a turn is in progress.
            if (session.IdleAt is null)
            {
                _observedIdleAt = null;
                _sawRunning = true;
                return;
            }

            // Only the arrival of an idle timestamp is news, so the same finished turn is
            // not reported on every poll after it.
            if (_observedIdleAt == session.IdleAt)
                return;

            var justFinished = _sawRunning;
            _observedIdleAt = session.IdleAt;
            _sawRunning = false;

            // A turn that was already idle when we started watching did not finish while
            // anyone was away. Only report a turn that was running and then stopped.
            if (!justFinished)
                return;

            RaiseBecameIdle();
        }
        catch (Exception)
        {
            // The database is missing, locked or mid-migration. The pane keeps working
            // and simply reports no status, which is what the quiet-period fallback is for.
        }
    }

    private void RaiseBecameIdle()
        => _dispatcher.BeginInvoke(new Action(() => BecameIdle?.Invoke()));
}