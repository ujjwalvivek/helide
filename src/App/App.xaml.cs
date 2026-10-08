using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Helide.Persistence;
using Helide.Theme;
using Application = System.Windows.Application;

namespace Helide;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before any window exists, so the first layout pass already sees the
        // resolved family rather than swapping under a live renderer.
        ThemePalette.ApplyFonts();

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

        LogFatalExceptions();

        var window = new MainWindow(stateStore, state, projectPath);
        MainWindow = window;
        window.Show();
    }

    // A WPF app has no console, so a crash on the dispatcher thread is just a
    // silent exit and an APPCRASH event naming KERNELBASE.dll rather than anything
    // useful. Anything raised outside a handler -- a binding fault, a layout pass, a
    // timer -- lands here, which is the only place the real stack still exists.
    private void LogFatalExceptions()
    {
        void Write(string kind, Exception exception)
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Helide");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "crash.log"),
                    $"[{DateTime.Now:O}] {kind}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 72)}{Environment.NewLine}");
            }
            catch
            {
                // Never let logging turn a crash into a different crash.
            }
        }

        DispatcherUnhandledException += (_, args) => Write("Dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Write("AppDomain", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) => Write("Task", args.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
