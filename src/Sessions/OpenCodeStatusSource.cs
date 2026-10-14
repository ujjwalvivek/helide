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
/// Each turn ends by stamping a new <c>time_idle</c>, so a stamp that changes is the agent
/// stopping, which is when a backgrounded pane is worth marking. The stamp is not cleared
/// while the next turn runs; a turn in progress is a message newer than the last idle one
/// (<see cref="OpenCodeSessionRow.Running"/>). The database is in WAL mode, so these reads
/// never contend with the agent writing to it.
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
    private bool _sawMissing;
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
        // the session. Whatever row turns up later came from this pane's first turn.
        if (session is null)
        {
            _sawMissing = true;
            return;
        }

        // First sighting of this row. Whatever state it is in now is a starting point,
        // not an event: a pane restored onto a conversation that finished before
        // Helide was launched must not light up, or every restored tab would come back
        // marked. Record it and wait for something to actually change -- unless the row
        // was missing before, in which case a first turn quick enough to finish between
        // two reads has just ended.
        if (!_baselineTaken)
        {
            _baselineTaken = true;
            _observedIdleAt = _sawMissing ? null : session.IdleAt;
            ReportBusy(session.Running);
            if (session.Running || !_sawMissing)
                return;
        }

        if (session.Running)
        {
            ReportBusy(true);
            return;
        }

        ReportBusy(false);

        // A new idle stamp is a turn that ended, whether or not a read caught it running.
        // The same stamp is the same turn, so it is not reported on every read after it.
        if (_observedIdleAt == session.IdleAt)
            return;

        _observedIdleAt = session.IdleAt;

        // A pane closed while its last read was in flight has nobody left to tell.
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
                BecameIdle?.Invoke();
        }));
    }
}
