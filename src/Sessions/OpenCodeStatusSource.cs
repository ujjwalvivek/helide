using System.Windows.Threading;

namespace Helide.Sessions;

/// <summary>
/// Reports whether an OpenCode pane is mid-turn, and keeps track of which conversation the
/// pane is on.
/// </summary>
/// <remarks>
/// Helide shares the user's opencode store, so this database holds every session on the
/// machine rather than just this pane's. A pane resumed onto a known conversation has its
/// id from the start; a fresh one is launched with no id and adopts the session the TUI
/// creates for itself, which is found by looking only at sessions created in this pane's
/// own project directory since the pane launched.
/// <para>
/// Adoption is claim-once. A session already spoken for by another pane is skipped, so two
/// panes opening together cannot end up watching the same conversation -- which is the
/// failure that matters, because a pane that adopts someone else's session will resume it
/// on the next launch.
/// </para>
/// <para>
/// A turn in progress has no <c>time_idle</c>; finishing sets it. The transition from "no
/// idle time" to "has one" is when the agent stopped, which is when a backgrounded pane is
/// worth marking. The database is in WAL mode, so these reads never contend with the agent
/// writing to it.
/// </para>
/// </remarks>
internal sealed class OpenCodeStatusSource : ISessionStatusSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    // Sessions already adopted by some pane. Shared across panes on purpose: adoption is
    // only safe if no two panes can claim the same session.
    private static readonly HashSet<string> Claimed = new(StringComparer.Ordinal);

    private readonly string _databasePath;
    private readonly string? _projectDirectory;
    private readonly DateTime _launchedUtc;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    // Guards the fields the poll writes and the UI thread reads.
    private readonly object _gate = new();

    private string? _sessionId;
    private long? _observedIdleAt;
    private bool _baselineTaken;
    private bool _sawRunning;

    public OpenCodeStatusSource(
        string databasePath,
        string? sessionId,
        string? projectDirectory,
        DateTime launchedUtc,
        Dispatcher dispatcher)
    {
        _databasePath = databasePath;
        _projectDirectory = projectDirectory;
        _launchedUtc = launchedUtc;
        _dispatcher = dispatcher;

        if (sessionId is { Length: > 0 } known)
        {
            _sessionId = known;
            lock (Claimed) Claimed.Add(known);
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = PollInterval,
        };

        _timer.Tick += (_, _) => Poll();
    }

    public bool IsBusy => false;

    /// <summary>
    /// The conversation this pane is on. Known immediately for a resumed pane, and learned
    /// from the first session this pane can be shown to have created otherwise.
    /// </summary>
    public string? SessionId
    {
        get { lock (_gate) return _sessionId; }
    }

    public event Action? BecameIdle;

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _timer.Stop();

        // Releasing the claim lets the session be adopted by a pane opened later, which is
        // what happens when a tab is closed and reopened.
        var id = SessionId;
        if (id is not null)
        {
            lock (Claimed) Claimed.Remove(id);
        }
    }

    private void Poll()
    {
        string? id;

        lock (_gate) id = _sessionId;
        id ??= Adopt();

        OpenCodeSessionRow? session;

        try
        {
            if (id is not { Length: > 0 })
                return;

            session = OpenCodeSessions.Read(_databasePath, id);
        }
        catch (Exception)
        {
            // The database is missing, locked or mid-migration. The pane keeps working
            // and simply reports no status.
            return;
        }

        lock (_gate)
        {
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
        }

        _dispatcher.BeginInvoke(new Action(() => BecameIdle?.Invoke()));
    }

    /// <summary>
    /// Claims the session this pane's TUI just created for itself, or null when there is
    /// not one to claim yet.
    /// </summary>
    private string? Adopt()
    {
        if (_projectDirectory is not { Length: > 0 } directory)
            return null;

        IReadOnlyList<OpenCodeSession> candidates;

        try
        {
            candidates = OpenCodeSessions.ReadCreatedIn(_databasePath, directory, _launchedUtc);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            lock (Claimed)
            {
                if (!Claimed.Add(candidate.Id))
                    continue;
            }

            lock (_gate)
            {
                if (_sessionId is not null)
                    continue;

                _sessionId = candidate.Id;
                // A session adopted this way is watched from the moment it appeared, so
                // there is no baseline to take: the first observation is already current.
                _baselineTaken = true;
                _observedIdleAt = candidate.IdleAt;
            }

            return candidate.Id;
        }

        return null;
    }
}