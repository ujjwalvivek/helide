using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Helide.Interop;
using Helide.Persistence;
using Helide.Sessions;
using Helide.Shell;
using Helide.Theme;
using Helide.Projects;
using Helide.Terminal;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using Application = System.Windows.Application;

namespace Helide;

public partial class MainWindow : Window
{
    private enum ToolPanel
    {
        Left,
        Runner,
        Agent,
    }

    private enum LeftTool
    {
        Git,
        Project,
    }

    // What the attention timer needs from any tab it might mark. The two view
    // models below are separate types with separate lifetimes, and the timer walks
    // all three collections, so this is the only thing they have to agree on.
    private interface IAttentiveTab
    {
        bool IsActive { get; }

        void SetHovered(bool hovered);

        void AttachStatusSource(ISessionStatusSource source);

        void ClearAttention();
    }

    private const int ExpectedToolCount = 4;


    private readonly AppStateStore _stateStore;
    private readonly AppState _state;

    // Exposed so other windows (e.g., AboutWindow) can determine the current project
    // when restarting after an update, so the new instance can resume the workspace.
    public string? LastProjectPathFromState => _state.LastProjectPath;

    // Per-project, and reloaded whenever the workspace switches projects. Kept apart
    // from _state so two Helide processes on different projects write different files
    // instead of the second one overwriting the first's sessions and tabs.
    private WorkspaceState _workspace = new();

    private readonly string? _startupProject;
    private readonly List<NativeTerminalHost> _terminalHosts = [];
    private NativeTerminalHost? _runnerHost;
    private NativeTerminalHost? _leftHost;
    private LeftTool _leftHostTool = LeftTool.Git;
    private LeftTool _activeLeftTool = LeftTool.Git;
    private readonly ObservableCollection<EditorTab> _editorTabs = [];
    private EditorTab? _activeEditorTab;
    private FileSystemWatcher? _openRequestWatcher;
    private string? _openRequestPath;
    private long _openRequestOffset;
    private string? _currentProject;
    public string? CurrentProject => _currentProject;

    // Read by the About window so its button reads the same state as the titlebar pill.
    public bool IsUpdateReadyToRestart => HelideUpdateState.IsReadyToRestart;
    private string _runCommand = "pwsh";
    private bool _loaded;
    private bool _closingWorkspace;
    private double _leftPaneMinWidth;
    private GridLength _leftSplitterWidth;
    private double _rightPaneMinWidth;
    private GridLength _rightSplitterWidth;

    private double _runnerMinHeight;
    private GridLength _runnerSplitterHeight;
    private double _frozenLeftWeight;
    private double _frozenRightWeight;
    private double _frozenRunnerWeight;
    private double _leftPanePixels;
    private double _rightPanePixels;
    private double _runnerPanePixels;

    // Agent session tracking
    private readonly ObservableCollection<AgentSessionView> _agentSessions = [];
    private AgentSessionView? _activeAgentSession;

    // Runner session tracking
    private readonly ObservableCollection<AgentSessionView> _runnerSessions = [];
    private AgentSessionView? _activeRunnerSession;

    internal MainWindow(AppStateStore stateStore, AppState state, string? startupProject = null)
    {
        _stateStore = stateStore;
        _state = state;
        _startupProject = startupProject;

        // Loaded here as well as in OpenWorkspace, because the two restores below run in
        // the constructor -- before any project is opened. Without this they would read an
        // empty workspace and quietly reset the arrangement and window position on every
        // launch, which reads as "my layout was ignored" rather than as a bug.
        if (!string.IsNullOrWhiteSpace(startupProject) && Directory.Exists(startupProject))
            _workspace = stateStore.LoadWorkspace(startupProject);

        InitializeComponent();

        // The collection is observable and bound once. Reassigning ItemsSource to the
        // same List instance on every change is a no-op as far as WPF is concerned,
        // which is what left the strip showing a single tab while the pane was
        // displaying a different file.
        EditorTabStrip.ItemsSource = _editorTabs;
        CaptureTrackDefaults();
        RestoreWindowGeometry();
        RestoreWorkspaceLayout();

        SourceInitialized += (_, _) => DwmNative.Apply(this);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += MainWindow_StateChanged;

        // Subscribed once here rather than per tab: the view models below hand
        // brushes to their bindings as values, so they are the one part of the UI
        // that {DynamicResource} cannot reach on a palette swap.
        ThemePalette.ThemeChanged += ThemePalette_ThemeChanged;
        Closed += (_, _) =>
        {
            ThemePalette.ThemeChanged -= ThemePalette_ThemeChanged;
            HelideUpdateState.Changed -= OnUpdateStateChanged;
        };

        // The update state lives outside both the pill and the About window so they
        // cannot disagree. Whoever downloads the update raises this, and the pill
        // follows rather than the two of them racing to guess.
        HelideUpdateState.Changed += OnUpdateStateChanged;

        CommandPaletteControl.Chosen += CommandPalette_Chosen;
        CommandPaletteControl.Dismissed += CloseCommandPalette;

        // Outside click dismissal is the popup's own job, via StaysOpen="False": it
        // captures the mouse while open, so a click that lands on a terminal's HWND
        // still reaches the popup. A window-level mouse handler could not do that --
        // the panes swallow the event before the window ever sees it.
        CommandPalettePopup.Closed += (_, _) => _paletteOpen = false;

        // The folder picker is opened by a palette command rather than by a key of its
        // own, and closes on picking as well as on dismiss -- so the closed flag is
        // maintained in one place here rather than by each of those paths.
        FolderPickerPopup.Closed += (_, _) =>
        {
            _folderPickerOpen = false;
            _pendingFolderAction = null;
        };

        FolderPickerControl.BrowseRequested += FolderPicker_BrowseRequested;
        FolderPickerControl.BackRequested += FolderPicker_BackRequested;

        // Minimising leaves the popup behind, because it is a separate top-level window
        // that does not follow the owner's minimised state.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                CloseCommandPalette();
        };

        // Status sources are attached as each session is created.
    }
    private void StartAttentionTimer()
    {
        // Replaced by per-session ISessionStatusSource implementations. Both bundled
        // agents report their own turn boundaries, which is the only thing accurate
        // for a task that runs for minutes.
    }

    private void ThemePalette_ThemeChanged(object? sender, EventArgs e)
    {
        foreach (var tab in _editorTabs)
            tab.RefreshTheme();

        foreach (var session in _runnerSessions)
            session.RefreshTheme();

        foreach (var session in _agentSessions)
            session.RefreshTheme();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        StartOpenRequestWatcher();
        // Yazi and Helix are launched by Helide, so they inherit this and route
        // "open this file" back to us instead of spawning their own windows.
        Environment.SetEnvironmentVariable("HELIDE_OPEN_REQUEST", _openRequestPath ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(_startupProject) && Directory.Exists(_startupProject))
            OpenWorkspace(_startupProject);
        else
            ShowWelcome();
    }

    private void OpenProjectButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a project folder for Helide",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
            SelectedPath = ResolveInitialFolder(),
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            OpenWorkspace(dialog.SelectedPath);
    }

    private string ResolveInitialFolder()
    {
        if (!string.IsNullOrWhiteSpace(_currentProject) && Directory.Exists(_currentProject))
            return _currentProject;
        if (!string.IsNullOrWhiteSpace(_state.LastProjectPath) && Directory.Exists(_state.LastProjectPath))
            return _state.LastProjectPath;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    // ---- Command palette -------------------------------------------------------
    //
    // Replaces the app-menu ContextMenu that used to hang off the logo button. Every
    // one of its items is here, plus the session and navigation commands that only
    // make sense once there is somewhere to put them. Built fresh on each open because
    // most of it depends on what is currently open.

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        // Which window in this thread holds real keyboard focus. WPF's
        // Keyboard.FocusedElement cannot answer this: it reports the query box as
        // focused even while the terminal's HWND is the one receiving keys.
        [DllImport("user32.dll")]
        public static extern IntPtr GetFocus();

        // Undocumented but long-lived and still what shell code uses to force a window
        // forward. SetForegroundWindow is refused when the request comes from a process
        // that does not own the foreground, which is exactly our case.
        [DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    }

    private bool _paletteOpen;
    private bool _folderPickerOpen;
    private Action<string>? _pendingFolderAction;

    private void OpenCommandPalette()
    {
        if (_paletteOpen)
            return;

        _paletteOpen = true;

        // Anchored to the root grid rather than left to default. A Popup placed with no
        // target centres on the pointer, which puts the overlay wherever the mouse
        // happens to be instead of in the window.
        CommandPalettePopup.PlacementTarget = RootGrid;
        CommandPalettePopup.IsOpen = true;
        CommandPaletteControl.Load(BuildPaletteCommands());

        // Focus comes from the popup's own Opened event, which is the first moment its
        // HwndSource exists. Asking earlier just queues focus against a window that is
        // not there yet.
    }

    private void CommandPalettePopup_Opened(object sender, EventArgs e)
    {
        CommandPaletteControl.FocusQuery();

        // WPF focus alone is not enough when the palette was opened from a pane. That
        // pane's renderer still holds real keyboard focus, so keystrokes went to it
        // rather than to the query box -- which is why Escape and typing did nothing
        // even though WPF reported the TextBox as the focused element. Handing the
        // popup's own window the focus is the same move NativeTerminalHost.FocusTerminal
        // makes in reverse.
        if (PresentationSource.FromVisual(CommandPaletteControl) is HwndSource source)
            NativeMethods.SetFocus(source.Handle);
    }

    private void CloseCommandPalette()
    {
        if (!_paletteOpen)
            return;

        _paletteOpen = false;
        CommandPalettePopup.IsOpen = false;
    }

    private void CommandPalette_Chosen(PaletteCommand command)
    {
        CloseCommandPalette();

        // Deferred so the overlay is gone before the command runs. Several of them open
        // a dialog or tear down the workspace, and doing that underneath a still-open
        // popup strands focus on a window that no longer exists.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, command.Invoke);
    }

    // ---- Folder picker ---------------------------------------------------------

    // Opens the in-project picker and remembers what the folder is for. The action is
    // deferred to the pick rather than wrapped into each command, so the picker has to
    // know nothing about editor tabs, agents or sessions.
    private void OpenFolderPicker(Action<string> open)
    {
        if (_currentProject is null || _folderPickerOpen)
            return;

        _folderPickerOpen = true;
        _pendingFolderAction = open;

        FolderPickerPopup.PlacementTarget = RootGrid;
        FolderPickerPopup.IsOpen = true;
        FolderPickerControl.Load(_currentProject);

        // Focus comes from the popup's own Opened event, for the same reason the
        // palette's does: it is the first moment the HwndSource exists.
    }

    private void FolderPickerPopup_Opened(object sender, EventArgs e)
    {
        FolderPickerControl.FocusQuery();

        if (PresentationSource.FromVisual(FolderPickerControl) is HwndSource source)
            NativeMethods.SetFocus(source.Handle);
    }

    private void CloseFolderPicker()
    {
        if (!_folderPickerOpen)
            return;

        _folderPickerOpen = false;
        _pendingFolderAction = null;
        FolderPickerPopup.IsOpen = false;
    }

    private void FolderPicker_Picked(string path)
    {
        // Captured before the close: the popup's Closed handler clears the pending
        // action, which this must not read after the fact.
        var open = _pendingFolderAction;
        CloseFolderPicker();

        if (open is null)
            return;

        // Deferred for the palette's reason -- the overlay goes first, the terminal
        // that is about to take the folder is created with the desktop settled.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => open(path));
    }

    // The palette this picker was opened from, brought back over it. The pending folder
    // action is dropped with the picker: the user changed their mind about the command,
    // so returning means choosing a command again rather than committing to the one they
    // abandoned. Nothing else needs doing -- the palette rebuilds its commands on every
    // open, so it comes back exactly as it was.
    private void FolderPicker_BackRequested()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            CloseFolderPicker();
            OpenCommandPalette();
        });
    }

    // The native browser, for the one folder the picker's tree cannot show: one outside
    // the project.
    //
    // The picker is closed BEFORE the dialog opens, not after it is cancelled. A
    // parentless WinForms dialog adopts whatever window is active when it opens, and
    // under an open popup that is the popup's own HWND -- so the deactivation that
    // closes a StaysOpen popup destroys the dialog's owner, and the dialog with it.
    // That is the flicker, the instant close, and the window left minimised with
    // nothing to activate back to.
    private void FolderPicker_BrowseRequested()
    {
        var open = _pendingFolderAction;
        if (open is null)
            return;

        // Deferred for the palette's reason: a modal loop started inside a mouse
        // handler re-enters WPF input from under that handler.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => BrowseHandoff(open));
    }

    private void BrowseHandoff(Action<string> open)
    {
        CloseFolderPicker();

        if (PickFolderWithDialog() is not { } path)
        {
            // Cancelled. The pick comes back rather than dropping the user out of the
            // command they asked for -- closing the picker was a means to an end, not
            // the thing they did.
            OpenFolderPicker(open);
            return;
        }

        // Deferred again so the dialog is gone before the terminal appears.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => open(path));
    }

    private string? PickFolderWithDialog()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a folder to open in",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
            SelectedPath = _currentProject ?? ResolveInitialFolder(),
        };

        return dialog.ShowDialog(DialogOwner) == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

    // This window as an IWin32Window, so the dialog is owned by it: without an owner
    // the dialog lands on whichever monitor is active and is parented to a popup.
    private System.Windows.Forms.IWin32Window DialogOwner =>
        new DialogOwnerWindow(new WindowInteropHelper(this).Handle);

    private sealed class DialogOwnerWindow(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private List<PaletteCommand> BuildPaletteCommands()
    {
        var workspace = () => WorkspaceRoot.Visibility == Visibility.Visible;
        var commands = new List<PaletteCommand>();

        // --- File ---
        commands.Add(new PaletteCommand("Open Folder", OpenProjectFolder, "File", PaletteCommand.Gesture(Key.O)));
        commands.Add(new PaletteCommand("Close Workspace", ShowWelcome, "File", canInvoke: workspace));
        commands.Add(new PaletteCommand("Exit", Close, "File"));

        foreach (var project in _state.RecentProjects)
        {
            var path = project.Path;
            commands.Add(new PaletteCommand(
                $"Open {project.Name}",
                () => OpenRecentProject(path),
                "Recent",
                path));
        }

        // --- Window ---
        //
        // A second process, not a second window in this one. The panes are HwndHost-backed
        // and WPF has no z-order authority over them, and the codex session capture keeps a
        // single static snapshot, so two live workspaces in one process would fight over
        // both. Separate processes get their own state file, their own statics and their own
        // env, which is what makes this cheap.

        // No project argument -- combined with --new-window so it skips the guard
        // entirely, which is what makes the command actually open a window rather than
        // focusing whichever one last claimed the chooser.
        commands.Add(new PaletteCommand(
            "Open New Window",
            () => OpenInNewWindow(null),
            "Window",
            "project chooser",
            keywords: "blank empty new instance"));

        foreach (var project in _state.RecentProjects)
        {
            // The open project is skipped: a second window on it would find the mutex
            // taken and focus this one, so the entry could never do anything.
            if (_currentProject is not null
                && string.Equals(project.Path, _currentProject, StringComparison.OrdinalIgnoreCase))
                continue;

            var path = project.Path;
            commands.Add(new PaletteCommand(
                $"Open {project.Name} in New Window",
                () => OpenInNewWindow(path),
                "Window",
                path));
        }

        // --- New ---
        commands.Add(new PaletteCommand("New Editor Tab", () => CreateEditorTab(null), "New", "Ctrl+N", canInvoke: workspace));
        commands.Add(new PaletteCommand("New Runner Session", () => CreateRunnerSession("pwsh", "pwsh", PowerShellCommand("pwsh -NoLogo -NoExit")), "New", canInvoke: workspace));
        commands.Add(new PaletteCommand("New OpenCode Session", () => CreateAgentSession("opencode", "OpenCode"), "New", canInvoke: workspace));
        commands.Add(new PaletteCommand("New Codex Session", () => CreateAgentSession("codex", "Codex"), "New", canInvoke: workspace));

        // Open in a chosen folder rather than the project root. Each picks a folder and
        // opens the session there, focused -- the creation methods already activate what
        // they make. This is the per-pane working directory: an agent scoped to a
        // subdirectory, a yazi on a folder outside the project.
        //
        // The picker lists the project's own folders, project-relative and
        // fuzzy-filtered. A folder outside the project is not in that list -- it is a
        // different workspace, not a session root in this one -- so the picker pins an
        // "Open another folder" row at the bottom that opens the native dialog. One
        // entry, not four: the dialog is the same whatever the pane.
        commands.Add(new PaletteCommand(
            "Open Editor Tab in Folder…",
            () => OpenFolderPicker(path => CreateEditorTab(null, path)),
            "New",
            "project folders",
            canInvoke: workspace));
        commands.Add(new PaletteCommand(
            "Open OpenCode in Folder…",
            () => OpenFolderPicker(path => CreateAgentSession("opencode", "OpenCode", workingDirectory: path)),
            "New",
            "project folders",
            canInvoke: workspace));
        commands.Add(new PaletteCommand(
            "Open Codex in Folder…",
            () => OpenFolderPicker(path => CreateAgentSession("codex", "Codex", workingDirectory: path)),
            "New",
            "project folders",
            canInvoke: workspace));
        commands.Add(new PaletteCommand(
            "Open Runner in Folder…",
            () => OpenFolderPicker(path => CreateRunnerSession("pwsh", "pwsh", PowerShellCommand("pwsh -NoLogo -NoExit"), workingDirectory: path)),
            "New",
            "project folders",
            canInvoke: workspace));

        // --- Close ---
        commands.Add(new PaletteCommand(
            "Close Current Editor Tab",
            () => { if (_activeEditorTab is { } tab) CloseEditorTab(tab); },
            "Close",
            canInvoke: () => workspace() && _editorTabs.Count > 0));

        commands.Add(new PaletteCommand(
            "Close Current Agent Session",
            () => { if (_activeAgentSession is { } session) CloseAgentSession(session); },
            "Close",
            canInvoke: () => workspace() && _agentSessions.Count > 1));

        // --- Focus ---
        commands.Add(new PaletteCommand("Focus Left Panel", () => FocusPanel(ToolPanel.Left, ActiveLeftHost()), "Focus", PaletteCommand.Gesture(Key.D1), canInvoke: workspace));
        commands.Add(new PaletteCommand("Focus Editor", () => FocusPanel(null, ActiveEditorHost()), "Focus", PaletteCommand.Gesture(Key.D2), canInvoke: workspace));
        commands.Add(new PaletteCommand("Focus Runner", () => FocusPanel(ToolPanel.Runner, _runnerHost), "Focus", PaletteCommand.Gesture(Key.D3), canInvoke: workspace));
        commands.Add(new PaletteCommand("Focus Agent", () => FocusPanel(ToolPanel.Agent, _activeAgentSession?.Host), "Focus", PaletteCommand.Gesture(Key.D4), canInvoke: workspace));

        // --- Navigate ---
        commands.Add(new PaletteCommand("Show Lazygit Panel", () => ToggleLeftTool(LeftTool.Git), "View", keywords: "git left", canInvoke: workspace));
        commands.Add(new PaletteCommand("Show Project Browser", () => ToggleLeftTool(LeftTool.Project), "View", keywords: "yazi files left", canInvoke: workspace));
        commands.Add(new PaletteCommand("Toggle Terminal Panel", () => TogglePanel(ToolPanel.Runner), "View", canInvoke: workspace));
        commands.Add(new PaletteCommand("Toggle Agent Panel", () => TogglePanel(ToolPanel.Agent), "View", canInvoke: workspace));
        commands.Add(new PaletteCommand("Next Editor Tab", () => CycleEditorTab(1), "Navigate", canInvoke: () => workspace() && _editorTabs.Count > 1));
        commands.Add(new PaletteCommand("Previous Editor Tab", () => CycleEditorTab(-1), "Navigate", canInvoke: () => workspace() && _editorTabs.Count > 1));
        commands.Add(new PaletteCommand("Run Project Command", RunProjectCommand, "Run", canInvoke: workspace));

        // --- Theme ---
        // Driven off the theme table rather than a hand-maintained list, so a theme
        // added to ThemePalette shows up here without a second edit.
        foreach (var (key, name, _) in ThemePalette.AvailableThemes)
        {
            commands.Add(new PaletteCommand(
                $"Theme: {name}",
                () => ApplyTheme(key),
                "Theme",
                key == ThemePalette.ActiveTheme ? "active" : null,
                keywords: key));
        }

        commands.Add(new PaletteCommand("About Helide", ShowAbout, "Help"));

        return commands;
    }

    private void OpenProjectFolder() => OpenProjectButton_Click(this, new RoutedEventArgs());

    // Launches another Helide, optionally on a project. A null path passes no argument, which
// lands the new process on the project chooser.
//
// When a window is already open for the same project, the new process finds that
// project's mutex taken and asks it to come forward instead. That is what stops a
// duplicate, and it matters because two windows on one project would both write the same
// workspace file.
private void OpenInNewWindow(string? projectPath)
{
    var executable = Environment.ProcessPath;
    if (executable is null)
        return;

    var arguments = string.IsNullOrWhiteSpace(projectPath)
        ? App.NewWindowFlag
        : QuoteArgument(Path.GetFullPath(projectPath));

    try
    {
        Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
        });
    }
    catch (Exception exception)
    {
        // Failing to spawn is not worth a crash: this window is unaffected either way.
        MessageBox.Show(this,
            $"Could not open a new Helide window:\n\n{exception.Message}",
            "Helide", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

    // Whether the popup's own window is the one receiving real keystrokes.
    //
    // This replaced a check on WPF's Keyboard.FocusedElement, which was the wrong
    // signal: it reports the query box as focused even while the terminal's HWND is
    // the one actually receiving keys, so the palette believed it had the keyboard and
    // let arrows and characters fall through to a text box that never saw them.
    private bool PaletteHasOsFocus()
    {
        if (PresentationSource.FromVisual(CommandPaletteControl) is not HwndSource source)
            return false;

        return NativeMethods.GetFocus() == source.Handle;
    }

    // The same question for the folder picker's popup, asked separately because the
    // two are distinct HwndSources and each would report the other's window as not
    // focused.
    private bool FolderPickerHasOsFocus()
    {
        if (PresentationSource.FromVisual(FolderPickerControl) is not HwndSource source)
            return false;

        return NativeMethods.GetFocus() == source.Handle;
    }

    // The character a key produces, or null when it is not printable. Only plain
    // unmodified keys are forwarded; anything with a modifier is a shortcut.
    private static string? PrintableText(Key key)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return null;

        if (key == Key.Space)
            return " ";

        if (key is >= Key.A and <= Key.Z)
            return key.ToString();

        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();

        return null;
    }

    private void ToggleCommandPalette()
    {
        if (_paletteOpen)
            CloseCommandPalette();
        else
            OpenCommandPalette();
    }

    private void ShowAbout()
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void UpdatePill_Click(object sender, RoutedEventArgs e)
    {
        // Non-clickable until restart state
        if (UpdatePillText.Text != "restart to update") return;
        try
        {
            UpdatePillText.Text = "Restarting...";
            // Broadcast first: Helide is one process per project, so the other windows
            // will not notice this one leaving otherwise. Each restores its own
            // workspace from its own watcher.
            HelideUpdateBroadcast.RequestRestart();
            HelideUpdateState.Restart();
        }
        catch (Exception ex)
        {
            UpdatePillText.Text = $"failed: {ex.Message}";
        }
    }

    // Raised by HelideUpdateState, which both the pill and the About window write to.
    // Following the shared state instead of each window's own TextBlock is what keeps
    // "the pill says restart but About offers to download again" from coming back.
    private void OnUpdateStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnUpdateStateChanged);
            return;
        }

        var status = HelideUpdateState.Status;
        if (string.IsNullOrEmpty(status)) return;

        UpdatePill.Visibility = Visibility.Visible;
        UpdatePillText.Text = status;
        // Only clickable once the exe is on disk; mid-download a click would start a
        // second one.
        UpdatePill.Cursor = HelideUpdateState.IsReadyToRestart
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
    }

    private void RunProjectCommand()
    {
        if (_runnerHost is null)
            return;

        _runnerHost.FocusTerminal();
        if (_runCommand != "pwsh")
            _runnerHost.WriteLine(_runCommand);
    }

    private void ApplyTheme(string key)
    {
        ThemePalette.ApplyTheme(key);

        _state.Theme = ThemePalette.ActiveTheme;
        _stateStore.Save(_state);
    }

    // A null reveal means "do not un-collapse anything": the editor is always visible,
    // so focusing it must not toggle a panel that happens to be collapsed.
    private void FocusPanel(ToolPanel? reveal, NativeTerminalHost? host)
    {
        if (host is null)
            return;

        if (reveal is { } panel && IsPanelCollapsed(panel))
            TogglePanel(panel);

        host.FocusTerminal();
    }

    private void CycleEditorTab(int delta)
    {
        if (_editorTabs.Count == 0 || _activeEditorTab is null)
            return;

        var index = _editorTabs.IndexOf(_activeEditorTab);
        var next = (index + delta + _editorTabs.Count) % _editorTabs.Count;
        ActivateEditorTab(_editorTabs[next]);
    }

    // Raised on this window when another launch asks for a project it already owns. That
    // process has no window of its own, so this is the only place the request can land.
    internal void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        // SwitchToThisWindow rather than the usual Topmost toggle. Activate alone is
        // refused when the request came from a different process, and going through
        // Topmost does work but visibly flashes the window across the screen every time
        // the second launch repeats.
        NativeMethods.SwitchToThisWindow(new WindowInteropHelper(this).Handle, true);
        Activate();
    }

    private void MinimizeCaptionButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreCaptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseCaptionButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        MaximizeInset.Margin = WindowState == WindowState.Maximized
            ? MaximizeBounds.Measure(this)
            : new Thickness(0);
    }

    private void ShowWelcome()
    {
        // Dropped on the way to the chooser: a window sitting here must not stop another
        // from opening the same project, which is what holding the claim would do.
        if (_currentProject is not null && Application.Current is App app)
            app.ReleaseProjectClaim(this);

        SaveWorkspaceLayout();
        WorkspaceRoot.Visibility = Visibility.Collapsed;
        WelcomeRoot.Visibility = Visibility.Visible;
        ProjectNamePill.Visibility = Visibility.Collapsed;
        ProjectPathPill.Visibility = Visibility.Collapsed;
        GitBranchPill.Visibility = Visibility.Collapsed;
        Title = "Open Recent Project";
        PopulateWelcome();
        OpenProjectButton.Focus();
    }

    private void ShowWorkspace()
    {
        if (string.IsNullOrWhiteSpace(_currentProject))
        {
            ShowWelcome();
            return;
        }

        WelcomeRoot.Visibility = Visibility.Collapsed;
        WorkspaceRoot.Visibility = Visibility.Visible;
        var projectName = new DirectoryInfo(_currentProject).Name;
        Title = $"Helide | {projectName}";
    }

    private void PopulateWelcome()
    {
        var currentOrLast = _currentProject ?? _state.LastProjectPath;
        if (!string.IsNullOrWhiteSpace(currentOrLast) && Directory.Exists(currentOrLast))
        {
            ContinueProjectButton.Visibility = Visibility.Visible;
            ContinueProjectPathText.Text = currentOrLast;
        }
        else
        {
            ContinueProjectButton.Visibility = Visibility.Collapsed;
            ContinueProjectPathText.Text = string.Empty;
        }

        var recentProjects = _state.RecentProjects
            .Take(8)
            .Select((project, index) => new RecentProjectView(
                string.IsNullOrWhiteSpace(project.Name)
                    ? new DirectoryInfo(project.Path).Name
                    : project.Name,
                project.Path,
                $"Ctrl+{index + 1}"))
            .ToArray();

        RecentProjectItems.ItemsSource = recentProjects;
        RecentEmptyText.Visibility = recentProjects.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ContinueProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentProject))
        {
            ShowWorkspace();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_state.LastProjectPath))
            OpenWorkspace(_state.LastProjectPath);
    }

    private void RecentProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            OpenRecentProject(path);
    }

    private void OpenRecentProject(string path)
    {
        if (!Directory.Exists(path))
        {
            _stateStore.RemoveRecentProject(_state, path);
            PopulateWelcome();
            WelcomeStatus.Text = $"Removed missing project: {path}";
            return;
        }

        OpenWorkspace(path);
    }

    private void OpenWorkspace(string project)
    {
        if (!Directory.Exists(project))
        {
            MessageBox.Show(this, $"The project folder does not exist:\n\n{project}",
                "Helide", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowWelcome();
            return;
        }

        project = Path.GetFullPath(project);
        if (string.Equals(project, _currentProject, StringComparison.OrdinalIgnoreCase) &&
            _terminalHosts.Count > 0)
        {
            ShowWorkspace();
            return;
        }

        // Ownership is taken before anything below tears the old workspace down. If
        // another window already has this project, this one hands focus to it and stays
        // put -- checked afterwards it would have already closed the workspace it was
        // showing. Two windows on one project would both write the same state file.
        if (Application.Current is App app && !app.ClaimProject(project, this))
            return;

        try
        {
            // Flush the workspace being left into its own file before the project
            // changes, or the layout and tabs just captured would be written against the
            // project being opened next.
            if (_currentProject is not null)
                _stateStore.SaveWorkspace(_workspace, _currentProject);

            SaveWorkspaceLayout();
            CloseWorkspace();

            _currentProject = project;

            // Loaded before anything reads it: RestoreWorkspaceLayout, RestoreEditorTabs
            // and InitializeAgentSessions all come further down this method.
            _workspace = _stateStore.LoadWorkspace(project);

            // The constructor restores these too, but only for the startup project. On a
            // switch the freshly loaded project's arrangement has to be applied, or the
            // previous project's layout would carry over and per-project layout would
            // mean nothing.
            //
            // Window geometry is deliberately NOT restored here. Per-project position
            // means opening a project yanks the window across the screen to wherever it
            // was left last time, which reads as the app jumping rather than as a
            // remembered arrangement. Geometry still applies per project when a window
            // *starts* on one, so two windows on different projects keep their own.
            RestoreWorkspaceLayout();

            _runCommand = ProjectDetector.DetectRunCommand(project);
            var projectName = new DirectoryInfo(project).Name;
            var branch = ProjectDetector.DetectGitBranch(project);

            ProjectNamePillText.Text = projectName;
            ProjectNamePill.Visibility = Visibility.Visible;
            ProjectPathPillText.Text = project;
            ProjectPathPill.Visibility = Visibility.Visible;
            if (branch is not null)
            {
                GitBranchPillText.Text = branch;
                GitBranchPill.Visibility = Visibility.Visible;
            }
            else
            {
                GitBranchPill.Visibility = Visibility.Collapsed;
            }
            ResetPaneStates();

            ShowWorkspace();

            var failures = 0;
            // Only the selected left tool is started. Keeping lazygit and yazi alive at
            // the same time put two renderer windows in one Grid, and WPF z-order has no
            // authority over the child HWNDs that back the terminal renderer, so one of
            // them always ended up invisible-but-alive, swallowing clicks and keys.
            SetLeftTool(RestoreLeftTool(), persist: false);
            if (_leftHost is null)
                failures++;
            // Restores the editor tabs this workspace had open. A tab whose file has since
            // been deleted or moved is dropped rather than opened on a path that no
            // longer resolves.
            if (RestoreEditorTabs() == 0 && CreateEditorTab(null) is null)
                failures++;
            // Initialize runner session with startup message, or create default
            InitializeRunnerSessions(project);
            if (_runnerSessions.Count == 0)
                CreateRunnerSession("runner", "pwsh", PowerShellCommand(
                    $"Write-Host {PowerShellLiteral($" Try {_runCommand}")} -ForegroundColor DarkCyan"
                ));
            // Take the rollout snapshot before any pane exists. A codex pane writes its
            // rollout file a moment after the process starts, so snapshotting after the
            // launches below would place the cutoff past the files those panes had
            // already written, and they would be discarded for the whole session --
            // leaving every codex tab to come back empty on the next launch.
            CodexSessions.Initialize();

            // Initialize agent session from saved state or create default
            InitializeAgentSessions(project);
            if (_agentSessions.Count == 0)
                CreateAgentSession("opencode", "OpenCode");

            // Requery session ids on a timer while the window is up so a restarted
            // pane re-registers. The close-time pass reads what the timer already has.
            StartSessionCaptureTimer();

            // Apply saved panel state (default collapsed for fresh launches, expanded if user toggled)
            SetPanelCollapsed(ToolPanel.Agent, _workspace.Layout.AgentCollapsed);
            SetPanelCollapsed(ToolPanel.Runner, _workspace.Layout.RunnerCollapsed);
            // Explicit sync so visibility + column/row tracks always match saved collapsed state
            AgentPane.Visibility = _workspace.Layout.AgentCollapsed ? Visibility.Collapsed : Visibility.Visible;
            AgentPanelToggle.IsChecked = !_workspace.Layout.AgentCollapsed;
            RunnerPane.Visibility = _workspace.Layout.RunnerCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TerminalPanelToggle.IsChecked = !_workspace.Layout.RunnerCollapsed;
            // Direct column/width sync to eliminate leftover empty space from star redistribution
            if (_workspace.Layout.AgentCollapsed)
            {
                RightPaneColumn.Width = new GridLength(0);
                RightPaneColumn.MinWidth = 0;
                RightSplitterColumn.Width = new GridLength(0);
            }
            else
            {
                RightPaneColumn.Width = new GridLength(RememberedRightWidth());
                RightPaneColumn.MinWidth = _rightPaneMinWidth;
                RightSplitterColumn.Width = _rightSplitterWidth;
            }
            if (_workspace.Layout.RunnerCollapsed)
            {
                RunnerPaneRow.Height = new GridLength(0);
                RunnerPaneRow.MinHeight = 0;
                RunnerSplitterRow.Height = new GridLength(0);
            }
            else
            {
                RunnerPaneRow.Height = new GridLength(RememberedRunnerHeight());
                RunnerPaneRow.MinHeight = _runnerMinHeight;
                RunnerSplitterRow.Height = _runnerSplitterHeight;
            }

            _stateStore.RecordProject(_state, project);
            if (failures > 0)
                WelcomeStatus.Text = $"{failures} tool pane{(failures == 1 ? "" : "s")} failed to start.";
        }
        catch (Exception exception)
        {
            CloseWorkspace();
            _currentProject = null;
            ShowWelcome();
            WelcomeStatus.Text = $"Could not open the workspace.\n{exception.Message}";
        }
    }

    private NativeTerminalHost? CreateTerminal(
        ContentControl slot,
        TextBlock stateText,
        string label,
        string commandLine,
        string workingDirectory)
    {
        try
        {
            var host = new NativeTerminalHost(label, commandLine, workingDirectory);
            host.StateChanged += (_, state) => UpdatePaneState(stateText, state);
            slot.Content = host;
            _terminalHosts.Add(host);
            return host;
        }
        catch (Exception exception)
        {
            slot.Content = CreateErrorPanel(label, exception);
            stateText.Text = "failed";
            stateText.Foreground = ThemePalette.Brush(ThemePalette.DangerBrush);
            return null;
        }
    }

// Exactly one terminal host is ever parented in the left stack. Two renderer
    // windows stacked in one Grid is not a thing WPF can arbitrate: the terminal
    // renderer is a child HWND (Microsoft.Terminal.Wpf.TerminalContainer derives from
    // HwndHost), sibling HWNDs z-order by creation, and Panel.ZIndex only reorders WPF
    // visuals. The hidden one kept eating clicks and keyboard focus, which is why yazi
    // would highlight a file but never open it and why lazygit came back dead.
    private NativeTerminalHost? StartLeftTool(LeftTool tool)
    {
        var label = tool == LeftTool.Project ? "yazi" : "lazygit";
        var stateText = tool == LeftTool.Project ? ProjectPaneState : GitPaneState;
        var host = new NativeTerminalHost(label, ToolCommand($"{label}.exe"), _currentProject!);
        host.StateChanged += (_, state) => UpdatePaneState(stateText, state);
        LeftToolStack.Children.Add(host);
        _terminalHosts.Add(host);
        _leftHost = host;
        _leftHostTool = tool;
        stateText.Text = "starting";
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => host.BringToFront()));
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => host.BringToFront()));
            return host;
    }

    private void StopLeftTool()
    {
        if (_leftHost is null)
            return;

        // Detach, then dispose -- see CloseEditorTab for why the order matters.
        _leftHost.Visibility = Visibility.Collapsed;
        LeftToolStack.Children.Clear();
        _terminalHosts.Remove(_leftHost);
        _leftHost.Dispose();
        _leftHost = null;
    }

    // Yazi hands paths over by appending them to a file Helide watches, rather than
    // launching its own editor process. That keeps every open file inside Helide's
    // editor pane instead of spawning stray `hx` windows.
    private void StartOpenRequestWatcher()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Helide");
            Directory.CreateDirectory(directory);
            _openRequestPath = Path.Combine(directory, "open-request.txt");
            if (File.Exists(_openRequestPath))
                File.Delete(_openRequestPath);
            _openRequestOffset = 0;

            _openRequestWatcher = new FileSystemWatcher(directory, "open-request.txt")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            // Deliberately not enabled in the initializer: a change that lands before
            // the handlers are attached would be missed, and the first thing yazi does
            // is append to this file.
            _openRequestWatcher.Changed += OpenRequest_Changed;
            _openRequestWatcher.Created += OpenRequest_Changed;
            _openRequestWatcher.Deleted += OpenRequest_Changed;
            _openRequestWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Without the watcher yazi simply opens files the usual way.
            _openRequestWatcher = null;
        }
    }

    // The request file is an append-only log that yazi grows one line at a time, so it
    // is consumed by byte offset rather than read-and-deleted. Read-then-delete could
    // land between one write and the next and drop a request outright, or catch a line
    // mid-write and open a truncated path.
    private void OpenRequest_Changed(object sender, FileSystemEventArgs e)
    {
        if (_openRequestPath is null)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ConsumeOpenRequests));
    }

    private void ConsumeOpenRequests()
    {
        if (_openRequestPath is null || !File.Exists(_openRequestPath))
        {
            _openRequestOffset = 0;
            return;
        }

        var consumed = new List<string>();
        try
        {
            using var stream = new FileStream(
                _openRequestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _openRequestOffset)
            {
                // The log was truncated or replaced; start over.
                _openRequestOffset = 0;
            }

            stream.Seek(_openRequestOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var complete = text.LastIndexOf('\n');
            if (complete < 0)
            {
                // No complete line yet. Leave it for the next notification.
                return;
            }

            _openRequestOffset += Encoding.UTF8.GetByteCount(text[..(complete + 1)]);
            foreach (var line in text[..complete].Split('\n'))
            {
                var path = line.Trim().Trim('"');
                if (path.Length > 0)
                    consumed.Add(path);
            }
        }
        catch
        {
            return;
        }

        // Retire the log only once it has been fully drained, so a request appended
        // while we were reading cannot be dropped on the floor.
        try
        {
            if (new FileInfo(_openRequestPath).Length <= _openRequestOffset)
            {
                File.Delete(_openRequestPath);
                _openRequestOffset = 0;
            }
        }
        catch
        {
        }

        foreach (var path in consumed)
        {
            if (File.Exists(path))
                OpenEditorTab(path);
        }
    }

    // Every open file gets its own Helix process, because Helix 25.07 has neither IPC
    // nor tabbed buffers: the only way to hand it a file is its command line, and the
    // only way to hold two buffers is two processes. Helix itself draws its own tab
    // bar for what is inside one process, so this strip is Helide's set of processes.
    //
    // The hosts stay parented for the whole session and only Visibility flips.
    // Removing an HwndHost from the visual tree reparents its native window to an
    // invisible message-only window, which is how a "closed" pane used to keep
    // painting over the live one. Stacking is only safe because the tab being shown
    // is explicitly raised with SetWindowPos: Panel.SetZIndex reorders WPF visuals
    // and has no say over the child HWNDs that are actually painted and clicked.
    private void OpenEditorTab(string path)
    {
        if (_currentProject is null || string.IsNullOrWhiteSpace(path))
            return;

        path = Path.GetFullPath(path);
        foreach (var tab in _editorTabs)
        {
            if (string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                ActivateEditorTab(tab);
                return;
            }
        }

        // The opening tab is Helix's own file picker, which exists to lead somewhere
        // rather than to be a tab of its own. The first file picked from it takes its
        // place, so picking a file never leaves a dead scratch tab behind.
        if (_editorTabs.Count == 1 && _editorTabs[0].Path is null)
            CloseEditorTab(_editorTabs[0], refill: false);

        CreateEditorTab(path);
    }

    // ---- Tab strip scrolling ----------------------------------------------------
    //
    // Three tab strips (editor, runner, agent) share this behaviour: no scrollbar is
    // drawn, the wheel scrolls them horizontally, and adding a tab scrolls it into
    // view. Without this a strip whose tabs outgrow the pane leaves the new tab --
    // the one you just asked for -- sitting off-screen past the right edge.

    // Wheel over a strip scrolls it sideways. A vertical wheel is the gesture people
    // actually have, and ScrollViewer would otherwise swallow it (the strip is
    // horizontal, so vertical scrolling does nothing and the wheel does nothing at
    // all). Marking the event handled stops the wheel bubbling up to whatever the
    // pane underneath would do with it.
    private void TabStrip_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer viewer)
            return;

        if (e.Delta == 0)
            return;

        // A strip whose content fits has nothing to scroll. Left alone, the offset is
        // set to a negative value and clamped, and the strip appears to eat the wheel
        // instead of passing it on.
        if (viewer.ScrollableWidth <= 0)
            return;

        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - (e.Delta / 120.0) * 48.0);
        e.Handled = true;
    }

    // Scrolls a strip so its right-hand end is visible. Called when a tab is added,
    // since that is the case where the content grows past the viewport; the offset
    // can then only be past the end already.
    private static void ScrollStripToEnd(System.Windows.Controls.ScrollViewer viewer)
    {
        // Deferred to the next layout pass: the new tab has no measured width until
        // the ItemsControl has arranged, so the extent read now is the old one and the
        // new tab would still be off-screen.
        viewer.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => viewer.ScrollToRightEnd()));
    }

    // The working directory defaults to the project, which is what every pane has always
    // used. It is a parameter so the palette can open a session rooted somewhere else --
    // an agent scoped to a subdirectory, a yazi on a folder outside the project -- without
    // pretending the project itself has moved.
    private EditorTab? CreateEditorTab(string? path, string? workingDirectory = null)
    {
        if (_currentProject is null)
            return null;

        // No path means Helix's file picker rooted at the project.
        var command = path is null ? ToolCommand("hx.exe", ".") : ToolCommand("hx.exe", path);
        try
        {
            var host = new NativeTerminalHost("helix", command, workingDirectory ?? _currentProject, path);
            var tab = new EditorTab(path, host);

            EditorSlot.Children.Add(host);
            _editorTabs.Add(tab);
            _terminalHosts.Add(host);
            ScrollStripToEnd(EditorTabStripScroll);
            ActivateEditorTab(tab);
            return tab;
        }
        catch (Exception exception)
        {
            EditorSlot.Children.Add(CreateErrorPanel("helix", exception));
            return null;
        }
    }

    private void ActivateEditorTab(EditorTab tab)
    {
        foreach (var other in _editorTabs)
        {
            other.IsActive = ReferenceEquals(other, tab);
            other.Host.Visibility = other.IsActive ? Visibility.Visible : Visibility.Collapsed;
        }

        _activeEditorTab = tab;

        // The dot means "not looked at yet", so arriving here clears it. Without
        // this it would sit there for the rest of the session.
        tab.ClearAttention();

        // The renderer only exists once WPF has laid the host out at a real size, so
        // the raise has to wait for a pass that has actually happened.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(tab.Host.BringToFront));
    }


    // IsMouseOver only exists in the visual tree, so it is pushed onto the view model
    // and both visibilities are computed there. That is also what keeps the tab strips
    // out of FrameworkElement.Triggers, which accepts EventTrigger only.
    private void Tab_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IAttentiveTab tab })
            tab.SetHovered(true);
    }

    private void Tab_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IAttentiveTab tab })
            tab.SetHovered(false);
    }

    private void EditorTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: EditorTab tab })
            ActivateEditorTab(tab);
    }

    // Bound to the close button rather than a right-click on the tab, which is not a
    // gesture anyone would try.
    private void EditorTab_Close(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: EditorTab tab })
            CloseEditorTab(tab);
    }

    private void CloseEditorTab(EditorTab tab, bool refill = true)
    {
        _editorTabs.Remove(tab);
        _terminalHosts.Remove(tab.Host);

        // Detach before disposing, and never the other way round. The renderer is a
        // child HWND that is still in the visual tree at this point, and WPF can post
        // it a focus change at any moment -- including when the replacement host is
        // added below. Microsoft.Terminal.Wpf answers a focus change by writing to
        // the pseudoconsole, so a host whose ConPTY is already gone throws
        // InvalidOperationException out of an HWND message hook and takes the
        // process down with it.
        tab.Host.Visibility = Visibility.Collapsed;
        EditorSlot.Children.Remove(tab.Host);
        tab.Host.Dispose();

        // Land on a neighbour rather than leaving the pane blank. A caller that is
        // about to open its own replacement passes refill: false, or the pane would
        // fill up again before it does.
        if (_editorTabs.Count == 0)
        {
            if (refill)
                CreateEditorTab(null);
        }
        else
        {
            ActivateNeighbourAfter(tab);
        }

    }

    private void ActivateNeighbourAfter(EditorTab removed)
    {
        var index = _editorTabs.Count;
        for (var i = 0; i < _editorTabs.Count; i++)
        {
            if (_editorTabs[i].Host == removed.Host)
            {
                index = i;
                break;
            }
        }

        ActivateEditorTab(_editorTabs[Math.Min(index, _editorTabs.Count - 1)]);
    }

    // The + button used to raise a Windows file dialog, which pulled focus out of the
    // workspace for something Helide can already do. It opens a plain, unnamed Helix
    // in the project instead; files arrive by picking them in the left pane.
    private void EditorTabSplitButton_Click(object sender, RoutedEventArgs e) => CreateEditorTab(null);

    private void UpdatePaneState(TextBlock stateText, TerminalHostState state)
    {
        if (_closingWorkspace)
            return;

        stateText.Text = state switch
        {
            TerminalHostState.Starting => "starting",
            TerminalHostState.Ready => "ready",
            TerminalHostState.Failed => "failed",
            _ => "stopped",
        };
        stateText.Foreground = state switch
        {
            TerminalHostState.Ready => ThemePalette.Brush(ThemePalette.SuccessBrush),
            TerminalHostState.Failed => ThemePalette.Brush(ThemePalette.DangerBrush),
            _ => ThemePalette.Brush(ThemePalette.TextIdleBrush),
        };
    }

    private void ResetPaneStates()
    {
        foreach (var stateText in new[] { GitPaneState, ProjectPaneState })
        {
            stateText.Text = "starting";
            stateText.Foreground = ThemePalette.Brush(ThemePalette.TextIdleBrush);
        }
    }

    private static Border CreateErrorPanel(string label, Exception exception) => new()
    {
        Background = ThemePalette.Brush(ThemePalette.TerminalBrush),
        Child = new TextBlock
        {
            Margin = new Thickness(18),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemePalette.Brush(ThemePalette.DangerBrush),
            FontFamily = ThemePalette.Font(ThemePalette.UiMonoFontFamily),
            FontSize = ThemePalette.FontSize(ThemePalette.FontSizeSmall),
            Text = $"{label} could not start.\n\n{exception.Message}",
        },
    };


    private void MainWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.Control && e.Key == Key.P)
        {
            ToggleCommandPalette();
            e.Handled = true;
            return;
        }

        if (_paletteOpen)
        {
            // Palette navigation is handled here rather than only in the query box.
            //
            // Opened from a pane, the palette takes WPF focus but the OS focus can stay
            // on that pane's renderer HWND, so the query box's own handlers never run.
            // The trace showed the split cleanly: Escape and the arrows did nothing when
            // opened from a terminal and worked when opened from the window. This
            // handler receives keys either way, so it owns them.
            switch (e.Key)
            {
                case Key.Escape:
                    CloseCommandPalette();
                    e.Handled = true;
                    return;

                case Key.Down:
                    CommandPaletteControl.MoveSelection(1);
                    e.Handled = true;
                    return;

                case Key.Up:
                    CommandPaletteControl.MoveSelection(-1);
                    e.Handled = true;
                    return;

                case Key.Enter:
                    CommandPaletteControl.InvokeCurrent();
                    e.Handled = true;
                    return;
            }

            // Everything else belongs to the palette. This handler is Preview, which
            // tunnels from the root down, so anything swallowed here would never reach
            // the query box.
            //
            // If the popup is not the window actually receiving keys, the characters are
            // handed over by hand. Letting them fall through instead would send them to
            // the terminal behind the overlay, which is how typing silently typed into a
            // shell while the palette sat there looking focused.
            if (!PaletteHasOsFocus())
            {
                if (PrintableText(e.Key) is { } text)
                    CommandPaletteControl.AppendText(text);

                e.Handled = true;
                return;
            }

            return;
        }

        // The picker's navigation lives here for the palette's reason: opened by a
        // palette command, it can come up while a pane still holds the OS-level
        // keyboard, and its own handlers would never run.
        if (_folderPickerOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CloseFolderPicker();
                    e.Handled = true;
                    return;

                case Key.Down:
                    FolderPickerControl.MoveSelection(1);
                    e.Handled = true;
                    return;

                case Key.Up:
                    FolderPickerControl.MoveSelection(-1);
                    e.Handled = true;
                    return;

                case Key.Enter:
                    FolderPickerControl.InvokeCurrent();
                    e.Handled = true;
                    return;
            }

            if (!FolderPickerHasOsFocus())
            {
                if (PrintableText(e.Key) is { } folderText)
                    FolderPickerControl.AppendText(folderText);

                e.Handled = true;
                return;
            }

            return;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OpenProjectButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (!modifiers.HasFlag(ModifierKeys.Control))
            return;

        var number = KeyToNumber(e.Key);
        if (WelcomeRoot.Visibility == Visibility.Visible && number is >= 1 and <= 8)
        {
            var project = _state.RecentProjects.ElementAtOrDefault(number - 1);
            if (project is not null)
            {
                OpenRecentProject(project.Path);
                e.Handled = true;
            }
            return;
        }

        if (WorkspaceRoot.Visibility != Visibility.Visible || number is < 1 or > 5)
            return;

        // Ctrl+5 always means the project browser; Ctrl+1 means whichever left tool is
        // showing, so it can never focus a pane the user cannot see.
        if (number == 5)
            ToggleLeftTool(LeftTool.Project);

        // Ctrl+1 focuses whichever left tool is showing, Ctrl+2 the editor. Every
        // other index still resolves positionally, so the list order of the
        // remaining tools is unchanged.
        var host = number switch
        {
            1 => ActiveLeftHost(),
            2 => ActiveEditorHost(),
            _ => number <= _terminalHosts.Count ? _terminalHosts[number - 1] : null,
        };
        if (host is not null)
        {
            var panel = number switch
            {
                1 or 5 => ToolPanel.Left,
                3 => ToolPanel.Runner,
                4 => ToolPanel.Agent,
                _ => (ToolPanel?)null,
            };
            if (panel is { } target && IsPanelCollapsed(target))
                TogglePanel(target);

            host.FocusTerminal();
            e.Handled = true;
        }
    }

    private static int KeyToNumber(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => 1,
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        Key.D6 or Key.NumPad6 => 6,
        Key.D7 or Key.NumPad7 => 7,
        Key.D8 or Key.NumPad8 => 8,
        _ => 0,
    };

    private void LayoutSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SaveWorkspaceLayout();
        _stateStore.Save(_state);
    }

    private void RestoreWorkspaceLayout()
    {
        var layout = _workspace.Layout;

        // Absolute sizes win over ratios when we have them: a pane that was
        // manually resized reopens at exactly the width it was left at.
        LeftPaneColumn.Width = layout.LeftPixels > 0
            ? new GridLength(layout.LeftPixels)
            : new GridLength(ClampRatio(layout.LeftRatio, 0.21), GridUnitType.Star);
        CenterPaneColumn.Width = new GridLength(ClampRatio(layout.CenterRatio, 0.58), GridUnitType.Star);
        RightPaneColumn.Width = new GridLength(ClampRatio(layout.RightRatio, 0.21), GridUnitType.Star);
        EditorPaneRow.Height = new GridLength(ClampRatio(layout.EditorRatio, 0.80), GridUnitType.Star);
        RunnerPaneRow.Height = layout.RunnerPixels > 0
            ? new GridLength(layout.RunnerPixels)
            : new GridLength(ClampRatio(layout.RunnerRatio, 0.20), GridUnitType.Star);

        _leftPanePixels = layout.LeftPixels;
        _rightPanePixels = layout.RightPixels;
        _runnerPanePixels = layout.RunnerPixels;

        // A collapsed pane keeps no pixel width to measure, so freeze its last star
        // weight now; SaveWorkspaceLayout scales it back in as the visible panes grow
        // or shrink. Without this, collapsing would overwrite the ratio with zero.
        _frozenLeftWeight = ClampRatio(layout.LeftRatio, 0.21);
        _frozenRightWeight = ClampRatio(layout.RightRatio, 0.21);
        _frozenRunnerWeight = ClampRatio(layout.RunnerRatio, 0.20);

        SetPanelCollapsed(ToolPanel.Left, layout.LeftCollapsed);
        SetPanelCollapsed(ToolPanel.Runner, layout.RunnerCollapsed);
        SetPanelCollapsed(ToolPanel.Agent, layout.AgentCollapsed);
    }

    private void CaptureTrackDefaults()
    {
        _leftPaneMinWidth = LeftPaneColumn.MinWidth;
        _leftSplitterWidth = LeftSplitterColumn.Width;
        _rightPaneMinWidth = RightPaneColumn.MinWidth;
        _rightSplitterWidth = RightSplitterColumn.Width;
        _runnerMinHeight = RunnerPaneRow.MinHeight;
        _runnerSplitterHeight = RunnerSplitterRow.Height;
    }

    // Each left tool owns its own toggle: clicking git shows git, clicking project
    // shows the project browser, and clicking whichever is already showing hides it.
    private void GitPanelToggle_Click(object sender, RoutedEventArgs e) =>
        ToggleLeftTool(LeftTool.Git);

    private void ProjectPanelToggle_Click(object sender, RoutedEventArgs e) =>
        ToggleLeftTool(LeftTool.Project);

    private void TerminalPanelToggle_Click(object sender, RoutedEventArgs e) =>
        TogglePanel(ToolPanel.Runner);

    private void AgentPanelToggle_Click(object sender, RoutedEventArgs e) =>
        TogglePanel(ToolPanel.Agent);

    private void ToggleLeftTool(LeftTool tool)
    {
        // Clicking the tool that is already showing hides the panel, which is what
        // makes these behave like the terminal and agent toggles.
        if (!IsPanelCollapsed(ToolPanel.Left) && _activeLeftTool == tool)
        {
            TogglePanel(ToolPanel.Left);
            return;
        }

        SetPanelCollapsed(ToolPanel.Left, false);
        SetLeftTool(tool);
        SaveWorkspaceLayout();
        _stateStore.Save(_state);
    }

    private LeftTool RestoreLeftTool() =>
        string.Equals(_workspace.Layout.LeftTool, "project", StringComparison.OrdinalIgnoreCase)
            ? LeftTool.Project
            : LeftTool.Git;

    private void SetLeftTool(LeftTool tool, bool persist = true)
    {
        _activeLeftTool = tool;
        var showingGit = tool == LeftTool.Git;

        LeftPaneTitle.Text = showingGit ? "GIT" : "PROJECT";
        GitPaneState.Visibility = showingGit ? Visibility.Visible : Visibility.Collapsed;
        ProjectPaneState.Visibility = showingGit ? Visibility.Collapsed : Visibility.Visible;

        GitPanelToggle.IsChecked = showingGit;
        ProjectPanelToggle.IsChecked = !showingGit;

        // Swap the process rather than the visibility of a stacked sibling: there is
        // only ever one child window behind this pane, so it always owns the clicks.
        if (_leftHost is null || _leftHostTool != tool)
        {
            StopLeftTool();
            try
            {
                StartLeftTool(tool);
            }
            catch (Exception exception)
            {
                LeftToolStack.Children.Add(CreateErrorPanel(
                    showingGit ? "lazygit" : "yazi", exception));
                var stateText = showingGit ? GitPaneState : ProjectPaneState;
                stateText.Text = "failed";
                stateText.Foreground = ThemePalette.Brush(ThemePalette.DangerBrush);
            }
        }
        else
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(_leftHost.BringToFront));
        }

        if (persist)
        {
            _workspace.Layout.LeftTool = showingGit ? "git" : "project";
            _stateStore.Save(_state);
        }
    }

    private void TogglePanel(ToolPanel panel)
    {
        SetPanelCollapsed(panel, !IsPanelCollapsed(panel));
        // Save synchronously so persistence works immediately (not async)
        SaveWorkspaceLayout();
        _stateStore.Save(_state);
    }

    // Reading ActualWidth/ActualHeight immediately after changing a track still
    // returns the pre-collapse measurement, so the saved ratios would be wrong and
    // the pane would come back at the wrong size on the next launch. Saving is
    // deferred until layout has settled.
    private void SaveLayoutAfterLayoutPasses()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            SaveWorkspaceLayout();
            _stateStore.Save(_state);
        }));
    }

    private bool IsPanelCollapsed(ToolPanel panel) => panel switch
    {
        ToolPanel.Left => _workspace.Layout.LeftCollapsed,
        ToolPanel.Runner => _workspace.Layout.RunnerCollapsed,
        _ => _workspace.Layout.AgentCollapsed,
    };

    // The track has to be driven to zero explicitly: a collapsed child does not
    // shrink a star-sized track, and MinWidth/MinHeight would win over a zero
    // Width/Height. The companion splitter track goes too, or a 3px divider is
    // left hanging beside nothing. Collapsing the container then hides the child
    // HWND via SW_HIDE, which keeps the ConPTY session alive and never measures
    // the terminal at 0x0.
    //
    // Re-expanding restores the remembered pixel width instead of the star
    // weight. With star sizing the freed space is redistributed across every
    // visible track, so a pane that was 400px wide reopens noticeably narrower
    // each time. Absolute widths are unaffected by that redistribution.
    private void SetPanelCollapsed(ToolPanel panel, bool collapsed)
    {
        // Snapshot before the track is zeroed, while its weight is still readable.
        if (collapsed)
            FreezeWeight(panel);

        // Only touch the track when the collapsed state actually changes. Tool
        // switches call this with collapsed=false on an already-visible pane, and
        // rewriting Width there is what snapped panels back to a stale size.
        if (IsPanelCollapsed(panel) != collapsed)
        {
            switch (panel)
            {
                case ToolPanel.Left:
                    LeftPaneColumn.Width = new GridLength(
                        collapsed ? 0 : RememberedLeftWidth());
                    LeftPaneColumn.MinWidth = collapsed ? 0 : _leftPaneMinWidth;
                    LeftSplitterColumn.Width = collapsed ? new GridLength(0) : _leftSplitterWidth;
                    LeftPane.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case ToolPanel.Runner:
                    RunnerPaneRow.Height = new GridLength(
                        collapsed ? 0 : RememberedRunnerHeight());
                    RunnerPaneRow.MinHeight = collapsed ? 0 : _runnerMinHeight;
                    RunnerSplitterRow.Height = collapsed ? new GridLength(0) : _runnerSplitterHeight;
                    RunnerPane.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case ToolPanel.Agent:
                    RightPaneColumn.Width = new GridLength(
                        collapsed ? 0 : RememberedRightWidth());
                    RightPaneColumn.MinWidth = collapsed ? 0 : _rightPaneMinWidth;
                    RightSplitterColumn.Width = collapsed ? new GridLength(0) : _rightSplitterWidth;
                    AgentPane.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                    break;
            }
        }

        switch (panel)
        {
            case ToolPanel.Left:
                _workspace.Layout.LeftCollapsed = collapsed;
                break;
            case ToolPanel.Runner:
                _workspace.Layout.RunnerCollapsed = collapsed;
                break;
            case ToolPanel.Agent:
                _workspace.Layout.AgentCollapsed = collapsed;
                break;
        }

        if (!collapsed)
            RequestPaneRefresh(panel);

        PanelToggleFor(panel).IsChecked = !collapsed;
    }

    // Remembered sizes come from the last time the pane was laid out, falling back
    // to the persisted pixel size and finally to the ratio applied to whatever
    // the window currently offers. Guarantees a sane non-zero result so a pane
    // can never reopen at zero width.
    private double RememberedLeftWidth() => FirstPositive(
        _leftPanePixels,
        _workspace.Layout.LeftPixels,
        ClampRatio(_workspace.Layout.LeftRatio, 0.21) * Math.Max(LeftPaneColumn.ActualWidth, 400));

    private double RememberedRightWidth() => FirstPositive(
        _rightPanePixels,
        _workspace.Layout.RightPixels,
        Math.Max(ClampRatio(_workspace.Layout.RightRatio, 0.21) * Math.Max(ActualWidth > 0 ? ActualWidth : 1500, 1200), 250));

    private double RememberedRunnerHeight() => FirstPositive(
        _runnerPanePixels,
        _workspace.Layout.RunnerPixels,
        Math.Max(ClampRatio(_workspace.Layout.RunnerRatio, 0.20) * Math.Max(ActualHeight > 0 ? ActualHeight : 900, 600), 150));

    private static double FirstPositive(params double[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (double.IsFinite(candidate) && candidate > 1)
                return candidate;
        }

        return 200;
    }

    private void FreezeWeight(ToolPanel panel)
    {
        switch (panel)
        {
            case ToolPanel.Left:
                _frozenLeftWeight = ClampRatio(LeftPaneColumn.Width.Value, 0.21);
                _leftPanePixels = LeftPaneColumn.ActualWidth;
                break;
            case ToolPanel.Runner:
                _frozenRunnerWeight = ClampRatio(RunnerPaneRow.Height.Value, 0.20);
                _runnerPanePixels = RunnerPaneRow.ActualHeight;
                break;
            case ToolPanel.Agent:
                _frozenRightWeight = ClampRatio(RightPaneColumn.Width.Value, 0.21);
                _rightPanePixels = RightPaneColumn.ActualWidth;
                break;
        }
    }

    private ToggleButton PanelToggleFor(ToolPanel panel) => panel switch
    {
        ToolPanel.Left => GitPanelToggle,
        ToolPanel.Runner => TerminalPanelToggle,
        _ => AgentPanelToggle,
    };

    // The hosted renderer repaints itself once the pane is arranged at a real
    // size, so this is only a nudge for the odd stale frame after SW_HIDE.
    private void RequestPaneRefresh(ToolPanel panel)
    {
        var slot = panel switch
        {
            ToolPanel.Runner => RunnerSlot,
            _ => AgentSlot,
        };
        var host = panel == ToolPanel.Left ? ActiveLeftHost() : slot.Content as NativeTerminalHost;

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => host?.Refresh()));
    }

    private NativeTerminalHost? ActiveLeftHost() => _leftHost;

    private NativeTerminalHost? ActiveEditorHost() => _activeEditorTab?.Host;

    // Agent new session: opens the flyout
    private void AgentNewSessionButton_Click(object sender, RoutedEventArgs e) =>
        AgentNewSessionFlyout.IsOpen = true;

    // Create a new OpenCode session replacing the panel
    private void NewOpenCodeButton_Click(object sender, RoutedEventArgs e)
    {
        AgentNewSessionFlyout.IsOpen = false;
        CreateAgentSession("opencode", "OpenCode");
    }

    // Create a new Codex session replacing the panel
    private void NewCodexButton_Click(object sender, RoutedEventArgs e)
    {
        AgentNewSessionFlyout.IsOpen = false;
        CreateAgentSession("codex", "Codex");
    }

    private void AttachStatusSource(AgentSessionView session, string type, int port)
    {
        if (session.Host is not { } host)
            return;

        // The OpenCode TUI serves this pane's own server on the pinned port, so the
        // source can read real turn events. Codex has no HTTP surface, but it writes
        // task_started/task_complete into the rollout file it already maintains, which
        // is the same kind of first-party signal rather than an inference.
        //
        // The rollout is looked up by session id, which resolves for a pane that resumed
        // a known thread and for one whose id was just captured, and stays null until
        // the pane has been used at all. Only then is there genuinely nothing to read,
        // and the quiet-period source covers that window instead of guessing once a real
        // signal exists.
        session.AttachStatusSource(type switch
        {
            "opencode" when port > 0 => new OpenCodeStatusSource(port, Dispatcher),
            "codex" => new RolloutStatusSource(
                () => session.SessionId is { Length: > 0 } id
                    ? CodexSessions.FindRolloutPath(id)
                    : null,
                Dispatcher),
            _ => new QuietPeriodStatusSource(host),
        });
    }

    // The command line and the status source must agree on the port, so both are
    // built together here rather than by each caller. That is also why this no
    // longer takes a commandLine: passing one in was how they drifted apart.
    private void CreateAgentSession(string type, string typeLabel, string? resumeSessionId = null, bool resuming = false, string? workingDirectory = null)
    {
        if (_currentProject is null)
            return;

        var port = type == "codex" ? 0 : ReserveLocalPort();

        // A codex pane only has a session id left to learn when its launch carries no
        // resume argument, and that is decided by the id we were handed, not by whether
        // this is a restore. A pane being restored with no saved id starts empty and
        // still goes on to write a rollout file, so it has to stay a candidate for one,
        // otherwise its conversation is dropped again on every launch.
        //
        // A pane launched with `resume <id>` already knows its thread, so it is never a
        // candidate: that is what stops a rollout produced by one pane being handed to
        // another and moving it onto a different conversation.
        //
        // `--last` used to be the fallback for a codex pane with no saved id, but it
        // resumes the newest conversation in the directory rather than this pane's
        // own, so two id-less panes both landed on the same thread and a pane could
        // reopen a conversation it had never been in. Starting empty is recoverable
        // -- the thread is still offered by `codex resume --all` -- whereas silently
        // opening the wrong conversation is not.
        var codexResumeId = type == "codex" && resuming && resumeSessionId is { Length: > 0 } savedCodexId
            ? savedCodexId
            : null;
        var startsFreshCodex = type == "codex" && codexResumeId is null;

        // Taken before the host is built, so it always precedes the rollout file the
        // process is about to write.
        var launchedUtc = DateTime.UtcNow;

        var commandLine = type == "codex"
            ? codexResumeId is { } codexId
                ? ToolCommand("codex.cmd", "resume", codexId)
                : ToolCommand("codex.cmd")
            : resuming && resumeSessionId is { Length: > 0 } id
                ? ToolCommand("opencode.cmd", "-s", id, "--port", port.ToString())
                : ToolCommand("opencode.cmd", "--port", port.ToString());


        try
        {
            // Create the new session host
            var host = new NativeTerminalHost(type, commandLine, workingDirectory ?? _currentProject!);

            // Replace the agent panel content
            AgentSlot.Content = host;
            _terminalHosts.Add(host);

            var newSession = new AgentSessionView(type, typeLabel);
            newSession.Host = host;
            newSession.StatusPort = port;
            newSession.SessionId = resumeSessionId;
            if (startsFreshCodex)
                newSession.AwaitingSessionSince = launchedUtc;
            AttachStatusSource(newSession, type, port);
            foreach (var s in _agentSessions)
                s.IsActive = false;
            newSession.IsActive = true;
            _agentSessions.Add(newSession);
            _activeAgentSession = newSession;

            // Update UI
            AgentPaneTitle.Text = typeLabel.ToUpper();
            UpdateAgentSessionUI();
        }
        catch (Exception exception)
        {
            AgentSlot.Content = CreateErrorPanel(type, exception);
        }
    }
    private void AgentSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AgentSessionView session)
        {
            // Deactivate all, activate selected
            foreach (var s in _agentSessions)
                s.IsActive = ReferenceEquals(s, session);

            if (session.Host is not null)
            {
                AgentSlot.Content = session.Host;
                AgentPaneTitle.Text = session.TypeLabel.ToUpper();
                _activeAgentSession = session;
                session.ClearAttention();
                session.Host.BringToFront();
            }
            UpdateAgentSessionUI();
        }
    }

    // Update session list and title
    private void UpdateAgentSessionUI()
    {
        AgentTabStrip.ItemsSource = null;
        AgentTabStrip.ItemsSource = _agentSessions;
        ScrollStripToEnd(AgentTabStripScroll);
    }

    private void InitializeAgentSessions(string project)
    {
        _agentSessions.Clear();

        // Load saved sessions for this project from AppState
        var savedSessions = _workspace.AgentSessions;
        if (savedSessions.Count > 0)
        {
            foreach (var saved in savedSessions)
            {
                var sessionType = saved.Type;
                var sessionName = string.IsNullOrEmpty(saved.Name) ? sessionType.ToUpper() : saved.Name;
                try
                {
                    CreateAgentSession(sessionType, sessionName, saved.SessionId, resuming: true);
                }
                catch
                {
                    // Skip failed sessions
                }
            }
        }
        else
        {
            // Default: create an OpenCode session
            CreateAgentSession("opencode", "OpenCode");
        }

        // Each newly created session is made active, so without re-asserting this on
        // restore the last-created one always wins rather than the one you left on.
        var activeIndex = _workspace.ActiveAgentIndex;
        if (activeIndex >= 0 && activeIndex < _agentSessions.Count)
        {
            foreach (var s in _agentSessions)
                s.IsActive = false;

            var target = _agentSessions[activeIndex];
            target.IsActive = true;
            _activeAgentSession = target;

            if (target.Host is not null)
            {
                AgentSlot.Content = target.Host;
                AgentPaneTitle.Text = target.TypeLabel.ToUpper();
            }

            UpdateAgentSessionUI();
            AgentTabStrip.Items.Refresh();
        }
    }

    // Asks the OS for a free loopback port by binding to 0 and immediately releasing
    // it. There is an unavoidable race here, but the window is small and both sides
    // bind only to loopback.
    private static int ReserveLocalPort()
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void InitializeRunnerSessions(string project)
    {
        _runnerSessions.Clear();
        // Default runner session with startup message is created in OpenWorkspace
    }

    private int RestoreEditorTabs()
    {
        var restored = 0;

        foreach (var saved in _workspace.EditorTabs)
        {
            // A file that moved or was deleted between sessions opens as a Helix
            // error pane, so skip it and let the file picker take over.
            if (saved.Path is not null && !File.Exists(saved.Path))
                continue;

            if (CreateEditorTab(saved.Path) is not null)
                restored++;
        }

        return restored;
    }

    private static double ClampRatio(double value, double fallback) =>
        double.IsFinite(value) && value > 0.02 ? value : fallback;

    private void SaveWorkspaceLayout()
    {
        var frozenWidth = (_workspace.Layout.LeftCollapsed ? _frozenLeftWeight : 0)
                        + (_workspace.Layout.AgentCollapsed ? _frozenRightWeight : 0);
        var visibleWidth = LeftPaneColumn.ActualWidth
                         + CenterPaneColumn.ActualWidth
                         + RightPaneColumn.ActualWidth;
        var widthShare = 1 - frozenWidth;
        if (visibleWidth > 0 && widthShare > 0.05)
        {
            var totalWidth = visibleWidth / widthShare;
            if (!_workspace.Layout.LeftCollapsed)
            {
                _workspace.Layout.LeftRatio = LeftPaneColumn.ActualWidth / totalWidth;
                _workspace.Layout.LeftPixels = LeftPaneColumn.ActualWidth;
            }
            _workspace.Layout.CenterRatio = CenterPaneColumn.ActualWidth / totalWidth;
            if (!_workspace.Layout.AgentCollapsed)
            {
                _workspace.Layout.RightRatio = RightPaneColumn.ActualWidth / totalWidth;
                _workspace.Layout.RightPixels = RightPaneColumn.ActualWidth;
            }
        }

        var frozenHeight = _workspace.Layout.RunnerCollapsed ? _frozenRunnerWeight : 0;
        var visibleHeight = EditorPaneRow.ActualHeight + RunnerPaneRow.ActualHeight;
        var heightShare = 1 - frozenHeight;
        if (visibleHeight > 0 && heightShare > 0.05)
        {
            var totalHeight = visibleHeight / heightShare;
            _workspace.Layout.EditorRatio = EditorPaneRow.ActualHeight / totalHeight;
            if (!_workspace.Layout.RunnerCollapsed)
            {
                _workspace.Layout.RunnerRatio = RunnerPaneRow.ActualHeight / totalHeight;
                _workspace.Layout.RunnerPixels = RunnerPaneRow.ActualHeight;
            }
        }
    }

    private void RestoreWindowGeometry()
    {
        var geometry = _workspace.Window;
        if (geometry.Width < MinWidth || geometry.Height < MinHeight)
            return;

        var visible = geometry.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                      geometry.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100 &&
                      geometry.Left + geometry.Width > SystemParameters.VirtualScreenLeft + 100 &&
                      geometry.Top + geometry.Height > SystemParameters.VirtualScreenTop + 100;
        if (!visible)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = geometry.Left;
        Top = geometry.Top;
        Width = geometry.Width;
        Height = geometry.Height;
    }

    private void SaveWindowGeometry()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        _workspace.Window.Left = bounds.Left;
        _workspace.Window.Top = bounds.Top;
        _workspace.Window.Width = bounds.Width;
        _workspace.Window.Height = bounds.Height;
        // Always reopen restored
        _workspace.Window.IsMaximized = false;
    }

    private void CloseWorkspace()
    {
        _closingWorkspace = true;
        try
        {
            // Detach every host before disposing any of them, for the same reason
            // CloseEditorTab does: a host that is still in the visual tree can be sent
            // a focus change, and answering that on a torn-down pseudoconsole throws
            // out of an HWND message hook.
            LeftToolStack.Children.Clear();
            EditorSlot.Children.Clear();
            RunnerSlot.Content = null;
            AgentSlot.Content = null;

            foreach (var host in _terminalHosts.ToArray())
                host.Dispose();

            _terminalHosts.Clear();
            _editorTabs.Clear();
            _activeEditorTab = null;
            _runnerHost = null;
            _leftHost = null;
        }
        finally
        {
            _closingWorkspace = false;
        }
    }

    // The startup script is handed to pwsh as -EncodedCommand rather than -Command.
    // Base64 has no quotes, spaces or backslashes in it, so nothing can be mangled on
    // the way in. Quoting by hand does not work here: the string has to survive two
    // parsers with different escaping rules. CommandLineToArgvW reads a backslash as
    // an escape, PowerShell does not, so `hx "C:\dir\a.txt"` reached hx.exe as
    // `C:\dir\a.txt\`` and helix dutifully opened an empty buffer.
    //
    // No -NoProfile: this is only used by the runner, which is a real shell and
    // where the ~360ms of profile loading is worth paying, because the profile is
    // what puts the prompt and the command history there. The panes that are on the
    // interactive path -- editor tabs and the left tool -- do not go through pwsh at
    // all any more, so they are not paying it either.
    private static string PowerShellCommand(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"pwsh.exe -NoLogo -NoExit -EncodedCommand {encoded}";
    }

    // Single quotes are the only PowerShell literal form that takes a path verbatim,
    // so callers embed paths with this rather than with escaped double quotes.
    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''") + "'";

    // Every tool is launched directly, with no shell in front of it. pwsh needs
    // 450-800ms to reach a prompt, and helix/yazi/lazygit need 30-70ms, so wrapping
    // them was the entire cost of opening a file and of switching the left tool --
    // an ~800ms wait that read as the app being sluggish. Only the runner pane, which
    // is a shell by definition, still goes through pwsh.
    private static string ToolCommand(string executable, params string[] arguments)
    {
        var command = new StringBuilder(executable);
        foreach (var argument in arguments)
            command.Append(' ').Append(QuoteArgument(argument));

        return command.ToString();
    }

    // Quotes an argument the way CommandLineToArgvW expects: only when it has to be,
    // and with every run of backslashes before a quote (or before the closing quote)
    // doubled, since a lone backslash there would escape the quote.
    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
            return argument;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            quoted.Append(character);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private void SaveEditorTabs()
    {
        _workspace.EditorTabs.Clear();
        foreach (var tab in _editorTabs)
            _workspace.EditorTabs.Add(new EditorTabState { Path = tab.Path });
    }

    // Records which conversation each agent pane is on, so the next launch reopens it
    // instead of starting empty. A pane that cannot answer simply gets no id and starts
    // fresh, which is the old behaviour rather than a failure.
    private System.Windows.Threading.DispatcherTimer? _sessionCaptureTimer;

    private void StartSessionCaptureTimer()
    {
        _sessionCaptureTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _sessionCaptureTimer.Tick += (_, _) => CaptureAgentSessionIds();
        _sessionCaptureTimer.Start();

        CaptureAgentSessionIds();
    }

    private void CaptureAgentSessionIds()
    {
        foreach (var session in _agentSessions)
        {
            if (session.Type == "opencode")
            {
                // Live signal wins (it is what the pane is on now); otherwise keep
                // whatever was saved. No HTTP fallback: a pane that has produced no
                // events is left id-less, so it restarts fresh instead of adopting
                // some other thread in the directory.
                session.SessionId = session.LiveSessionId ?? session.SessionId;
            }
        }

        // Codex: a pane started with no resume argument is on a thread that does not
        // exist yet, and the rollout file it writes is the first place that thread's
        // id appears. Pair each rollout with the pane that had been waiting longest --
        // and that was already running when the file appeared -- instead of walking
        // the tab list. Tab order and file order are not the same order once panes
        // start together, and a mismatch is a pane that resumes somebody else's
        // conversation. The started-before check also stops a stale rollout from an
        // earlier launch being claimed by a pane that had not started yet.
        foreach (var rollout in CodexSessions.NewRollouts())
        {
            // Resuming a session can make codex write a rollout whose session id is the
            // one being resumed, so this file belongs to a pane that already has its id
            // and not to a pane still waiting for one. Left unguarded, a tab opened later
            // would claim it and reopen the conversation the resumed pane is sitting in.
            if (_agentSessions.Any(s => s.SessionId == rollout.SessionId))
            {
                CodexSessions.MarkConsumed(rollout);
                continue;
            }

            var pane = _agentSessions
                .Where(s => s.Type == "codex"
                    && s.SessionId is null
                    && s.AwaitingSessionSince is { } since
                    && since <= rollout.CreatedUtc)
                .OrderBy(s => s.AwaitingSessionSince)
                .FirstOrDefault();

            // Nothing is waiting, so no later rollout can be claimed either. Leaving
            // them unconsumed keeps them available to a pane opened in a moment.
            if (pane is null)
                break;

            pane.SessionId = rollout.SessionId;
            pane.AwaitingSessionSince = null;
            CodexSessions.MarkConsumed(rollout);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        CaptureAgentSessionIds();

        _workspace.AgentSessions.Clear();
        // IsActive is updated on every tab click, so it is the reliable witness of
        // what the user had selected. _activeAgentSession alone could be null at
        // close, which is how -1 was being written.
        var active = _agentSessions.Select((session, index) => (session, index))
            .FirstOrDefault(x => x.session.IsActive);
        _workspace.ActiveAgentIndex = active.session is null ? -1 : active.index;

        foreach (var session in _agentSessions)
        {
            _workspace.AgentSessions.Add(new AgentSessionState
            {
                Type = session.Type,
                Name = session.Name,
                CommandLine = session.Host is not null ? session.Host.Label : string.Empty,
                CreatedUtc = DateTime.UtcNow,
                SessionId = session.SessionId,
            });
        }

        SaveEditorTabs();
        SaveWorkspaceLayout();
        SaveWindowGeometry();

        // Only when a project is open: with no project there is nothing to key a
        // workspace file against, and writing one would create an orphan per launch
        // from the welcome screen.
        if (_currentProject is not null)
            _stateStore.SaveWorkspace(_workspace, _currentProject);

        _stateStore.Save(_state);
        CloseWorkspace();

        _sessionCaptureTimer?.Stop();
        _openRequestWatcher?.Dispose();
        if (_openRequestPath is not null && File.Exists(_openRequestPath))
        {
            try
            {
                File.Delete(_openRequestPath);
            }
            catch
            {
            }
        }
    }

    // One tab per Helix process. IsActive drives which renderer is visible, the
    // label colours, and the underline that marks the tab in use.
    private sealed class EditorTab : INotifyPropertyChanged, IAttentiveTab
    {
        private bool _isActive;
        private bool _needsAttention;

        public EditorTab(string? path, NativeTerminalHost host)
        {
            Path = path;
            Host = host;
        }

        public string? Path { get; }

        public NativeTerminalHost Host { get; }


        // True once a session has gone quiet after working while this tab was in
        // the background. Cleared when the tab is looked at, so it always means
        // "not seen yet" rather than "happened at some point".
        public bool NeedsAttention => _needsAttention;

        public string Name => Path is null ? "files" : System.IO.Path.GetFileName(Path);

        public string ToolTipText => Path ?? "Project file picker";

        public System.Windows.Media.Brush Foreground => IsActive
            ? ThemePalette.Brush(ThemePalette.TextBrush)
            : ThemePalette.Brush(ThemePalette.TextSubtleBrush);

        // The active tab carries an accent underline as well as a lighter fill, so
        // which one is in use reads at a glance rather than only on hover.
        public System.Windows.Media.Brush Background => IsActive
            ? ThemePalette.Brush(ThemePalette.TabActiveBackgroundBrush)
            : System.Windows.Media.Brushes.Transparent;

        public System.Windows.Media.Brush Underline => IsActive
            ? ThemePalette.Brush(ThemePalette.AccentBrush)
            : System.Windows.Media.Brushes.Transparent;

        public FontWeight Weight => IsActive ? FontWeights.SemiBold : FontWeights.Normal;

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value)
                    return;

                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Underline)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Weight)));
            }
        }

        public void RefreshTheme()
        {
            // These three are re-read on every get, so the values are already
            // correct after a palette swap. Nothing pushes them at the binding
            // though, which is what left the tab strip on the previous theme's
            // colours until a different tab was activated.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Underline)));
        }

        private ISessionStatusSource? _statusSource;

        public void AttachStatusSource(ISessionStatusSource source)
        {
            _statusSource?.Dispose();
            _statusSource = source;

            // Marking only means anything while the tab is in the background. If the
            // agent finishes before you ever look away there is nothing to catch up
            // on, and a dot would just be noise.
            source.BecameIdle += () =>
            {
                if (!IsActive)
                    MarkAttention();
            };

            source.Start();
        }

        private bool _hovered;

        // Hover wins over the mark, so a marked tab still offers its close button.
        public bool ShowAttentionDot => _needsAttention && !_hovered;

        public bool ShowCloseButton => !_needsAttention || _hovered;

        public void SetHovered(bool hovered)
        {
            if (_hovered == hovered)
                return;

            _hovered = hovered;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAttentionDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCloseButton)));
        }

        public void ClearAttention() => MarkAttention(clear: true);

        private void MarkAttention(bool clear = false)
        {
            if (_needsAttention == !clear)
                return;

            _needsAttention = !clear;

            // All three, not just NeedsAttention: the tab templates bind the two
            // derived booleans, so notifying only the flag leaves the dot and the
            // close button showing whatever they were before.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsAttention)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAttentionDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCloseButton)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record RecentProjectView(string Name, string Path, string Shortcut);

    // Agent tab strip click - switch sessions
    private void AgentTabStrip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is AgentSessionView session)
        {
            // Deactivate all, activate this one
            foreach (var s in _agentSessions)
                s.IsActive = ReferenceEquals(s, session);

            // Switch to this session
            if (session.Host is not null)
            {
                AgentSlot.Content = session.Host;
                AgentPaneTitle.Text = session.TypeLabel.ToUpper();
                _activeAgentSession = session;
                session.ClearAttention();
                session.Host.BringToFront();
            }
            UpdateAgentSessionUI();
            AgentTabStrip.Items.Refresh();
        }
    }

    // Close agent session tab
    private void AgentTabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AgentSessionView session })
            CloseAgentSession(session);
    }

    // Split out from the click handler so the command palette can close a session too.
    // It reads the session off the button, and a palette entry has no button to hand
    // it; this keeps the one place that knows how to dispose a host and drop the
    // session from both the list and the status sources.
    private void CloseAgentSession(AgentSessionView session)
    {
        session.Host?.Dispose();

        _agentSessions.Remove(session);
        if (session.Host is not null && _terminalHosts.Contains(session.Host))
            _terminalHosts.Remove(session.Host);

        // If this was the active session, switch to another one or create default
        if (ReferenceEquals(_activeAgentSession, session))
        {
            if (_agentSessions.Count > 0)
            {
                var nextSession = _agentSessions.Last();
                nextSession.IsActive = true;
                AgentSlot.Content = nextSession.Host;
                AgentPaneTitle.Text = nextSession.TypeLabel.ToUpper();
                _activeAgentSession = nextSession;
                nextSession.Host?.BringToFront();
            }
            else
            {
                AgentSlot.Content = null;
                AgentPaneTitle.Text = "OPENCODE";
                _activeAgentSession = null;
            }
        }

        UpdateAgentSessionUI();
    }
    private void RunnerTabStrip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is AgentSessionView session)
        {
            foreach (var s in _runnerSessions)
                s.IsActive = ReferenceEquals(s, session);

            if (session.Host is not null)
            {
                RunnerSlot.Content = session.Host;
                _activeRunnerSession = session;
                session.ClearAttention();
                session.Host.BringToFront();
            }
            RunnerTabStrip.Items.Refresh();
        }
    }

    // Runner session close
    private void RunnerTabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AgentSessionView session)
        {
            session.Host?.Dispose();
            _runnerSessions.Remove(session);
            if (session.Host is not null && _terminalHosts.Contains(session.Host))
                _terminalHosts.Remove(session.Host);

            if (ReferenceEquals(_activeRunnerSession, session))
            {
                if (_runnerSessions.Count > 0)
                {
                    var nextSession = _runnerSessions.Last();
                    nextSession.IsActive = true;
                    RunnerSlot.Content = nextSession.Host;
                    _activeRunnerSession = nextSession;
                    nextSession.Host?.BringToFront();
                }
                else
                {
                    RunnerSlot.Content = null;
                    _activeRunnerSession = null;
                }
            }
            RunnerTabStrip.Items.Refresh();
        }
    }

    private void RunnerNewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        CreateRunnerSession("pwsh", "pwsh", PowerShellCommand("pwsh -NoLogo -NoExit"));
    }

    private void CreateRunnerSession(string type, string label, string commandLine, string? workingDirectory = null)
    {
        if (_currentProject is null) return;
        try
        {
            var host = new NativeTerminalHost(type, commandLine, workingDirectory ?? _currentProject!);
            RunnerSlot.Content = host;
            _terminalHosts.Add(host);

var session = new AgentSessionView(type, label);
                session.Host = host;
                session.CommandLine = commandLine;
                foreach (var s in _runnerSessions)
                    s.IsActive = false;
            session.IsActive = true;
            _runnerSessions.Add(session);
            _activeRunnerSession = session;
            RunnerTabStrip.ItemsSource = null;
            RunnerTabStrip.ItemsSource = _runnerSessions;
            ScrollStripToEnd(RunnerTabStripScroll);
        }
        catch (Exception exception)
        {
            RunnerSlot.Content = CreateErrorPanel(type, exception);
        }
    }

    private sealed class AgentSessionView : INotifyPropertyChanged, IAttentiveTab
    {
        private bool _isActive;
        private bool _needsAttention;

        // See the same note on EditorTab: cleared on focus, so the dot always means
        // "not looked at yet".
        public bool NeedsAttention => _needsAttention;

        public string Type { get; }  // "opencode" or "codex"
        public string TypeLabel => Type == "codex" ? "Codex" : "OpenCode";
        public string Name { get; }
        public NativeTerminalHost? Host { get; set; }

        // Only set for runner sessions, which are a shell running a command line and
        // can therefore be recreated from it. Agent sessions build their own command
        // line from Type, since it has to carry the pinned status port.
        public string CommandLine { get; set; } = string.Empty;

        // The port pinned into that command line, needed again at save time to ask
        // this pane's server which session it is on.
        public int StatusPort { get; set; }

        // What the pane's own event stream says it is on.
        public string? LiveSessionId => (_statusSource as OpenCodeStatusSource)?.SessionId;

        // Filled in at save time so the next launch can reopen this conversation.
        public string? SessionId { get; set; }

        // Set when this pane was launched as a brand new codex conversation, i.e.
        // with no resume argument, and holds the moment the process was started.
        // Only such a pane has a session id still to learn; a pane launched with
        // `resume <id>` was already told which thread to open. The timestamp is what
        // decides which rollout file belongs to which pane when several start at once.
        public DateTime? AwaitingSessionSince { get; set; }

        public AgentSessionView(string type, string name)
        {
            Type = type;
            Name = name;
        }

        public System.Windows.Media.Brush Background => IsActive
            ? ThemePalette.Brush(ThemePalette.TabActiveBackgroundBrush)
            : System.Windows.Media.Brushes.Transparent;

        public System.Windows.Media.Brush Underline => IsActive
            ? ThemePalette.Brush(ThemePalette.AccentBrush)
            : System.Windows.Media.Brushes.Transparent;

        public System.Windows.Media.Brush Foreground => IsActive
            ? ThemePalette.Brush(ThemePalette.TextBrush)
            : ThemePalette.Brush(ThemePalette.TextSubtleBrush);

        public FontWeight Weight => IsActive ? FontWeights.SemiBold : FontWeights.Normal;

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Underline)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Weight)));
            }
        }

        private ISessionStatusSource? _statusSource;

        public void AttachStatusSource(ISessionStatusSource source)
        {
            _statusSource?.Dispose();
            _statusSource = source;

            // Marking only means anything while the tab is in the background. If the
            // agent finishes before you ever look away there is nothing to catch up
            // on, and a dot would just be noise.
            source.BecameIdle += () =>
            {
                if (!IsActive)
                    MarkAttention();
            };

            source.Start();
        }

        private bool _hovered;

        // Hover wins over the mark, so a marked tab still offers its close button.
        public bool ShowAttentionDot => _needsAttention && !_hovered;

        public bool ShowCloseButton => !_needsAttention || _hovered;

        public void SetHovered(bool hovered)
        {
            if (_hovered == hovered)
                return;

            _hovered = hovered;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAttentionDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCloseButton)));
        }

        public void ClearAttention() => MarkAttention(clear: true);

        private void MarkAttention(bool clear = false)
        {
            if (_needsAttention == !clear)
                return;

            _needsAttention = !clear;

            // All three, not just NeedsAttention: the tab templates bind the two
            // derived booleans, so notifying only the flag leaves the dot and the
            // close button showing whatever they were before.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsAttention)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAttentionDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCloseButton)));
        }

        public void RefreshTheme()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Underline)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
