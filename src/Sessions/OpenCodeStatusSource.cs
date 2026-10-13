using System.Windows.Threading;

namespace Helide.Sessions;

/// <summary>
/// Reports when an OpenCode pane finishes a turn.
/// </summary>
/// <remarks>
/// Helide shares the user's opencode store, so this database holds every session on the
/// machine rather than just this pane's. The pane is told which one is its own before it
/// starts: Helide makes up the session id and launches the TUI onto it, and the TUI
/// creates the session under that id the first time it is used. So there is one id to
/// watch, known from the start, and nothing here has to work out which session belongs
/// to which pane.
/// <para>
/// Until the pane is sent a message there is no row for that id, which is read as "nothing
/// to report yet" rather than as a failure.
/// </para>
/// <para>
/// A turn in progress has no <c>time_idle</c>; finishing sets it. The transition from "no
/// idle time" to "has one" is when the agent stopped, which is when a backgrounded pane is
/// worth marking. The database is in WAL mode, so these reads never contend with the agent
/// writing to it.
/// </para>
/// <para>
/// The timer runs on the dispatcher but each read runs on the thread pool, and only the
/// <see cref="BecameIdle"/> raise comes back.
/// </para>
/// </remarks>
internal sealed class OpenCodeStatusSource : ISessionStatusSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private readonly string _databasePath;
    private readonly string _sessionId;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    // Only ever touched by the read in flight, and _reading allows one at a time.
    private long? _observedIdleAt;
    private bool _baselineTaken;
    private bool _sawRunning;
    private bool _reportedBusy;

    private int _reading;
    private volatile bool _disposed;

    public OpenCodeStatusSource(string databasePath, string sessionId, Dispatcher dispatcher)
    {
        _databasePath = databasePath;
        _sessionId = sessionId;
        _dispatcher = dispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = PollInterval,
        };

        _timer.Tick += (_, _) => Poll();
    }

    public bool IsBusy => false;

    public event Action? BecameIdle;

    public event Action<bool>? BusyChanged;

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }

    // The timer ticks on the dispatcher, and the read does not belong there: it opens a
    // connection to a database that holds every session on the machine, and that can
    // take long enough to be felt as a stutter in every pane at once. A read still
    // running when the next tick comes round is left to finish rather than joined by a
    // second one.
    private void Poll()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1)
            return;

        Task.Run(() =>
        {
            try
            {
                Read();
            }
            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        });
    }

    // Called from the read, which is on the thread pool; the change goes back to the
    // dispatcher so the tab is only ever touched from the UI thread.
    private void ReportBusy(bool busy)
    {
        if (busy == _reportedBusy)
            return;

        _reportedBusy = busy;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
                BusyChanged?.Invoke(busy);
        }));
    }

    private void Read()
    {
        OpenCodeSessionRow? session;

        try
        {
            session = OpenCodeSessions.Read(_databasePath, _sessionId);
        }
        catch (Exception)
        {
            // The database is missing, locked or mid-migration. The pane keeps working
            // and simply reports no status.
            return;
        }

        // No row yet: the pane has not been sent a message, so the TUI has not created
        // the session.
        if (session is null)
            return;

        // First sighting of this row. Whatever state it is in now is a starting point,
        // not an event: a pane restored onto a conversation that finished before
        // Helide was launched must not light up, or every restored tab would come back
        // marked. Record it and wait for something to actually change.
        if (!_baselineTaken)
        {
            _baselineTaken = true;
            _observedIdleAt = session.IdleAt;
            _sawRunning = session.IdleAt is null;
            ReportBusy(_sawRunning);
            return;
        }

        // No idle timestamp means a turn is in progress.
        if (session.IdleAt is null)
        {
            _observedIdleAt = null;
            _sawRunning = true;
            ReportBusy(true);
            return;
        }

        ReportBusy(false);

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

        // A pane closed while its last read was in flight has nobody left to tell.
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
                BecameIdle?.Invoke();
        }));
    }
}
