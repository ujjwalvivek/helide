using System.IO;
using System.Threading;
using System.Windows;
using Helide.Persistence;
using Application = System.Windows.Application;

namespace Helide;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _singleInstanceMutex = new Mutex(true, @"Local\Helide", out var createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown();
                return;
            }
        }
        catch (AbandonedMutexException)
        {
            // A previous instance died without releasing the mutex
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but is not accessible
            _singleInstanceMutex = null;
        }

        var stateStore = new AppStateStore();
        var state = stateStore.Load();
        var projectPath = e.Args.Length == 1 && Directory.Exists(e.Args[0])
            ? Path.GetFullPath(e.Args[0])
            : null;

        var window = new MainWindow(stateStore, state, projectPath);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
