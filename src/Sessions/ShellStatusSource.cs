using System.Windows.Threading;
using Helide.Interop;
using Helide.Terminal;

namespace Helide.Sessions;

/// <summary>
/// Reports whether a shell pane is running something, by looking for processes under it.
/// </summary>
/// <remarks>
/// A shell sitting at its prompt has no children, and a command it is running does. That
/// is the whole signal, and it is a real one: unlike a silence timer it does not mistake
/// a long quiet build for a finished one.
/// <para>
/// A busy state needs two polls in a row to count, so a command that lives for a moment,
/// or a prompt that spawns a helper, does not flash the tab. The console host that the
/// pseudoconsole keeps under the shell is not a command and is ignored.
/// </para>
/// </remarks>
internal sealed class ShellStatusSource : ISessionStatusSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private static readonly HashSet<string> Infrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "conhost.exe",
        "OpenConsole.exe",
    };

    private readonly NativeTerminalHost _host;
    private readonly DispatcherTimer _timer;

    private int _busyPolls;
    private bool _busy;

    public ShellStatusSource(NativeTerminalHost host)
    {
        _host = host;

        _timer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher)
        {
            Interval = PollInterval,
        };

        _timer.Tick += (_, _) => Evaluate();
    }

    public bool IsBusy => _busy;

    public event Action? BecameIdle;

    public event Action<bool>? BusyChanged;

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    private void Evaluate()
    {
        if (_host.ProcessId is not { } pid)
        {
            _busyPolls = 0;
            return;
        }

        var running = ProcessTree.DescendantNames(pid).Any(name => !Infrastructure.Contains(name));

        if (running)
        {
            _busyPolls++;
            if (_busyPolls >= 2 && !_busy)
            {
                _busy = true;
                BusyChanged?.Invoke(true);
            }

            return;
        }

        _busyPolls = 0;
        if (!_busy)
            return;

        _busy = false;
        BusyChanged?.Invoke(false);
        BecameIdle?.Invoke();
    }
}
