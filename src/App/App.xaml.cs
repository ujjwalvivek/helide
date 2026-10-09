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
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private string? _claimedKey;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // First, before anything that can throw. Startup failures are otherwise
        // invisible: a WPF app has no console, so an exception during OnStartup just
        // exits silently and leaves nothing but an APPCRASH event naming KERNELBASE.
        LogFatalExceptions();

        // Sweeps the .old executables left by the previous update. This is the first
        // process that starts after the one holding them exited, so it is the first
        // that can actually delete them.
        HelideUpdater.DeleteStaleOldExecutables();

        // Before any window exists, so the first layout pass already sees the
        // resolved family rather than swapping under a live renderer.
        ThemePalette.ApplyFonts();

        // Parsed before the instance guard, because the guard is keyed by project.
        var projectPath = e.Args.Length == 1 && Directory.Exists(e.Args[0])
            ? Path.GetFullPath(e.Args[0])
            : null;

        // Asked for explicitly by the palette's "Open New Window". It skips the guard
        // entirely rather than claiming the chooser key, because a window that started
        // with no arguments keeps that claim for its whole life -- including after it has
        // gone on to open a project -- so claiming it again would just focus that window
        // and appear to do nothing. A command called "open a new window" should open one.
        var forceNewWindow = e.Args.Any(argument =>
            string.Equals(argument, NewWindowFlag, StringComparison.OrdinalIgnoreCase));

        if (!forceNewWindow)
        {
            // One window per project. That is what lets two projects sit side by side
            // without their workspace files fighting, while still refusing a second window
            // on a project that is already open -- two would race on the same state file,
            // which is the exact problem the per-project state split just removed.
            //
            // The key is joined with a hyphen, not a backslash. Only the namespace prefix
            // may contain one: a name is treated as a path in the object namespace, so
            // "Local\Helide\welcome" asks for an object called "welcome" inside a
            // directory that does not exist, and the mutex constructor throws.
            var instanceName = InstanceName(AppStateStore.ProjectKey(projectPath));

            if (!TryClaimInstance(instanceName))
            {
                // This project already has a window. Ask it to come forward rather than
                // exiting silently, which is all this used to do.
                RequestActivation(instanceName);
                Shutdown();
                return;
            }

            CreateActivationEvent(instanceName);
            _claimedKey = AppStateStore.ProjectKey(projectPath);
        }

        var stateStore = new AppStateStore();
        var state = stateStore.Load();

        // If no project passed on startup but we have a last project from a previous session,
        // use it to resume the workspace.
        if (string.IsNullOrEmpty(projectPath) && !string.IsNullOrEmpty(state.LastProjectPath) && Directory.Exists(state.LastProjectPath))
        {
            projectPath = state.LastProjectPath;
        }

        // Before the window is constructed, so the persisted theme is the one the
        // first layout pass paints rather than a flash of the default.
        ThemePalette.ApplyTheme(state.Theme);

        // Auto-check for updates on restart (non-blocking, silent unless update found)
        CheckForUpdatesSilently();

        var window = new MainWindow(stateStore, state, projectPath);
        MainWindow = window;
        window.Show();

        if (!forceNewWindow)
            WatchForActivation(window);
    }

    internal const string NewWindowFlag = "--new-window";

    private static string InstanceName(string projectKey) => @"Local\Helide-" + projectKey;

    // Created before the window so a second launch arriving during startup still finds
    // it. AutoReset holds the signal until the watcher starts, so nothing is lost in
    // the gap.
    private void CreateActivationEvent(string instanceName)
    {
        try
        {
            _activationEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, instanceName + @".activate");
        }
        catch
        {
            _activationEvent = null;
        }
    }

    private bool TryClaimInstance(string instanceName)
    {
        try
        {
            _instanceMutex = new Mutex(true, instanceName, out var createdNew);
            if (createdNew)
                return true;

            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing it, so this process now holds it.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _instanceMutex = null;
            return true;
        }
    }

    private static void RequestActivation(string instanceName)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(instanceName + @".activate");
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The owning instance has not created its event yet. Nothing to bring forward.
        }
    }

    // A second launch has no window of its own to show, so this instance has to be the one
    // to react. The wait runs off the UI thread and only ever marshals back onto it.
    private void WatchForActivation(Window window)
    {
        if (_activationEvent is null)
            return;

        var signal = _activationEvent;
        var thread = new Thread(() =>
        {
            try
            {
                while (signal.WaitOne())
                {
                    window.Dispatcher.BeginInvoke(
                        new Action(() => (window as MainWindow)?.BringToFront()));
                }
            }
            catch (ObjectDisposedException)
            {
                // The claim was released or the app is shutting down.
            }
        })
        {
            IsBackground = true,
            Name = "Helide activation watcher",
        };

        thread.Start();
    }

    // Moves this window's ownership claim onto a project.
    //
    // The claim has to follow the project rather than the launch argument. A window
    // started from the chooser claims "welcome" and can then open any project, and if it
    // kept that claim another window would find the project's own key free and open a
    // second one -- two windows writing the same state file, which is exactly what the
    // per-project split exists to prevent.
    //
    // Returns false when another window already owns the project. The caller must not
    // open it: the chooser claim is restored first so this window keeps behaving like a
    // chooser, and the other window is brought forward instead.
    internal bool ClaimProject(string? projectPath, Window window)
    {
        var key = AppStateStore.ProjectKey(projectPath);

        if (string.Equals(key, _claimedKey, StringComparison.Ordinal))
            return true;

        ReleaseClaim();

        var instanceName = InstanceName(key);

        if (!TryClaimInstance(instanceName))
        {
            RequestActivation(instanceName);

            // Back to being a chooser, so this window is still addressable for "Open New
            // Window" and still forwards rather than duplicating.
            _claimedKey = AppStateStore.ProjectKey(null);
            TryClaimInstance(InstanceName(_claimedKey));
            CreateActivationEvent(InstanceName(_claimedKey));
            WatchForActivation(window);

            return false;
        }

        _claimedKey = key;
        CreateActivationEvent(instanceName);
        WatchForActivation(window);

        return true;
    }

    // Drops ownership, which is what returning to the chooser means: a window sitting on
    // the chooser must not block a second one from opening.
    internal void ReleaseProjectClaim(Window window)
    {
        ReleaseClaim();
        _claimedKey = AppStateStore.ProjectKey(null);
        TryClaimInstance(InstanceName(_claimedKey));
        CreateActivationEvent(InstanceName(_claimedKey));
        WatchForActivation(window);
    }

    private void ReleaseClaim()
    {
        // Disposing the event is what releases the watcher thread: its wait throws.
        _activationEvent?.Dispose();
        _activationEvent = null;
        _instanceMutex?.Dispose();
        _instanceMutex = null;
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

    // Silent background check for new updates when app restarts.
    // If one exists, a subtle notification is shown (not a popup blocking startup).
    private async void CheckForUpdatesSilently()
    {
        try
        {
            var updater = new HelideUpdater();
            // Use a short timeout so startup isn't delayed.
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var hasUpdate = await updater.CheckAsync(_ => { });

            if (hasUpdate)
            {
                // Auto-start download when app opens with update available.
                Dispatcher?.BeginInvoke(new Action(async () =>
                {
                    if (Application.Current?.MainWindow is MainWindow mw)
                    {
                        try
                        {
                            HelideUpdateState.SetDownloading();
                            var updater = new HelideUpdater();
                            // Reported through the shared state so a window opened midway
                            // sees the same labels rather than its own stale button.
                            await updater.DownloadAndApplyAsync(pct => HelideUpdateState.SetProgress(pct));

                            // The project to resume once the user agrees to restart. The
                            // window may still be on the welcome screen here, in which
                            // case the persisted path is the best answer available.
                            var projectPathArg = mw.CurrentProject;
                            if (string.IsNullOrEmpty(projectPathArg))
                            {
                                var stateStore = new AppStateStore();
                                projectPathArg = stateStore.Load().LastProjectPath;
                            }

                            HelideUpdateState.MarkUpdateApplied(projectPathArg);
                        }
                        catch (Exception ex)
                        {
                            HelideUpdateState.Clear();
                            mw.UpdatePillText.Text = $"failed: {ex.Message}";
                        }
                    }
                }));
            }
        }
        catch
        {
            // Silent failure: no update check is critical to app function.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Disposed before the mutex so the watcher thread is released first.
        _activationEvent?.Dispose();
        _activationEvent = null;
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // Called by the updater right before it spawns the replacement process.
    //
    // The new process is the same exe at the same path, so it asks for the very mutex
    // this one is still holding. If it does not get it it assumes a window for that
    // project is already open, signals it, and exits -- which reads as the restart
    // having done nothing. Releasing here means the child finds the claim free.
    internal static void ReleaseInstanceGuardForRestart()
    {
        if (Application.Current is App app) app.ReleaseClaim();
    }
}
