using System.Windows.Threading;
using Helide.Terminal;

namespace Helide.Sessions;

/// <summary>
/// Last resort for a pane whose tool cannot report status: assume it settled once it
/// has been quiet for a while.
/// </summary>
/// <remarks>
/// Deliberately biased towards false negatives. An agent running a silent build is
/// indistinguishable from one that has finished, so a shorter window lights the mark
/// up constantly and teaches you to ignore it. A longer one occasionally misses a
/// fast reply, which costs nothing.
/// </remarks>
internal sealed class QuietPeriodStatusSource : ISessionStatusSource
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(6);

    private readonly NativeTerminalHost _host;
    private readonly DispatcherTimer _timer;

    private long _lastSeenOutput;

    public QuietPeriodStatusSource(NativeTerminalHost host)
    {
        _host = host;

        _timer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _timer.Tick += (_, _) => Evaluate();
    }

    public bool IsBusy => false;

    public event Action? BecameIdle;

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    private void Evaluate()
    {
        var produced = _host.LastOutputTicks;

        // Never produced anything, or already judged this burst.
        if (produced == 0 || produced == _lastSeenOutput)
            return;

        _lastSeenOutput = produced;

        if (DateTime.UtcNow.Ticks - produced < QuietPeriod.Ticks)
            return;

        BecameIdle?.Invoke();
    }
}