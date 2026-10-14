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
using Helide.Usage;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
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
    private NativeTerminalHost? _leftHost;
    private LeftTool _leftHostTool = LeftTool.Git;
    private LeftTool _activeLeftTool = LeftTool.Git;

    // The yazi offer, waiting for the project browser. Held as a fact rather than asked
    // of the file system every time the pane is shown: the answer only changes when the
    // config is installed, and that path already clears it.
    private bool _yaziPromptPending;
    private YaziPromptCard? _yaziPromptCard;
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
        Helide.Keys.HelideKeymap.Load(_state.KeyBindings);
        _startupProject = startupProject;

        // Loaded here as well as in OpenWorkspace, because the two restores below run in
        // the constructor -- before any project is opened. Without this they would read an
        // empty workspace and quietly reset the arrangement and window position on every
        // launch, which reads as "my layout was ignored" rather than as a bug.
        if (!string.IsNullOrWhiteSpace(startupProject) && Directory.Exists(startupProject))
            _workspace = stateStore.LoadWorkspace(startupProject);

        InitializeComponent();

        CommandPalettePopup.CustomPopupPlacementCallback = (popup, target, _) => AnchoredTop(ref _paletteTop, popup, target);
        FolderPickerPopup.CustomPopupPlacementCallback = (popup, target, _) => AnchoredTop(ref _folderPickerTop, popup, target);

        // The collection is observable and bound once. Reassigning ItemsSource to the
        // same List instance on every change is a no-op as far as WPF is concerned,
        // which is what left the strip showing a single tab while the pane was
        // displaying a different file.
        EditorTabStrip.ItemsSource = _editorTabs;
        ZenTabStrip.ItemsSource = _editorTabs;

        // Which tab is active follows the pane that has focus, and clicking into a
        // pane moves that without anyone touching the tab strip.
        EditorSlot.FocusedHostChanged += EditorSlot_FocusedHostChanged;
        RunnerSlot.FocusedHostChanged += RunnerSlot_FocusedHostChanged;

        // How the panes are split is saved with the tabs, and as soon as it changes, for
        // the reason the tabs are: it should not depend on exiting cleanly.
        EditorSlot.LayoutChanged += PersistEditorTabs;
        RunnerSlot.LayoutChanged += PersistEditorTabs;

        // A popup is a topmost window of its own, so one left open would sit over
        // whatever application was switched to.
        Deactivated += (_, _) => ZenBarPopup.IsOpen = false;
        // Before anything reads or sets a track: the panes are laid out in the order
        // they were left in, and every size below is read by role, not by position.
        ApplySideSwap(_state.SidesSwapped);
        CaptureTrackDefaults();
        RestoreWindowGeometry();
        RestoreWorkspaceLayout();

        SourceInitialized += (_, _) =>
        {
            // Out of sight until there is a frame to show.
            DwmNative.Cloak(this, true);
            DwmNative.Apply(this);
            MaximizeBounds.Attach(this);
            MaximizeBounds.Reapply(this);

            // A window restored as maximised got there before the handler that insets
            // it was attached, so the inset is applied once here.
            MainWindow_StateChanged(this, EventArgs.Empty);
        };
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += MainWindow_StateChanged;
        SizeChanged += (_, _) => ApplyMaximizeInset();
        ContentRendered += (_, _) =>
        {
            // Queued first, so nothing below can leave the window hidden. It runs after
            // the frame that the pixel taken below asks for, which is the one to show.
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded, new Action(() => DwmNative.Cloak(this, false)));

            MaximizeBounds.Started(this);
            ApplyMaximizeInset();
        };

        // Subscribed once here rather than per tab: the view models below hand
        // brushes to their bindings as values, so they are the one part of the UI
        // that {DynamicResource} cannot reach on a palette swap.
        ThemePalette.ThemeChanged += ThemePalette_ThemeChanged;
        Closed += (_, _) =>
        {
            StopYaziFolderWatch();
            ThemePalette.ThemeChanged -= ThemePalette_ThemeChanged;
            HelideUpdateState.Changed -= OnUpdateStateChanged;
            StopWatchingGitHead();
        };

        // The update state lives outside both the pill and the About window so they
        // cannot disagree. Whoever downloads the update raises this, and the pill
        // follows rather than the two of them racing to guess.
        HelideUpdateState.Changed += OnUpdateStateChanged;

        DialogControl.Finished += CompleteDialog;
        DialogPopup.Closed += (_, _) => CompleteDialog(false, null, false);

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
        // The frame is drawn by DWM from colours it was handed once, so it has to be
        // handed the new ones.
        DwmNative.Apply(this);

        foreach (var tab in _editorTabs)
            tab.RefreshTheme();

        foreach (var session in _runnerSessions)
            session.RefreshTheme();

        foreach (var session in _agentSessions)
            session.RefreshTheme();
    }

    private bool _wasMinimized;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        InitializeUsage();
        WarmKeybindings();
        // Not while minimised. A click on the taskbar button brings a minimised window
        // forward first and restores it second, and git started in between costs the
        // click its second half: the window stays down and Windows sounds its error
        // chime. The readout is brought up to date as the window comes back up instead.
        Activated += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
                RefreshGitBranch();
        };
        StateChanged += (_, _) =>
        {
            var restored = _wasMinimized && WindowState != WindowState.Minimized;
            _wasMinimized = WindowState == WindowState.Minimized;
            if (restored)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshGitBranch));
        };
        StartOpenRequestWatcher();
        // Yazi and Helix are launched by Helide, so they inherit this and route
        // "open this file" back to us instead of spawning their own windows.
        Environment.SetEnvironmentVariable("HELIDE_OPEN_REQUEST", _openRequestPath ?? string.Empty);

        // The panes are drawn by a renderer that takes 24-bit colour, but nothing tells
        // the programs inside: Windows sets no COLORTERM, and a program that cannot tell
        // squeezes every hex colour into the 256-colour palette. That is what turned a
        // diff's red and green backgrounds into the same grey. A value the user already
        // set is left alone.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COLORTERM")))
            Environment.SetEnvironmentVariable("COLORTERM", "truecolor");

        SyncYaziIntegration();
        UpdateAiChrome();

        // Asked now, on a background thread, so the answer is waiting by the time an
        // OpenCode pane wants it instead of holding up the first one.
        if (_state.AiEnabled)
            OpenCodeVersion.Prewarm();

        // A startup project another window already owns is refused, and without the
        // fallback this one would be left exactly as the XAML declares it: no recents,
        // nothing to continue, and a title that says nothing.
        if (string.IsNullOrWhiteSpace(_startupProject)
            || !Directory.Exists(_startupProject)
            || !OpenWorkspace(_startupProject))
            ShowWelcome();
    }

    // Whether the file tree opens files in the editor is decided by yazi's own config,
    // which lives in the user's yazi directory and is only written when they ask for it.
    // So the check is the script's presence on disk rather than anything this app
    // remembers: it stays correct when the user deletes it, restores a backup, or edits
    // the config by hand.
    private void SyncYaziIntegration()
    {
        if (YaziConfig.IsInstalled())
        {
            if (!_state.YaziOpenInHelide)
            {
                _state.YaziOpenInHelide = true;
                _stateStore.Save(_state);
            }

            return;
        }

        // Nothing is raised here. The offer belongs to the browser pane, which does not
        // exist until a workspace opens, so this only arms it: SetLeftTool puts it on
        // screen the moment the project browser is the pane being shown.
        _yaziPromptPending = true;
    }

    // The offer sits in the browser's own pane, so the terminal behind it is never
    // started until the question is answered. Starting it first would leave the pane
    // looking like a working file browser that opens files the old way, which is
    // precisely what the offer is about changing.
    private void ShowYaziPrompt()
    {
        if (_yaziPromptCard is null)
        {
            _yaziPromptCard = new YaziPromptCard();
            _yaziPromptCard.Accepted += YaziPrompt_Accepted;
            _yaziPromptCard.Declined += YaziPrompt_Declined;
        }

        if (!LeftToolStack.Children.Contains(_yaziPromptCard))
            LeftToolStack.Children.Add(_yaziPromptCard);
    }

    private void DismissYaziPrompt()
    {
        if (_yaziPromptCard is not null)
            LeftToolStack.Children.Remove(_yaziPromptCard);
    }

    private void YaziPrompt_Accepted() => EnableYaziFileOpening();

    private void YaziPrompt_Declined()
    {
        // Decline installs nothing: yazi keeps its own config and opens files the way
        // it always has. What it does do is start the browser that was held back while
        // the question was on screen, so the pane is never left empty.
        //
        // No restart here, unlike accepting: nothing was installed for yazi to pick up,
        // and the offer is driven by the config being absent, so a restart would only
        // put the same question back on screen.
        _yaziPromptPending = false;
        DismissYaziPrompt();
        FocusWhenReady(StartLeftTool(LeftTool.Project));
    }

    // The project browser opens files by appending their path to a file this window
    // watches, but only if yazi's own openers point at the hand-off script -- and that
    // config is the user's, so it is written here or nowhere.
    //
    // The restart is not optional politeness: yazi reads its config once at startup, so
    // the instance already running keeps the openers it launched with and would keep
    // opening files the old way no matter what was just written to disk.
    private void EnableYaziFileOpening()
    {
        try
        {
            YaziConfig.Install();

            // The restart below ends the process without the window ever closing, so
            // nothing that is normally saved on the way out would be. The browser is
            // what was just being set up, so it is what the new window opens on and
            // hands the keyboard to.
            _state.YaziOpenInHelide = true;
            _state.FocusProjectBrowserOnStart = true;
            _stateStore.Save(_state);

            _yaziPromptPending = false;
            DismissYaziPrompt();

            if (_currentProject is not null && _ownsProject)
            {
                SetPanelCollapsed(ToolPanel.Left, false);
                _workspace.Layout.LeftTool = "project";
                RecordAgentSessions();
                SaveEditorTabs();
                SaveWorkspaceLayout();
                WriteWorkspace();
            }

            // Goes through the updater's restart path rather than a raw Process.Start:
            // it releases the single-instance claim first, or the replacement asks for
            // the mutex this process still holds, loses, and shuts itself down.
            new HelideUpdater().RestartApp(_currentProject);
        }
        catch (Exception exception)
        {
            // A failed install leaves yazi exactly as it was, so the fallback still
            // works and there is nothing to roll back. The offer stays up: it is what
            // the pane is showing, and clearing it would leave nothing behind.
            ShowNotice("Could not install the yazi config", exception.Message);
        }
    }

    private void OpenProjectButton_Click(object sender, RoutedEventArgs e) =>
        BrowseFolders(ResolveInitialFolder(), path => OpenWorkspace(path));

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

        // What a key produces under the current layout and modifier state. Used to hand
        // typed characters to an overlay when a pane still owns the keyboard, where
        // guessing from the key's name gets case and punctuation wrong.
        [DllImport("user32.dll")]
        public static extern bool GetKeyboardState(byte[] keyState);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ToUnicode(
            uint virtualKey,
            uint scanCode,
            byte[] keyState,
            [Out, MarshalAs(UnmanagedType.LPWStr, SizeConst = 8)] StringBuilder buffer,
            int bufferSize,
            uint flags);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);
    }

    private bool _paletteOpen;
    private bool _folderPickerOpen;
    private Action<string>? _pendingFolderAction;

    private void OpenCommandPalette()
    {
        if (_paletteOpen)
            return;

        ShowPalette(BuildPaletteCommands(), PaletteMode.Commands);
    }

    // The palette's popup, for anything laid out like it: the command list itself, and
    // the project and branch pickers the title bar opens.
    private void ShowPalette(List<PaletteCommand> commands, PaletteMode mode)
    {
        if (_paletteOpen)
            return;

        _paletteOpen = true;

        // Anchored to the root grid rather than left to default. A Popup placed with no
        // target centres on the pointer, which puts the overlay wherever the mouse
        // happens to be instead of in the window.
        CommandPalettePopup.PlacementTarget = RootGrid;
        _paletteTop = null;

        // Filled before it opens, so the first placement is of the list it will show.
        CommandPaletteControl.Load(commands, mode);
        CommandPalettePopup.IsOpen = true;

        // Focus comes from the popup's own Opened event, which is the first moment its
        // HwndSource exists. Asking earlier just queues focus against a window that is
        // not there yet.
    }

    // Where the overlay's top edge was put when it opened. It stays there while the list
    // under it grows and shrinks with the search, so the search box does not move under
    // the eye; only the bottom edge does.
    private double? _paletteTop;
    private double? _folderPickerTop;

    // Centred across, and centred down only once: the first height it opens at sets the
    // top. A list that later grows past the window's bottom is pushed up just enough.
    private static System.Windows.Controls.Primitives.CustomPopupPlacement[] AnchoredTop(
        ref double? top, System.Windows.Size popup, System.Windows.Size target)
    {
        top ??= Math.Max(0, (target.Height - popup.Height) / 2);
        var y = Math.Max(0, Math.Min(top.Value, target.Height - popup.Height));

        return
        [
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point((target.Width - popup.Width) / 2, y),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.None),
        ];
    }

    private void CommandPalettePopup_Opened(object sender, EventArgs e)
    {
        // WPF focus alone is not enough when the palette was opened from a pane. That
        // pane's renderer still holds real keyboard focus, so keystrokes went to it
        // rather than to the query box -- which is why Escape and typing did nothing
        // even though WPF reported the TextBox as the focused element. Handing the
        // popup's own window the focus is the same move NativeTerminalHost.FocusTerminal
        // makes in reverse.
        //
        // The window first, then the box: focusing the box while another window held
        // the keyboard did nothing, and the window's focus then landed on the popup
        // itself, so there was no caret and nothing typed reached the box.
        if (PresentationSource.FromVisual(CommandPaletteControl) is HwndSource source)
            NativeMethods.SetFocus(source.Handle);

        CommandPaletteControl.FocusQuery();
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
        _folderPickerTop = null;
        FolderPickerControl.Load(_currentProject);
        FolderPickerPopup.IsOpen = true;

        // Focus comes from the popup's own Opened event, for the same reason the
        // palette's does: it is the first moment the HwndSource exists.
    }

    private void FolderPickerPopup_Opened(object sender, EventArgs e)
    {
        // The window, then the box, as the palette does.
        if (PresentationSource.FromVisual(FolderPickerControl) is HwndSource source)
            NativeMethods.SetFocus(source.Handle);

        FolderPickerControl.FocusQuery();
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

    // The folder the picker's tree cannot show: one outside the project. Handed to the
    // in-app browser, which opens on the project and can go anywhere from there.
    //
    // Deferred for the palette's reason: the picker is closed first, and the browser
    // opens with the desktop settled rather than from inside the click that asked.
    private void FolderPicker_BrowseRequested()
    {
        var open = _pendingFolderAction;
        if (open is null)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            CloseFolderPicker();
            BrowseFolders(_currentProject, open);
        });
    }

    private List<PaletteCommand> BuildPaletteCommands()
    {
        var workspace = () => WorkspaceRoot.Visibility == Visibility.Visible;
        var commands = new List<PaletteCommand>();

        // --- File ---
        commands.Add(new PaletteCommand("Open Folder", OpenProjectFolder, "File", Helide.Keys.HelideKeymap.GestureText(Helide.Keys.HelideKeymap.OpenFolder)));
        commands.Add(new PaletteCommand("Close Workspace", ShowWelcome, "File", canInvoke: workspace));
        commands.Add(ReloadFilesCommand(workspace));
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
        commands.Add(new PaletteCommand("New Runner Session", () => StartRunnerShell(), "New", canInvoke: workspace));

        // One entry per agent that is installed, and none at all while AI is off: an
        // entry that could only ever fail is worse than no entry.
        var agents = _state.AiEnabled ? AgentProviders.Installed() : [];

        foreach (var agent in agents)
        {
            commands.Add(new PaletteCommand(
                $"New {agent.Label} Session",
                () => StartAgentSession(agent.Type, agent.Label),
                "New",
                canInvoke: workspace));
        }

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
        foreach (var agent in agents)
        {
            commands.Add(new PaletteCommand(
                $"Open {agent.Label} in Folder…",
                () => OpenFolderPicker(path => StartAgentSession(agent.Type, agent.Label, workingDirectory: path)),
                "New",
                "project folders",
                canInvoke: workspace));
        }
        // Only offered when there is something to attach to: a conversation that was
        // moved to the background keeps running there, and this is the way back into it.
        if (agents.Any(agent => agent.Type == "claude")
            && _currentProject is { } attachProject
            && ClaudeSessions.ListBackgroundJobs(attachProject).Count > 0)
        {
            commands.Add(new PaletteCommand(
                "Attach Claude Background Session…",
                AttachBackgroundSession,
                "New",
                "running in the background",
                keywords: "claude resume job conversation continue",
                canInvoke: workspace));
        }

        commands.Add(new PaletteCommand(
            "Open Runner in Folder…",
            () => OpenFolderPicker(path => StartRunnerShell(path)),
            "New",
            "project folders",
            canInvoke: workspace));

        // --- Split ---
        // The same as the right-click menu on a tab and as dragging one onto a pane,
        // for when the keyboard is already where the hands are.
        commands.Add(new PaletteCommand("Split Editor Right", () => SplitActive(false, DropEdge.Right), "Split", keywords: "pane side by side horizontal", canInvoke: () => workspace() && _activeEditorTab is not null));
        commands.Add(new PaletteCommand("Split Editor Down", () => SplitActive(false, DropEdge.Bottom), "Split", keywords: "pane above below vertical", canInvoke: () => workspace() && _activeEditorTab is not null));
        commands.Add(new PaletteCommand("Close Editor Split", () => { if (_activeEditorTab is { } tab && EditorSlot.IsShown(tab.Host)) EditorSlot.Unsplit(tab.Host); }, "Split", keywords: "unsplit merge pane", canInvoke: () => workspace() && EditorSlot.PaneCount > 1));
        commands.Add(new PaletteCommand("Split Terminal Right", () => SplitActive(true, DropEdge.Right), "Split", keywords: "runner pane side by side horizontal", canInvoke: () => workspace() && _activeRunnerSession is not null));
        commands.Add(new PaletteCommand("Split Terminal Down", () => SplitActive(true, DropEdge.Bottom), "Split", keywords: "runner pane above below vertical", canInvoke: () => workspace() && _activeRunnerSession is not null));
        commands.Add(new PaletteCommand("Close Terminal Split", () => { if (_activeRunnerSession?.Host is { } host && RunnerSlot.IsShown(host)) RunnerSlot.Unsplit(host); }, "Split", keywords: "runner unsplit merge pane", canInvoke: () => workspace() && RunnerSlot.PaneCount > 1));

        // --- Close ---
        commands.Add(new PaletteCommand(
            "Close Current Editor Tab",
            () => { if (_activeEditorTab is { } tab) CloseEditorTab(tab); },
            "Close",
            canInvoke: () => workspace() && _editorTabs.Count > 0));

        if (_state.AiEnabled)
        {
            commands.Add(new PaletteCommand(
                "Close Current Agent Session",
                () => { if (_activeAgentSession is { } session) CloseAgentSession(session); },
                "Close",
                canInvoke: () => workspace() && _agentSessions.Count > 0));
        }

        // --- Focus ---
        commands.Add(new PaletteCommand("Focus Left Panel", () => FocusPanel(ToolPanel.Left, ActiveLeftHost()), "Focus", canInvoke: workspace));
        commands.Add(new PaletteCommand("Focus Editor", () => FocusPanel(null, ActiveEditorHost()), "Focus", Helide.Keys.HelideKeymap.GestureText(Helide.Keys.HelideKeymap.Editor), canInvoke: workspace));
        commands.Add(new PaletteCommand("Focus Runner", () => FocusPanel(ToolPanel.Runner, _activeRunnerSession?.Host), "Focus", canInvoke: workspace));
        if (_state.AiEnabled)
            commands.Add(new PaletteCommand("Focus Agent", () => FocusPanel(ToolPanel.Agent, _activeAgentSession?.Host), "Focus", canInvoke: workspace));

        // --- Navigate ---
        commands.Add(new PaletteCommand("Toggle Lazygit Panel", () => ToggleLeftToolFromKey(LeftTool.Git), "View", Helide.Keys.HelideKeymap.GestureText(Helide.Keys.HelideKeymap.Lazygit), keywords: "git left show hide", canInvoke: workspace));
        commands.Add(new PaletteCommand("Toggle Project Browser", () => ToggleLeftToolFromKey(LeftTool.Project), "View", Helide.Keys.HelideKeymap.GestureText(Helide.Keys.HelideKeymap.Files), keywords: "yazi files left show hide", canInvoke: workspace));
        commands.Add(new PaletteCommand("Show Keybindings", () => Dispatcher.BeginInvoke(ShowKeybindings), "View", Helide.Keys.HelideKeymap.GestureText(Helide.Keys.HelideKeymap.Keybindings), keywords: "shortcuts keys keymap bindings hotkeys"));
        // The same offer the first-run toast makes, reachable any time -- the toast is
        // driven by the config file's absence, so a user who dismissed it once can still
        // turn the whole thing on without hunting for the file themselves.
        commands.Add(new PaletteCommand(
            "Open Project Browser Files in Helide",
            EnableYaziFileOpening,
            "View",
            _state.YaziOpenInHelide ? "on · restarts Helide" : "restarts Helide",
            keywords: "yazi file tree editor helix open config"));
        commands.Add(new PaletteCommand("Toggle Terminal Panel", () => TogglePanelFromKey(ToolPanel.Runner, _activeRunnerSession?.Host), "View", Helide.Keys.HelideKeymap.GestureText(Helide.Keys.HelideKeymap.Terminal), canInvoke: workspace));
        commands.Add(new PaletteCommand(
            _workspace.Layout.Zen ? "Leave Zen Mode" : "Zen Mode",
            ToggleZen,
            "View",
            "F11",
            keywords: "focus fullscreen distraction hide panels",
            canInvoke: workspace));
        commands.Add(new PaletteCommand(
            _state.ZenFullScreen ? "Zen Mode: Keep Taskbar Visible" : "Zen Mode: Cover Taskbar",
            ToggleZenFullScreen,
            "View",
            _state.ZenFullScreen ? "now covers taskbar" : "now keeps taskbar",
            keywords: "zen fullscreen full screen taskbar setting"));
        if (_state.AiEnabled)
            commands.Add(new PaletteCommand("Toggle Agent Panel", () => TogglePanelFromKey(ToolPanel.Agent, _activeAgentSession?.Host), "View", Helide.Keys.HelideKeymap.GestureText(Helide.Keys.HelideKeymap.Agent), canInvoke: workspace));
        commands.Add(new PaletteCommand("Next Editor Tab", () => CycleEditorTab(1), "Navigate", canInvoke: () => workspace() && _editorTabs.Count > 1));
        commands.Add(new PaletteCommand("Previous Editor Tab", () => CycleEditorTab(-1), "Navigate", canInvoke: () => workspace() && _editorTabs.Count > 1));

        commands.Add(new PaletteCommand(
            "Swap Side Panels",
            SwapSidePanels,
            "View",
            "left ⇄ right",
            keywords: "flip mirror switch sides left right agent git project",
            canInvoke: workspace));

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

        // --- AI ---
        // The same switch as the banner on the welcome screen, reachable without
        // leaving the workspace.
        commands.Add(new PaletteCommand(
            _state.AiEnabled ? "Disable AI" : "Enable AI",
            () => SetAiEnabled(!_state.AiEnabled),
            "AI",
            _state.AiEnabled ? "closes the agent pane" : "opens the agent pane",
            keywords: "agent opencode codex claude toggle switch"));

        AddBridgeCommands(commands, workspace);
        AddNavigationCommands(commands, workspace);

        // Only offered once something has been hidden, so it is not a command that does
        // nothing.
        if (_state.HiddenDialogs.Count > 0)
        {
            commands.Add(new PaletteCommand(
                "Show Hidden Dialogs Again",
                () =>
                {
                    _state.HiddenDialogs.Clear();
                    _stateStore.Save(_state);
                },
                "Help",
                "undoes \"don't ask again\"",
                keywords: "reset confirm notice popup warning"));
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
        ShowNotice("Could not open a new Helide window", exception.Message);
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

    // The character a key produces, or null when it is not printable. Only keys with no
    // Ctrl or Alt are forwarded: those are shortcuts. Shift is not one, and it is what
    // makes a capital or a symbol, so the layout is asked rather than the key's name.
    private static string? PrintableText(Key key)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return null;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
            return null;

        var state = new byte[256];
        if (!NativeMethods.GetKeyboardState(state))
            return null;

        var buffer = new StringBuilder(8);

        // 0x4 leaves the keyboard's dead-key state alone, so asking does not change what
        // the next real keystroke produces.
        var count = NativeMethods.ToUnicode(
            virtualKey,
            NativeMethods.MapVirtualKey(virtualKey, 0),
            state,
            buffer,
            buffer.Capacity,
            0x4);

        if (count != 1 || char.IsControl(buffer[0]))
            return null;

        return buffer.ToString();
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

        SetPillSpinning(HelideUpdateState.IsRestarting);
    }

    // A rotation animation runs on the render thread, so it keeps turning while the UI
    // thread is busy spawning the new process -- which is exactly when a timer-driven
    // spinner would freeze and the pill would look stuck.
    private void SetPillSpinning(bool spinning)
    {
        if (spinning)
        {
            UpdatePillIcon.Text = ""; // Sync
            UpdatePillSpin.BeginAnimation(
                System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                });
        }
        else
        {
            UpdatePillSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            UpdatePillIcon.Text = "";
        }
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
        var revealing = reveal is { } wanted && IsPanelCollapsed(wanted);

        // A panel with no tab in it has no host to hand over, and is exactly the one
        // that has to open: opening it is what puts a tab there.
        if (host is null && !revealing)
            return;

        if (revealing && reveal is { } panel)
        {
            TogglePanel(panel);

            FocusWhenReady(host ?? HostFor(panel));
            return;
        }

        host?.FocusTerminal();
    }

    // The host a panel is showing now, which is not the one it was showing before it was
    // opened if opening it started one.
    private NativeTerminalHost? HostFor(ToolPanel panel) => panel switch
    {
        ToolPanel.Runner => _activeRunnerSession?.Host,
        ToolPanel.Agent => _activeAgentSession?.Host,
        _ => ActiveLeftHost(),
    };

    // Focus for a pane that cannot take it yet: one just revealed, or just started.
    //
    // A collapsed pane's renderer window is hidden, and a hidden window is refused
    // focus -- the pane opened and the keys stayed where they were. The renderer comes
    // back some passes after the toggle, and not on any event this window is told
    // about: one deferred call at idle priority was still too early. So the focus is
    // asked for over the next moment instead.
    private void FocusWhenReady(NativeTerminalHost? host)
    {
        if (host is null)
            return;

        var attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            // Closed in the meantime: there is nothing left to focus.
            if (!_terminalHosts.Contains(host))
            {
                timer.Stop();
                return;
            }

            host.FocusTerminal();
            if (++attempts >= 8)
                timer.Stop();
        };
        timer.Start();
    }

    // A session started from outside its panel -- the palette, the folder picker, the
    // list of background conversations -- is made in a panel that may be shut, and a tab
    // in a shut panel is a tab nobody can see. The buttons that make sessions sit inside
    // the panels, so they never had this. The panel opens with the session and the
    // keyboard goes into it, as it does for Ctrl+3 and Ctrl+4.
    //
    // Done by these callers and not in the Create methods: restoring a workspace calls
    // those with the layout deliberately collapsed, and it must stay that way.
    private void StartAgentSession(
        string type,
        string typeLabel,
        string? resumeSessionId = null,
        bool resuming = false,
        string? workingDirectory = null)
    {
        var before = _agentSessions.Count;
        CreateAgentSession(type, typeLabel, resumeSessionId, resuming, workingDirectory);

        if (_agentSessions.Count > before && IsPanelCollapsed(ToolPanel.Agent))
            FocusPanel(ToolPanel.Agent, _activeAgentSession?.Host);
    }

    private void StartRunnerShell(string? workingDirectory = null)
    {
        var before = _runnerSessions.Count;
        CreateRunnerShell(workingDirectory: workingDirectory);

        if (_runnerSessions.Count > before && IsPanelCollapsed(ToolPanel.Runner))
            FocusPanel(ToolPanel.Runner, _activeRunnerSession?.Host);
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

    // `SwitchToThisWindow` is undocumented.
    // It is the reliable way to force a window to the foreground when the request comes from a different process, which is exactly this case.
    // `SetForegroundWindow` is refused in that situation. The usual alternative, toggling `Topmost`, works but visibly flashes/jumps the window.
    // Undocumented does not mean unstable, so if activation ever misbehaves this is the first thing to look at.

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

    private void MainWindow_StateChanged(object? sender, EventArgs e) => ApplyMaximizeInset();

    // Also run whenever the window's size changes, not only its state: a window restored
    // as maximised is measured before it has been shown at that size, and the pixel held
    // back from an auto-hiding taskbar comes and goes with zen mode.
    private void ApplyMaximizeInset()
    {
        // Left as it is while minimised. Dropping the inset there and putting it back on
        // the way up resizes every pane twice for nothing, and each program in one
        // redraws itself both times.
        if (WindowState == WindowState.Minimized)
            return;

        var maximized = WindowState == WindowState.Maximized;
        MaximizeInset.Margin = maximized ? MaximizeBounds.Measure(this) : new Thickness(0);
        WindowOutline.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowWelcome()
    {
        // Dropped on the way to the chooser: a window sitting here must not stop another
        // from opening the same project, which is what holding the claim would do.
        //
        // The workspace is written first, and then not again until this window owns
        // the project once more. Its panes stay alive behind this screen, and without
        // that a second window could open the project, change it, and have this
        // window's older picture written over the top when it closed.
        SaveWorkspaceLayout();
        if (_currentProject is not null && _ownsProject)
        {
            RecordAgentSessions();
            SaveEditorTabs();
            WriteWorkspace();
        }

        _ownsProject = false;

        if (_currentProject is not null && Application.Current is App app)
            app.ReleaseProjectClaim(this);

        WorkspaceRoot.Visibility = Visibility.Collapsed;
        WelcomeRoot.Visibility = Visibility.Visible;
        // The welcome screen always has its title bar, whatever the workspace was in.
        ApplyZenChrome(false);
        ZenToggle.Visibility = Visibility.Collapsed;
        ProjectNamePill.Visibility = Visibility.Collapsed;
        ProjectPathPill.Visibility = Visibility.Collapsed;
        GitBranchPill.Visibility = Visibility.Collapsed;
        Title = "Open Recent Project";
        PopulateWelcome();

        FocusWelcomeDefault();

        // And again once things have settled. Arriving here from the palette, the
        // popup is still closing, and when its window goes the main window is handed
        // focus back and restores whatever last had it -- a pane that is now hidden.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(FocusWelcomeDefault));
    }

    // Enter goes to whichever row has focus, and the Enter badge sits on the continue
    // row, so that is the one focused whenever it is there.
    private void FocusWelcomeDefault()
    {
        if (WelcomeRoot.Visibility != Visibility.Visible)
            return;

        // A pane's renderer can still hold the real keyboard focus after the workspace
        // is hidden, and WPF focus alone does not take it back.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && IsActive)
            NativeMethods.SetFocus(handle);

        if (ContinueProjectButton.Visibility == Visibility.Visible)
            ContinueProjectButton.Focus();
        else
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

        // The welcome screen hides these, and coming back to a workspace that was kept
        // alive behind it does not go through OpenWorkspace, which is what fills them in.
        // Their text is still there from when it did.
        ProjectNamePill.Visibility = Visibility.Visible;
        ProjectPathPill.Visibility = Visibility.Visible;
        GitBranchPill.Visibility = string.IsNullOrEmpty(GitBranchPillText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ZenToggle.Visibility = Visibility.Visible;
        ApplyZenChrome(_workspace.Layout.Zen);
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

        UpdateAiChrome();
        RefreshToolStatus();
    }

    private bool _toolInstallWatched;

    // The install state is shared with the panes, so a start or an end that happens in a
    // card, or while a project is open, has to be reflected here without being asked.
    private void WatchToolInstalls()
    {
        if (_toolInstallWatched)
            return;

        _toolInstallWatched = true;
        ToolLocator.InstallStateChanged += () => Dispatcher.BeginInvoke(RefreshToolStatus);
    }

    // The marks, plus the notice that appears at the top when something required is
    // missing. Found is a quiet check; a missing required tool is a cross, and only then
    // does anything ask for attention.
    private void RefreshToolStatus()
    {
        WatchToolInstalls();

        ToolLocator.SyncProcessPath();

        var icons = new System.Windows.Media.FontFamily("Segoe Fluent Icons");
        var found = ThemePalette.Brush(ThemePalette.SuccessBrush);
        var danger = ThemePalette.Brush(ThemePalette.DangerBrush);
        var faint = ThemePalette.Brush(ThemePalette.TextFaintBrush);

        ToolStatusText.Inlines.Clear();

        var missingRequired = new List<ManagedTool>();
        var first = true;
        foreach (var tool in ToolLocator.Tools)
        {
            var present = ToolLocator.IsInstalled(tool);
            if (!present && tool.Required)
                missingRequired.Add(tool);

            if (!first)
                ToolStatusText.Inlines.Add(new System.Windows.Documents.Run("    "));
            first = false;

            ToolStatusText.Inlines.Add(new System.Windows.Documents.Run(
                present ? "" : tool.Required ? "" : "")
            {
                FontFamily = icons,
                FontSize = 11,
                Foreground = present ? found : tool.Required ? danger : faint,
            });

            // pwsh has a fallback, so its absence is stated as that rather than as a fault.
            ToolStatusText.Inlines.Add(new System.Windows.Documents.Run(
                " " + tool.Label + (present || tool.Required ? string.Empty : " (optional)")));
        }

        var installing = ToolLocator.IsInstalling;
        ToolNotice.Visibility = missingRequired.Count > 0 || installing
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (missingRequired.Count > 0)
        {
            var names = string.Join(", ", missingRequired.Select(tool => tool.Label));
            ToolNoticeTitle.Text = missingRequired.Count == 1
                ? $"{names} is not installed"
                : $"{missingRequired.Count} tools are not installed";
            ToolNoticeDetail.Text = missingRequired.Count == 1
                ? "Helide opens panes for it, and they cannot start without it."
                : $"{names}. Helide opens panes for these, and they cannot start without them.";
        }
        else if (installing)
        {
            ToolNoticeTitle.Text = "Installing tools";
            ToolNoticeDetail.Text = "Open a project once it finishes.";
        }

        ToolInstallButton.IsEnabled = !installing;
        ToolInstallButton.Content = installing
            ? $"Installing {(ToolLocator.Current?.Label ?? "…")}…"
            : missingRequired.Count == 1
                ? $"Install {missingRequired[0].Label}"
                : $"Install {missingRequired.Count} tools";
    }

    private async void ToolInstallButton_Click(object sender, RoutedEventArgs e)
    {
        var missing = ToolLocator.Missing().Where(tool => tool.Required).ToArray();
        if (missing.Length == 0 || ToolLocator.IsInstalling)
        {
            RefreshToolStatus();
            return;
        }

        WelcomeStatus.Text = string.Empty;

        try
        {
            var failed = await ToolLocator.InstallAsync(missing);

            if (failed.Count == 0)
                WelcomeStatus.Text = "Tools installed. Open a project to use them.";
            else if (ToolLocator.WingetMissing)
                // Said with the command, so the next step is a paste rather than a search.
                WelcomeStatus.Text = "winget is not available here. Install: "
                    + string.Join(" ; ", failed.Select(tool => $"winget install -e --id {tool.WingetId}"));
            else
                WelcomeStatus.Text = "Could not install "
                    + string.Join(", ", failed.Select(tool => tool.Label))
                    + ". Run: "
                    + string.Join(" ; ", failed.Select(tool => $"winget install -e --id {tool.WingetId}"));
        }
        catch (Exception exception)
        {
            WelcomeStatus.Text = $"Could not install the missing tools.\n{exception.Message}";
        }
        finally
        {
            RefreshToolStatus();
        }
    }

    // Everything outside the agent pane that shows whether AI is on: the banner on the
    // welcome screen and the agent toggle in the status bar, which goes away entirely
    // rather than sitting there greyed out.
    private void UpdateAiChrome()
    {
        var installed = AgentProviders.Installed();

        AiBanner.IsChecked = _state.AiEnabled;
        AiBanner.Content = installed.Count > 0
            ? "Found on this machine:  " + string.Join("  ·  ", installed.Select(provider => provider.Label))
            : "No agent found on PATH. Install opencode, codex or claude to use this.";

        AgentPanelToggle.Visibility = _state.AiEnabled ? Visibility.Visible : Visibility.Collapsed;
        RefreshUsageLimits();
    }

    private void AiBanner_Click(object sender, RoutedEventArgs e) =>
        SetAiEnabled(AiBanner.IsChecked == true);

    // The one switch behind both the welcome banner and the palette entry.
    private void SetAiEnabled(bool enable)
    {
        if (enable == _state.AiEnabled)
            return;

        // The open panes are written down while they still exist and while AI still
        // counts as on. Once it is off the saved list is never rewritten, which is what
        // keeps every workspace's conversations there for when it is turned back on.
        if (!enable)
            PersistAgentSessions();

        _state.AiEnabled = enable;
        _stateStore.Save(_state);
        if (enable)
            OpenCodeVersion.Prewarm();
        UpdateAiChrome();

        // Turning it off takes effect now: a workspace can still be alive behind the
        // welcome screen, and its agents should not keep running. Turning it on waits
        // until a workspace is on screen, because a terminal must not be started inside
        // a pane that has no size yet -- from the palette it already is.
        if (!enable || WorkspaceRoot.Visibility == Visibility.Visible)
            SyncAgentsWithAiState();
    }

    // Brings a workspace that is already open into line with the AI switch, which can
    // have been flipped on the welcome screen while that workspace sat behind it.
    private void SyncAgentsWithAiState()
    {
        UpdateAiChrome();

        if (_currentProject is null || _terminalHosts.Count == 0)
            return;

        if (_state.AiEnabled)
        {
            if (_agentSessions.Count > 0)
                return;

            // Same reason as in OpenWorkspace: the snapshot has to precede the panes.
            CodexSessions.Initialize();
            _workspace.Layout.AgentHiddenByAi = false;
            SetPanelCollapsed(ToolPanel.Agent, false);
            InitializeAgentSessions(_currentProject);
        }
        else
        {
            if (_agentSessions.Count == 0 && IsPanelCollapsed(ToolPanel.Agent))
                return;

            if (!IsPanelCollapsed(ToolPanel.Agent))
                _workspace.Layout.AgentHiddenByAi = true;

            TearDownAgentSessions();
            SetPanelCollapsed(ToolPanel.Agent, true);
        }

        SaveLayoutAfterLayoutPasses();
    }

    // Stops every agent pane and forgets it, without touching what is saved.
    private void TearDownAgentSessions()
    {
        foreach (var session in _agentSessions)
        {
            session.DisposeStatusSource();

            if (session.Host is { } host)
            {
                host.Dispose();
                _terminalHosts.Remove(host);
            }
        }

        _agentSessions.Clear();
        _dormantAgentSessions.Clear();
        _activeAgentSession = null;
        AgentSlot.Content = null;
        UpdateAgentSessionUI();
    }

    // Opens one session when the pane would otherwise be empty, on the first agent that
    // is installed. With none installed the pane stays empty and the + menu says why.
    private void EnsureAgentSession()
    {
        if (_agentSessions.Count > 0 || !_state.AiEnabled)
            return;

        if (AgentProviders.Installed().FirstOrDefault() is { } provider)
            CreateAgentSession(provider.Type, provider.Label);
    }

    // Back to the workspace that was kept alive behind the welcome screen.
    //
    // The claim on its project has to be taken again first. Going to the welcome
    // screen drops it, and coming back used to just show the panes -- so this window
    // was on a project it no longer owned, and "Open New Window" found that project
    // free and opened it a second time.
    //
    // If another window took the project in the meantime it is the owner now. It has
    // been asked forward, and the copy held here is dropped without being saved, since
    // the file is the other window's to write.
    private bool ResumeKeptWorkspace()
    {
        if (Application.Current is App app && !app.ClaimProject(_currentProject, this))
        {
            CloseWorkspace();
            TearDownAgentSessions();
            _currentProject = null;
            PopulateWelcome();
            return false;
        }

        _ownsProject = true;
        ShowWorkspace();
        SyncAgentsWithAiState();
        return true;
    }

    private void ContinueProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentProject))
        {
            ResumeKeptWorkspace();
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

    // False when the project was not opened: it is missing, another window owns it, or
    // it failed to start. Every caller but startup can ignore that, because they leave
    // the window on whatever it was already showing.
    private bool OpenWorkspace(string project)
    {
        if (!Directory.Exists(project))
        {
            ShowNotice("The project folder does not exist", project);
            ShowWelcome();
            return false;
        }

        project = Path.GetFullPath(project);
        if (string.Equals(project, _currentProject, StringComparison.OrdinalIgnoreCase) &&
            _terminalHosts.Count > 0)
        {
            return ResumeKeptWorkspace();
        }

        // Ownership is taken before anything below tears the old workspace down. If
        // another window already has this project, this one hands focus to it and stays
        // put -- checked afterwards it would have already closed the workspace it was
        // showing. Two windows on one project would both write the same state file.
        if (Application.Current is App app && !app.ClaimProject(project, this))
        {
            // A refused claim leaves this window holding the chooser's claim instead of
            // its own project's. The workspace it is still showing has to be taken
            // back, or it would be open here and free for another window to open too.
            if (_ownsProject && _currentProject is not null)
                app.ClaimProject(_currentProject, this);

            return false;
        }

        try
        {
            // Flush the workspace being left into its own file before the project
            // changes, or the layout and tabs just captured would be written against the
            // project being opened next.
            //
            // The agent tabs and which of them was showing are captured here too. They
            // used to be recorded only when the window closed, so a workspace left by
            // switching came back on whatever the file last held -- the last tab. The
            // editor tabs had the same gap.
            if (_currentProject is not null)
            {
                RecordAgentSessions();
                SaveEditorTabs();
                WriteWorkspace();
            }

            SaveWorkspaceLayout();
            CloseWorkspace();
            TearDownAgentSessions();

            _currentProject = project;
            _ownsProject = true;

            // Loaded before anything reads it: RestoreWorkspaceLayout, RestoreEditorTabs
            // and InitializeAgentSessions all come further down this method.
            _workspace = _stateStore.LoadWorkspace(project);
            StartOpenClock();

            // With AI off there is no agent pane to show, whatever this workspace last
            // had. Its saved sessions are not touched.
            //
            // A pane closed that way is remembered as such, so it opens again by itself
            // once AI is back on instead of looking like something the user closed.
            if (!_state.AiEnabled)
            {
                if (!_workspace.Layout.AgentCollapsed)
                    _workspace.Layout.AgentHiddenByAi = true;

                _workspace.Layout.AgentCollapsed = true;
            }
            else if (_workspace.Layout.AgentHiddenByAi)
            {
                _workspace.Layout.AgentHiddenByAi = false;
                _workspace.Layout.AgentCollapsed = false;
            }

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

            ProjectNamePillText.Text = projectName;
            ProjectNamePill.Visibility = Visibility.Visible;
            ProjectPathPillText.Text = project;
            ProjectPathPill.Visibility = Visibility.Visible;
            RefreshGitBranch();

            ShowWorkspace();

            var failures = 0;
            // Only the selected left tool is started. Keeping lazygit and yazi alive at
            // the same time put two renderer windows in one Grid, and WPF z-order has no
            // authority over the child HWNDs that back the terminal renderer, so one of
            // them always ended up invisible-but-alive, swallowing clicks and keys.
            SetLeftTool(RestoreLeftTool(), persist: false);
            // SetLeftTool lights the toggle of the tool it selected, which is wrong
            // for a pane that is staying closed.
            if (_workspace.Layout.LeftCollapsed)
            {
                GitPanelToggle.IsChecked = false;
                ProjectPanelToggle.IsChecked = false;
            }
            // The pane is not missing when the offer is on it -- the browser is simply
            // held back until the question is answered. A tool that is not installed is
            // not a failure either: its pane is a card that says so and offers the fix,
            // and counting it left "1 tool pane failed to start" on the welcome screen
            // long after the tool had been installed and the pane was running.
            if (_leftHost is null && !_yaziPromptPending
                && !LeftToolStack.Children.OfType<MissingToolCard>().Any())
                failures++;
            // Restores the editor tabs this workspace had open. A tab whose file has since
            // been deleted or moved is dropped rather than opened on a path that no
            // longer resolves.
            // Nothing about the runner is saved until it has been brought up below.
            _runnerLayoutReady = false;
            if (RestoreEditorTabs() == 0 && CreateEditorTab(null) is null
                && !(_missingEditorCard is not null && EditorSlot.Children.Contains(_missingEditorCard)))
                failures++;
            // Initialize runner session with startup message, or create default
            InitializeRunnerSessions(project);
            if (_runnerSessions.Count == 0 && !RestoreRunnerLayout())
                CreateRunnerShell($" Try {_runCommand}");
            _runnerLayoutReady = true;
            // Take the rollout snapshot before any pane exists. A codex pane writes its
            // rollout file a moment after the process starts, so snapshotting after the
            // launches below would place the cutoff past the files those panes had
            // already written, and they would be discarded for the whole session --
            // leaving every codex tab to come back empty on the next launch.
            CodexSessions.Initialize();

            // Initialize agent session from saved state or create default
            if (_state.AiEnabled)
                InitializeAgentSessions(project);

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

            // Asked for by the window that restarted into this one after installing the
            // yazi config. One-shot: the next ordinary launch opens on the editor.
            if (_state.FocusProjectBrowserOnStart)
            {
                _state.FocusProjectBrowserOnStart = false;
                _stateStore.Save(_state);

                SetPanelCollapsed(ToolPanel.Left, false);
                SetLeftTool(LeftTool.Project);
                FocusWhenReady(ActiveLeftHost());
            }

            if (failures > 0)
                WelcomeStatus.Text = $"{failures} tool pane{(failures == 1 ? "" : "s")} failed to start.";

            RefreshUsageSummary();
            WatchProjectFiles(project);
            return true;
        }
        catch (Exception exception)
        {
            CloseWorkspace();
            _currentProject = null;
            ShowWelcome();
            WelcomeStatus.Text = $"Could not open the workspace.\n{exception.Message}";
            return false;
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

        // A pane whose executable is missing never fails: it sits on "Starting…" for
        // good. So the question is asked here, and the pane says what it needs instead.
        var managed = ToolLocator.Tools.First(candidate => candidate.Label == label);
        if (!ToolLocator.IsInstalled(managed))
        {
            ShowMissingLeftTool(tool, managed);
            return null;
        }

        var arguments = tool == LeftTool.Git
            ? ToolDefaults.LazygitArguments()
            : ["--client-id", YaziClientId.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        var host = new NativeTerminalHost(label, ToolCommand($"{label}.exe", arguments), _currentProject!);
        LeftToolStack.Children.Add(host);
        _terminalHosts.Add(host);
        _leftHost = host;
        _leftHostTool = tool;
        if (tool == LeftTool.Project)
        {
            _yaziFolder = _currentProject;
            WatchYaziFolder();
        }
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => host.BringToFront()));
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => host.BringToFront()));
            return host;
    }

    // Takes the place of the pane the missing tool would have filled. When the tool turns
    // up -- installed from the card, from the welcome screen, or by hand -- the card goes
    // and the real pane starts, as long as that pane is still the one being shown.
    private void ShowMissingLeftTool(LeftTool tool, ManagedTool managed)
    {
        var purpose = tool == LeftTool.Project
            ? "The project browser is yazi, and it was not found on this machine."
            : "Git control is lazygit, and it was not found on this machine.";

        var card = new MissingToolCard(managed, purpose);
        card.Resolved += () => Dispatcher.BeginInvoke(() =>
        {
            if (!LeftToolStack.Children.Contains(card))
                return;

            LeftToolStack.Children.Remove(card);
            if (_activeLeftTool == tool && _leftHost is null && _currentProject is not null)
                FocusWhenReady(StartLeftTool(tool));
        });

        LeftToolStack.Children.Add(card);
    }

    private void StopLeftTool()
    {
        // Detach, then dispose -- see CloseEditorTab for why the order matters.
        if (_leftHost is not null)
        {
            _leftHost.Visibility = Visibility.Collapsed;
            LeftToolStack.Children.Clear();
            _terminalHosts.Remove(_leftHost);
            _leftHost.Dispose();
            _leftHost = null;
        }

        // Cleared even with nothing to dispose: the pane also hosts the yazi offer,
        // which is ordinary WPF content rather than a terminal.
        LeftToolStack.Children.Clear();
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
    //
    // `beside` puts the new tab in a pane of its own next to the one showing that host,
    // instead of in the pane that has focus.
    private EditorTab? CreateEditorTab(
        string? path,
        string? workingDirectory = null,
        (NativeTerminalHost Anchor, DropEdge Edge)? beside = null,
        string? position = null)
    {
        if (_currentProject is null)
            return null;

        var helix = ToolLocator.Tools.First(candidate => candidate.Label == "helix");
        if (!ToolLocator.IsInstalled(helix))
        {
            ShowMissingEditorTool(helix, path, workingDirectory);
            return null;
        }

        // No path means Helix's file picker rooted at the project.
        var target = path is null ? "." : position is null ? path : path + ":" + position;
        var command = ToolCommand("hx.exe", [.. ToolDefaults.HelixArguments(), target]);
        try
        {
            var host = new NativeTerminalHost("helix", command, workingDirectory ?? _currentProject, path);
            var tab = new EditorTab(path, host);
            host.ModifiedChanged += (_, modified) => tab.SetModified(modified);

            EditorSlot.Add(host);
            _editorTabs.Add(tab);
            _terminalHosts.Add(host);
            ScrollStripToEnd(EditorTabStripScroll);

            if (beside is { } place && EditorSlot.IsShown(place.Anchor))
            {
                EditorSlot.SplitBeside(place.Anchor, place.Edge, host);
                ActivateEditorTab(tab);
            }
            else
            {
                ActivateEditorTab(tab);
            }

            PersistEditorTabs();
            return tab;
        }
        catch (Exception exception)
        {
            EditorSlot.Children.Add(CreateErrorPanel("helix", exception));
            return null;
        }
    }

    private MissingToolCard? _missingEditorCard;
    private readonly List<(string? Path, string? WorkingDirectory)> _pendingEditorOpens = [];

    // One card for however many tabs were asked for while Helix was missing. What was
    // asked for is kept, so installing it opens the files rather than an empty editor.
    private void ShowMissingEditorTool(ManagedTool helix, string? path, string? workingDirectory)
    {
        _pendingEditorOpens.Add((path, workingDirectory));

        // A card left over from a workspace that has since closed is not this one's.
        if (_missingEditorCard is not null && EditorSlot.Children.Contains(_missingEditorCard))
            return;

        var card = new MissingToolCard(
            helix, "The editor is Helix (hx), and it was not found on this machine.");
        _missingEditorCard = card;

        card.Resolved += () => Dispatcher.BeginInvoke(() =>
        {
            EditorSlot.Children.Remove(card);
            if (ReferenceEquals(_missingEditorCard, card))
                _missingEditorCard = null;

            var pending = _pendingEditorOpens.Distinct().ToArray();
            _pendingEditorOpens.Clear();

            foreach (var (pendingPath, pendingDirectory) in pending)
                CreateEditorTab(pendingPath, pendingDirectory);
        });

        EditorSlot.Children.Add(card);
    }

    private void ActivateEditorTab(EditorTab tab)
    {
        // Which pane shows it is the panel's business: where the tab already is, or else
        // in the pane that has focus.
        EditorSlot.Show(tab.Host);
        MarkActiveEditorTab(tab);

        // The renderer only exists once WPF has laid the host out at a real size, so
        // the raise has to wait for a pass that has actually happened.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(tab.Host.BringToFront));
    }

    // Which tab is the active one: the one in the pane that has focus. Clicking into
    // another pane moves it, so this is also what the panel's focus change lands on.
    private void MarkActiveEditorTab(EditorTab tab)
    {
        foreach (var other in _editorTabs)
            other.IsActive = ReferenceEquals(other, tab);

        _activeEditorTab = tab;

        // The dot means "not looked at yet", so arriving here clears it. Without
        // this it would sit there for the rest of the session.
        tab.ClearAttention();

        UpdateEditorFileActions();
    }

    private void EditorSlot_FocusedHostChanged(NativeTerminalHost? host)
    {
        if (host is null)
            return;

        var tab = _editorTabs.FirstOrDefault(t => ReferenceEquals(t.Host, host));
        if (tab is not null && !ReferenceEquals(tab, _activeEditorTab))
            MarkActiveEditorTab(tab);
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

    // ------------------------------------------------------------------ tabs
    //
    // The three strips share one tab template, so they share these handlers too. What a
    // tab is comes from its view model: an editor tab, or a session that is either in
    // the runner's list or the agent's.

    private void ActivateTab(object? tab)
    {
        switch (tab)
        {
            case EditorTab editor:
                ActivateEditorTab(editor);
                break;
            case AgentSessionView session when _runnerSessions.Contains(session):
                ActivateRunnerSession(session);
                break;
            case AgentSessionView session:
                ActivateAgentSession(session);
                break;
        }
    }

    // Bound to the close button rather than a right-click on the tab, which is not a
    // gesture anyone would try.
    private void Tab_Close(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case EditorTab editor:
                CloseEditorTab(editor);
                break;
            case AgentSessionView session when _runnerSessions.Contains(session):
                CloseRunnerSession(session);
                break;
            case AgentSessionView session:
                CloseAgentSession(session);
                break;
        }
    }

    // A press on a tab is either a click or the start of a drag, and which one is only
    // known once the pointer has moved or the button has come back up. So nothing
    // happens on the press itself: a tab is activated on release, and only if it was
    // not dragged in between.
    private object? _pressedTab;
    private Point _pressOrigin;   // in screen pixels
    private bool _draggingTab;
    private FrameworkElement? _dragElement;

    // Far enough that a click with an unsteady hand is still a click.
    private const double TabDragThreshold = 6;

    private void Tab_PointerDown(object sender, MouseButtonEventArgs e)
    {
        // The close button is inside the tab and takes its own clicks.
        if (IsInsideButton(e.OriginalSource as DependencyObject, sender as DependencyObject))
            return;

        if (sender is not FrameworkElement pressed)
            return;

        _pressedTab = pressed.DataContext;
        _pressOrigin = pressed.PointToScreen(e.GetPosition(pressed));
        _draggingTab = false;
    }

    private void Tab_PointerMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element
            || _pressedTab is null
            || !ReferenceEquals(element.DataContext, _pressedTab))
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // Released somewhere this tab never heard about.
            if (!_draggingTab)
                _pressedTab = null;
            return;
        }

        var pointer = element.PointToScreen(e.GetPosition(element));

        if (_draggingTab)
        {
            MoveTabGhost(element, pointer);

            // Over the strip it is a reorder, and nothing is shown. Over a pane of its
            // own area it is a split, and the pane lights up where the tab would land.
            UpdateSplitDrop(_pressedTab, pointer);

            // The other tabs make way for it, unless it is on its way into a pane.
            UpdateStripShift(pointer, splitting: _splitDrop is not null);
            return;
        }

        // Down as well as sideways: pulling a tab off the strip into a pane is the
        // gesture for splitting it.
        if (Math.Abs(pointer.X - _pressOrigin.X) < TabDragThreshold
            && Math.Abs(pointer.Y - _pressOrigin.Y) < TabDragThreshold)
            return;

        // Captured only now, not on the press: a captured tab would take the mouse-up
        // meant for its own close button.
        _draggingTab = true;
        _dragElement = element;

        // Photographed before it is dimmed, so the ghost is the tab as it was.
        BeginTabGhost(element, pointer);

        // Where every tab is, before any of them is moved out of place.
        BeginStripShift(element);

        // The tab itself is the ghost now. What is left behind is only its room, kept
        // so the strip does not close up under the pointer; the others slide into it
        // when the tab is carried past them.
        element.Opacity = 0;
        element.Cursor = System.Windows.Input.Cursors.Arrow;
        element.CaptureMouse();
    }

    // ---- Making room ----------------------------------------------------------
    //
    // As a tab is carried along the strip, the tabs it has passed slide aside to show
    // where it would go. Only how they are drawn changes: nothing is reordered until the
    // tab is let go, because moving an item rebuilds the tab that is holding the mouse
    // and the drag would end on its first step.

    private sealed class StripSlot
    {
        public required FrameworkElement Container;

        // Where the middle of the tab was when the drag began, in screen pixels. Taken
        // once: read live, it would move with the very shift it decides.
        public required double Middle;

        public double Offset;
    }

    private List<StripSlot>? _stripSlots;
    private int _stripFrom;
    private double _stripShift;

    private void BeginStripShift(FrameworkElement element)
    {
        _stripSlots = null;

        if (FindAncestor<ItemsControl>(element) is not { } strip)
            return;

        var from = strip.Items.IndexOf(element.DataContext);
        if (from < 0)
            return;

        var slots = new List<StripSlot>();
        for (var index = 0; index < strip.Items.Count; index++)
        {
            if (strip.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                return;

            var left = container.PointToScreen(new Point(0, 0)).X;
            var right = container.PointToScreen(new Point(container.ActualWidth, 0)).X;
            slots.Add(new StripSlot { Container = container, Middle = (left + right) / 2 });
        }

        // What the dragged tab takes up, with its margin: how far each tab it passes
        // has to move to fill the room it leaves and open the room it arrives in.
        _stripFrom = from;
        _stripShift = slots[from].Container.ActualWidth;
        _stripSlots = slots;
    }

    private void UpdateStripShift(Point pointer, bool splitting)
    {
        if (_stripSlots is not { } slots)
            return;

        // Where it would be put: after every other tab whose middle is left of the pointer.
        var to = _stripFrom;
        if (!splitting)
        {
            to = 0;
            for (var index = 0; index < slots.Count; index++)
                if (index != _stripFrom && pointer.X > slots[index].Middle)
                    to++;
        }

        for (var index = 0; index < slots.Count; index++)
        {
            double offset = 0;
            if (index != _stripFrom)
            {
                if (_stripFrom < to && index > _stripFrom && index <= to)
                    offset = -_stripShift;
                else if (_stripFrom > to && index >= to && index < _stripFrom)
                    offset = _stripShift;
            }

            var slot = slots[index];
            if (slot.Offset == offset)
                continue;

            slot.Offset = offset;
            if (slot.Container.RenderTransform is not TranslateTransform translate)
                slot.Container.RenderTransform = translate = new TranslateTransform();

            translate.BeginAnimation(
                TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    offset,
                    TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                    },
                });
        }
    }

    // Puts every tab back where it is drawn from. Done however the drag ended, and
    // before the move is committed, so what is committed is laid out from scratch.
    private void EndStripShift()
    {
        if (_stripSlots is not { } slots)
            return;

        _stripSlots = null;
        foreach (var slot in slots)
        {
            if (slot.Container.RenderTransform is TranslateTransform translate)
                translate.BeginAnimation(TranslateTransform.XProperty, null);

            slot.Container.RenderTransform = Transform.Identity;
        }
    }

    // ---- The tab under the pointer ---------------------------------------------
    //
    // While a tab is dragged, a picture of it travels with the pointer, held by the
    // place it was grabbed. It is a window of its own and not something drawn in the
    // tree: every pane is a native window, and anything WPF draws in this window goes
    // under them the moment the tab is over one.

    private System.Windows.Controls.Primitives.Popup? _tabGhost;
    private Point _tabGhostGrab;

    // Room the ghost's frame takes round the picture, so the picture sits where the
    // tab was and the grabbed point stays under the pointer.
    private const double TabGhostFrame = 1;

    private void BeginTabGhost(FrameworkElement element, Point pointerScreen)
    {
        EndTabGhost();

        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (width < 1 || height < 1)
            return;

        try
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            var picture = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)),
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            picture.Render(element);

            _tabGhost = new System.Windows.Controls.Primitives.Popup
            {
                AllowsTransparency = true,

                // Placed by the point it returns plus the offsets, the way the drop
                // highlight is; see PaneSplitPanel.ShowDropPreview for why not Relative.
                Placement = PlacementMode.Custom,
                CustomPopupPlacementCallback = (_, _, _) =>
                [
                    new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None),
                ],
                PlacementTarget = element,
                PopupAnimation = PopupAnimation.None,
                StaysOpen = true,
                Focusable = false,
                IsHitTestVisible = false,
                Child = new Border
                {
                    // Something under the picture: the tab itself is partly transparent,
                    // and over a terminal its text would be lost.
                    Background = ThemePalette.Brush(ThemePalette.PopoverBrush),
                    BorderBrush = ThemePalette.Brush(ThemePalette.AccentBrush),
                    BorderThickness = new Thickness(TabGhostFrame),
                    Opacity = 0.96,
                    IsHitTestVisible = false,
                    Child = new System.Windows.Controls.Image
                    {
                        Source = picture,
                        Width = width,
                        Height = height,
                        IsHitTestVisible = false,
                    },
                },
            };

            // Where in the tab it was taken hold of: the press, not wherever the pointer
            // had got to by the time the drag was recognised.
            _tabGhostGrab = element.PointFromScreen(_pressOrigin);
            PlaceTabGhost(element, pointerScreen);
            _tabGhost.IsOpen = true;
        }
        catch (Exception)
        {
            // A drag without a ghost is still a drag.
            _tabGhost = null;
        }
    }

    private void MoveTabGhost(FrameworkElement element, Point pointerScreen) =>
        PlaceTabGhost(element, pointerScreen);

    private void PlaceTabGhost(FrameworkElement element, Point pointerScreen)
    {
        if (_tabGhost is null)
            return;

        // Relative to the tab, which does not move during the drag, in the units the
        // popup's offsets are in.
        var inTab = element.PointFromScreen(pointerScreen);
        _tabGhost.HorizontalOffset = inTab.X - _tabGhostGrab.X - TabGhostFrame;
        _tabGhost.VerticalOffset = inTab.Y - _tabGhostGrab.Y - TabGhostFrame;
    }

    private void EndTabGhost()
    {
        if (_tabGhost is null)
            return;

        _tabGhost.IsOpen = false;
        _tabGhost = null;
    }

    private void Tab_PointerUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        var tab = _pressedTab;
        var dragged = _draggingTab;
        var pointer = element.PointToScreen(e.GetPosition(element));

        // Read before the drag is ended, which clears them.
        var drop = _splitDrop;
        var dropPanel = _splitDropPanel;

        EndTabDrag();

        if (tab is null || !ReferenceEquals(element.DataContext, tab))
            return;

        if (dragged && drop is { } target && dropPanel is not null)
            CommitSplitDrop(tab, dropPanel, target);
        else if (dragged)
            DropTab(element, tab, pointer);
        else
            ActivateTab(tab);

        e.Handled = true;
    }

    // ---- Splitting ------------------------------------------------------------
    //
    // Editor tabs and runner tabs can be put in panes of their own, side by side or one
    // above another, as many times as there are tabs. Dragging one onto a pane splits
    // that pane where it is let go; the right-click menu does the same without a drag.

    // Where the tab being dragged would land, and the panel that would take it. Both
    // null unless the pointer is over a pane of the tab's own area.
    private DropTarget? _splitDrop;
    private PaneSplitPanel? _splitDropPanel;

    private PaneSplitPanel? SplitPanelFor(object? tab) => tab switch
    {
        EditorTab => EditorSlot,
        AgentSessionView session when _runnerSessions.Contains(session) => RunnerSlot,
        _ => null,
    };

    private static NativeTerminalHost? HostOf(object? tab) => tab switch
    {
        EditorTab editor => editor.Host,
        AgentSessionView session => session.Host,
        _ => null,
    };

    // What a pane shows once the tab in it has been moved away: a tab that is open but
    // out of sight, or null when there is none.
    private NativeTerminalHost? FillFor(object tab) => tab switch
    {
        EditorTab editor => NearestHidden(
            _editorTabs, _editorTabs.IndexOf(editor), EditorSlot, t => t.Host)?.Host,
        AgentSessionView session when _runnerSessions.Contains(session) => NearestHidden(
            _runnerSessions, _runnerSessions.IndexOf(session), RunnerSlot, s => s.Host!)?.Host,
        _ => null,
    };

    private void UpdateSplitDrop(object? tab, Point pointer)
    {
        var panel = SplitPanelFor(tab);
        var host = HostOf(tab);

        if (_splitDropPanel is not null && !ReferenceEquals(_splitDropPanel, panel))
            _splitDropPanel.HideDropPreview();

        DropTarget? target = null;
        if (panel is not null && host is not null && tab is not null)
        {
            target = panel.HitTestDrop(pointer);

            // A drop that would do nothing is not offered: a tab onto the middle of its
            // own pane, or a tab alone in its pane split against itself.
            if (target is { } hit && ReferenceEquals(hit.Anchor, host))
            {
                if (hit.Edge == DropEdge.Center || FillFor(tab) is null)
                    target = null;
            }
        }

        _splitDrop = target;
        _splitDropPanel = target is null ? null : panel;
        panel?.ShowDropPreview(target);
    }

    private void CommitSplitDrop(object tab, PaneSplitPanel panel, DropTarget target)
    {
        var host = HostOf(tab);
        if (host is null)
            return;

        panel.MoveTo(host, target, FillFor(tab));
        ActivateTab(tab);

        if (tab is EditorTab)
            PersistEditorTabs();
    }

    // The tab a right click opened the menu on.
    private object? _menuTab;

    private void Tab_ContextRequested(object sender, MouseButtonEventArgs e)
    {
        if (_draggingTab || sender is not FrameworkElement element)
            return;

        var tab = element.DataContext;
        var panel = SplitPanelFor(tab);
        if (panel is null)
            return;

        _menuTab = tab;
        TabUnsplitRow.Visibility = HostOf(tab) is { } host && panel.IsShown(host) && panel.PaneCount > 1
            ? Visibility.Visible
            : Visibility.Collapsed;

        TabSplitFlyout.PlacementTarget = element;
        TabSplitFlyout.IsOpen = true;
        e.Handled = true;
    }

    private void TabSplit_Click(object sender, RoutedEventArgs e)
    {
        TabSplitFlyout.IsOpen = false;

        if (_menuTab is not { } tab || (sender as FrameworkElement)?.Tag is not string choice)
            return;

        if (choice == "Unsplit")
        {
            if (SplitPanelFor(tab) is { } panel && HostOf(tab) is { } host)
                panel.Unsplit(host);

            return;
        }

        var edge = choice switch
        {
            "Left" => DropEdge.Left,
            "Up" => DropEdge.Top,
            "Down" => DropEdge.Bottom,
            _ => DropEdge.Right,
        };

        SplitTab(tab, edge);
    }

    // A tab on screen is split by opening another like it beside it: a shell in the
    // same place, or the same file. A tab that is not on screen is simply shown beside
    // the pane that has focus, since that is the only way it can be looked at next to
    // anything.
    private void SplitTab(object tab, DropEdge edge)
    {
        var panel = SplitPanelFor(tab);
        var host = HostOf(tab);
        if (panel is null || host is null)
            return;

        if (!panel.IsShown(host))
        {
            if (panel.FocusedHost is { } anchor)
                panel.MoveTo(host, new DropTarget(anchor, edge, default), null);

            ActivateTab(tab);
            return;
        }

        switch (tab)
        {
            case EditorTab editor:
                CreateEditorTab(editor.Path, beside: (host, edge));
                break;
            case AgentSessionView session:
                CreateRunnerSession(
                    session.Type, session.BaseName, session.CommandLine, session.WorkingDirectory, (host, edge));
                break;
        }
    }

    // The same from the keyboard, on whichever tab is active in that area.
    private void SplitActive(bool runner, DropEdge edge)
    {
        object? tab = runner ? _activeRunnerSession : _activeEditorTab;
        if (tab is not null)
            SplitTab(tab, edge);
    }

    private void Tab_LostCapture(object sender, MouseEventArgs e)
    {
        // Capture taken away mid-drag -- another window came forward, say -- is a
        // cancelled drag, and the tab must not stay dimmed.
        if (_draggingTab)
            EndTabDrag();
    }

    private void EndTabDrag()
    {
        var element = _dragElement;

        _pressedTab = null;
        _draggingTab = false;
        _dragElement = null;

        // The ghost, the highlight and the room made in the strip belong to the drag,
        // however it ended.
        EndTabGhost();
        EndStripShift();
        _splitDropPanel?.HideDropPreview();
        EditorSlot.HideDropPreview();
        RunnerSlot.HideDropPreview();
        _splitDrop = null;
        _splitDropPanel = null;

        if (element is null)
            return;

        element.Opacity = 1;
        element.Cursor = System.Windows.Input.Cursors.Hand;
        if (element.IsMouseCaptured)
            element.ReleaseMouseCapture();
    }

    // Moves a tab to where it was let go. The new position is counted from the tabs it
    // was dragged past: every other tab whose middle lies left of the pointer is one
    // that should now come before it.
    //
    // The move happens on release rather than as the pointer travels. Moving an item
    // makes the strip rebuild that tab's visual, and the visual is the thing holding
    // the mouse, so a live reorder would end the drag on its first step.
    private void DropTab(FrameworkElement element, object tab, Point pointer)
    {
        if (FindAncestor<ItemsControl>(element) is not { } strip)
            return;

        var from = strip.Items.IndexOf(tab);
        if (from < 0)
            return;

        var to = 0;
        for (var index = 0; index < strip.Items.Count; index++)
        {
            if (index == from
                || strip.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement other)
                continue;

            // Screen coordinates on both sides: the strip may be the one in the zen bar,
            // which is a popup and has no position relative to this window.
            var left = other.PointToScreen(new Point(0, 0)).X;
            var right = other.PointToScreen(new Point(other.ActualWidth, 0)).X;
            if (pointer.X > (left + right) / 2)
                to++;
        }

        if (to == from)
            return;

        switch (tab)
        {
            case EditorTab editor:
                _editorTabs.Move(_editorTabs.IndexOf(editor), to);
                PersistEditorTabs();
                break;
            case AgentSessionView session when _runnerSessions.Contains(session):
                _runnerSessions.Move(_runnerSessions.IndexOf(session), to);
                RunnerTabStrip.Items.Refresh();
                break;
            case AgentSessionView session:
                _agentSessions.Move(_agentSessions.IndexOf(session), to);
                UpdateAgentSessionUI();

                // The order is what the next launch restores, so it is saved with it.
                PersistAgentSessions();
                break;
        }
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject? boundary)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, boundary);)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
                return true;

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }

    private void CloseEditorTab(EditorTab tab, bool refill = true)
    {
        var wasShown = EditorSlot.IsShown(tab.Host);
        var index = _editorTabs.IndexOf(tab);

        _editorTabs.Remove(tab);
        _terminalHosts.Remove(tab.Host);

        // The pane that was showing it shows a neighbour that is open but out of sight,
        // so a split is not lost to closing the tab in it. With none to show, the pane
        // closes up and the ones beside it take the room.
        var replacement = wasShown
            ? NearestHidden(_editorTabs, index, EditorSlot, t => t.Host)
            : null;

        // Detach before disposing, and never the other way round. The renderer is a
        // child HWND that is still in the visual tree at this point, and WPF can post
        // it a focus change at any moment -- including when the replacement host is
        // added below. Microsoft.Terminal.Wpf answers a focus change by writing to
        // the pseudoconsole, so a host whose ConPTY is already gone throws
        // InvalidOperationException out of an HWND message hook and takes the
        // process down with it.
        EditorSlot.Remove(tab.Host, replacement?.Host);
        tab.Host.Dispose();

        // Land on a neighbour rather than leaving the pane blank. A caller that is
        // about to open its own replacement passes refill: false, or the pane would
        // fill up again before it does.
        if (_editorTabs.Count == 0)
        {
            _activeEditorTab = null;
            if (refill)
                CreateEditorTab(null);
        }
        else
        {
            // Whatever has focus now is the active tab. Closing a tab in the background
            // leaves the one in front alone.
            var focused = EditorSlot.FocusedHost is { } host
                ? _editorTabs.FirstOrDefault(t => ReferenceEquals(t.Host, host))
                : null;
            ActivateEditorTab(focused ?? _editorTabs[Math.Min(Math.Max(index, 0), _editorTabs.Count - 1)]);
        }

        PersistEditorTabs();
    }

    // The open tab nearest to where `index` was that is not in any pane, or null when
    // every tab is already on screen.
    private static T? NearestHidden<T>(
        IList<T> tabs,
        int index,
        PaneSplitPanel panel,
        Func<T, NativeTerminalHost> hostOf)
        where T : class
    {
        T? best = null;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < tabs.Count; i++)
        {
            if (panel.IsShown(hostOf(tabs[i])))
                continue;

            // A tab that moved up into the closed one's place is nearer than the one
            // before it, which is what the tab bar does when you close one in the middle.
            var distance = i >= index ? i - index : index - i + 1;
            if (distance < bestDistance)
            {
                best = tabs[i];
                bestDistance = distance;
            }
        }

        return best;
    }

    // The + button used to raise a Windows file dialog, which pulled focus out of the
    // workspace for something Helide can already do. It opens a plain, unnamed Helix
    // in the project instead; files arrive by picking them in the left pane.
    private void EditorTabSplitButton_Click(object sender, RoutedEventArgs e) => CreateEditorTab(null);

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

        // With Alt held, WPF reports the key as System and the real one separately.
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;

        if (HandleShortcutCapture(pressed, modifiers))
        {
            e.Handled = true;
            return;
        }

        // A dialog card owns the keyboard while it is up, for the palette's reason: shown
        // over a pane, its text box would never be handed a key of its own.
        if (_dialogOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    DialogControl.Cancel();
                    e.Handled = true;
                    return;

                case Key.Enter:
                    DialogControl.Accept();
                    e.Handled = true;
                    return;

                // The one key a card with no text box has left over.
                case Key.Space when !DialogControl.HasInput:
                    DialogControl.ToggleSuppress();
                    e.Handled = true;
                    return;
            }

            // Swallowed rather than passed on when the card is not the window receiving
            // keys: they would otherwise be typed into the terminal behind it.
            if (!DialogHasOsFocus())
            {
                if (e.Key == Key.Back)
                    DialogControl.Backspace();
                else if (PrintableText(e.Key) is { } typed)
                    DialogControl.AppendText(typed);

                e.Handled = true;
            }

            return;
        }

        // An agent pane owns Ctrl+O (expand) and Ctrl+P (its own commands) while it has
        // the keyboard, so those two are not taken from it. Ctrl+Shift+O and Ctrl+Shift+P
        // do the same for Helide in any pane: a terminal cannot tell Ctrl+Shift+letter
        // from Ctrl+letter, so no agent has anything on them.
        //
        // An open palette is the exception: Ctrl+P has to be able to close it, and its own
        // window can be without the OS-level keyboard when it was opened from a pane.
        //
        // Which key does what is HelideKeymap's: the defaults, or what the user set.
        var agentOwnsKeys = !_paletteOpen && AgentHasKeyboard();
        var bound = Helide.Keys.HelideKeymap.Match(pressed, modifiers);
        if (agentOwnsKeys && Helide.Keys.HelideKeymap.YieldsToAgent(pressed, modifiers))
            bound = null;

        if (bound == Helide.Keys.HelideKeymap.ErrorLocation && WorkspaceRoot.Visibility == Visibility.Visible)
        {
            OpenErrorLocation();
            e.Handled = true;
            return;
        }

        if (bound == Helide.Keys.HelideKeymap.Mention && WorkspaceRoot.Visibility == Visibility.Visible && _state.AiEnabled)
        {
            MentionFileInAgent();
            e.Handled = true;
            return;
        }

        if (bound == Helide.Keys.HelideKeymap.Palette)
        {
            ToggleCommandPalette();
            e.Handled = true;
            return;
        }

        // Its own key closes it again, as the palette's does.
        if (bound == Helide.Keys.HelideKeymap.Keybindings)
        {
            if (_paletteOpen)
                CloseCommandPalette();
            else
                ShowKeybindings();

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

        if (bound == Helide.Keys.HelideKeymap.Zen && WorkspaceRoot.Visibility == Visibility.Visible)
        {
            ToggleZen();
            e.Handled = true;
            return;
        }

        if (bound == Helide.Keys.HelideKeymap.OpenFolder)
        {
            OpenProjectButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Enter on the welcome screen continues, as the badge on that row says, without
        // depending on the row having kept focus. A button that does have focus -- a
        // recent project tabbed to, say -- still gets its own Enter.
        if (modifiers == ModifierKeys.None
            && e.Key == Key.Enter
            && WelcomeRoot.Visibility == Visibility.Visible
            && ContinueProjectButton.Visibility == Visibility.Visible
            && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase)
        {
            ContinueProjectButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // The welcome screen's numbers are fixed: they are the rows it shows.
        var number = modifiers == ModifierKeys.Control ? KeyToNumber(pressed) : 0;
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

        if (WorkspaceRoot.Visibility != Visibility.Visible)
            return;

        number = bound switch
        {
            Helide.Keys.HelideKeymap.Lazygit => 1,
            Helide.Keys.HelideKeymap.Editor => 2,
            Helide.Keys.HelideKeymap.Terminal => 3,
            Helide.Keys.HelideKeymap.Agent => 4,
            Helide.Keys.HelideKeymap.Files => 5,
            _ => 0,
        };
        if (number == 0)
            return;

        // There is no agent pane to focus while AI is off.
        if (number == 4 && !_state.AiEnabled)
            return;

        // Every number is a pane, and every one of them toggles: the key shows its pane
        // and puts the keyboard in it -- a pane already in view is only given the
        // keyboard -- and the same key again puts the pane away and hands the keyboard
        // back to the editor. Ctrl+1 is lazygit and Ctrl+5 the
        // project browser, each by name -- Ctrl+1 used to mean "whichever of the two is
        // showing", so it could never bring lazygit back once the browser was up, and
        // only Ctrl+5 ever closed anything. The editor is the one pane that cannot be
        // closed, so Ctrl+2 only focuses.
        switch (number)
        {
            case 1:
                ToggleLeftToolFromKey(LeftTool.Git);
                break;
            case 5:
                ToggleLeftToolFromKey(LeftTool.Project);
                break;
            case 2:
                FocusPanel(null, ActiveEditorHost());
                break;
            case 3:
                TogglePanelFromKey(ToolPanel.Runner, _activeRunnerSession?.Host);
                break;
            case 4:
                TogglePanelFromKey(ToolPanel.Agent, _activeAgentSession?.Host);
                break;
        }

        e.Handled = true;
    }

    // The agent pane that is showing, and whether it is the window receiving keys.
    private bool AgentHasKeyboard() =>
        AgentSlot.Content is NativeTerminalHost host && host.HasKeyboardFocus();

    private void ToggleLeftToolFromKey(LeftTool tool)
    {
        var showing = !IsPanelCollapsed(ToolPanel.Left) && _activeLeftTool == tool;

        // In view but not where the keys go: the key is asking to use it, not to hide it.
        if (showing && ActiveLeftHost() is { } left && !left.HasKeyboardFocus())
        {
            FocusWhenReady(left);
            return;
        }

        // Shows the tool, switching to it if the other one is up, or hides the panel.
        ToggleLeftTool(tool);

        if (showing)
            ActiveEditorHost()?.FocusTerminal();
        else
            FocusWhenReady(ActiveLeftHost());
    }

    private void TogglePanelFromKey(ToolPanel panel, NativeTerminalHost? host)
    {
        if (IsPanelCollapsed(panel))
        {
            FocusPanel(panel, host);
            return;
        }

        // In view but not where the keys go: the key is asking to use it, not to hide it.
        if (!PanelHasKeyboard(panel))
        {
            FocusPanel(panel, host);
            return;
        }

        TogglePanel(panel);

        // A hidden pane must not keep the keyboard: typing would go nowhere visible.
        ActiveEditorHost()?.FocusTerminal();
    }

    // The terminal can be split, so any of its panes having the keyboard counts.
    private bool PanelHasKeyboard(ToolPanel panel) => panel switch
    {
        ToolPanel.Runner => _runnerSessions.Any(session => session.Host?.HasKeyboardFocus() == true),
        ToolPanel.Agent => AgentHasKeyboard(),
        ToolPanel.Left => ActiveLeftHost()?.HasKeyboardFocus() == true,
        _ => false,
    };

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

        // SetPanelCollapsed only moves a track when the state changes, and the state it
        // compares against is the saved one it was just handed -- so a pane saved as
        // collapsed never had its track closed and came back open on every launch. The
        // runner and agent tracks are applied again in OpenWorkspace; the left one was
        // not applied anywhere.
        if (layout.LeftCollapsed)
        {
            LeftPaneColumn.Width = new GridLength(0);
            LeftPaneColumn.MinWidth = 0;
            LeftSplitterColumn.Width = new GridLength(0);
            LeftPane.Visibility = Visibility.Collapsed;
        }
        else
        {
            LeftPaneColumn.MinWidth = _leftPaneMinWidth;
            LeftSplitterColumn.Width = _leftSplitterWidth;
            LeftPane.Visibility = Visibility.Visible;
        }
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

        GitToolStrip.Visibility = showingGit ? Visibility.Visible : Visibility.Collapsed;
        ProjectToolStrip.Visibility = showingGit ? Visibility.Collapsed : Visibility.Visible;
        GitStatusText.Visibility = showingGit ? Visibility.Visible : Visibility.Collapsed;
        ProjectInfoText.Visibility = showingGit ? Visibility.Collapsed : Visibility.Visible;

        GitPanelToggle.IsChecked = showingGit;
        ProjectPanelToggle.IsChecked = !showingGit;

        // Swap the process rather than the visibility of a stacked sibling: there is
        // only ever one child window behind this pane, so it always owns the clicks.
        if (_yaziPromptPending && !showingGit)
        {
            StopLeftTool();
            ShowYaziPrompt();
        }
        else if (_leftHost is null || _leftHostTool != tool)
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

    // ---- Swapping the side panels ---------------------------------------------
    //
    // The git and project panel and the agent panel trade sides, and the two clusters of
    // panel buttons in the status bar trade with them, so each button stays under the
    // panel it belongs to.
    //
    // Nothing that sizes a panel knows about this. Every width is read and written
    // through a column definition named for its role, so what moves is the order the
    // definitions are in and the column each pane is put in; the definitions, and the
    // sizes in them, go along with their panes.

    private bool _sidesSwappedApplied;

    private void ApplySideSwap(bool swapped)
    {
        if (swapped == _sidesSwappedApplied)
            return;

        ColumnDefinition[] order = swapped
            ? [RightPaneColumn, RightSplitterColumn, CenterPaneColumn, LeftSplitterColumn, LeftPaneColumn]
            : [LeftPaneColumn, LeftSplitterColumn, CenterPaneColumn, RightSplitterColumn, RightPaneColumn];

        PaneGrid.ColumnDefinitions.Clear();
        foreach (var column in order)
            PaneGrid.ColumnDefinitions.Add(column);

        Grid.SetColumn(LeftPane, Array.IndexOf(order, LeftPaneColumn));
        Grid.SetColumn(LeftSplitter, Array.IndexOf(order, LeftSplitterColumn));
        Grid.SetColumn(CenterPane, Array.IndexOf(order, CenterPaneColumn));
        Grid.SetColumn(RightSplitter, Array.IndexOf(order, RightSplitterColumn));
        Grid.SetColumn(AgentPane, Array.IndexOf(order, RightPaneColumn));

        // The status bar: the clusters change sides, and each is mirrored so the button
        // for a panel is still the one nearest the edge that panel is against.
        Grid.SetColumn(StatusLeftGroup, swapped ? 2 : 0);
        Grid.SetColumn(StatusRightGroup, swapped ? 0 : 2);
        StatusLeftGroup.Margin = swapped ? new Thickness(4, 0, 0, 0) : new Thickness(0, 0, 12, 0);
        StatusRightGroup.Margin = swapped ? new Thickness(0, 0, 12, 0) : new Thickness(4, 0, 0, 0);
        MirrorButtons(StatusLeftGroup);
        MirrorButtons(StatusRightGroup);

        _sidesSwappedApplied = swapped;
    }

    private static void MirrorButtons(StackPanel group)
    {
        var buttons = group.Children.Cast<UIElement>().Reverse().ToArray();
        group.Children.Clear();

        for (var index = 0; index < buttons.Length; index++)
        {
            if (buttons[index] is FrameworkElement element)
                element.Margin = new Thickness(index == 0 ? 0 : 2, 0, 0, 0);

            group.Children.Add(buttons[index]);
        }
    }

    private void SwapSidePanels()
    {
        _state.SidesSwapped = !_state.SidesSwapped;
        ApplySideSwap(_state.SidesSwapped);
        _stateStore.Save(_state);

        // The panes' windows are put right once they are arranged at their new size;
        // this only nudges the odd stale frame.
        RequestPaneRefresh(ToolPanel.Left);
        RequestPaneRefresh(ToolPanel.Agent);
    }

    private void TogglePanel(ToolPanel panel)
    {
        SetPanelCollapsed(panel, !IsPanelCollapsed(panel));

        if (!IsPanelCollapsed(panel))
            EnsurePanelHasTab(panel);

        // Save synchronously so persistence works immediately (not async)
        SaveWorkspaceLayout();
        _stateStore.Save(_state);
    }

    // A panel opened by hand with nothing in it would be an empty rectangle, so it opens
    // with a tab: a shell for the terminal, and for the agents the first one installed,
    // which is OpenCode when it is there.
    //
    // Here and not in SetPanelCollapsed, which restoring a workspace also calls, with
    // the panes it is about to bring back not made yet.
    private void EnsurePanelHasTab(ToolPanel panel)
    {
        if (_currentProject is null)
            return;

        switch (panel)
        {
            case ToolPanel.Runner when _runnerSessions.Count == 0:
                CreateRunnerShell();
                break;

            case ToolPanel.Agent when _state.AiEnabled && _agentSessions.Count == 0:
                EnsureAgentSession();
                break;
        }
    }

    // The last tab of a panel being closed closes the panel with it, and the keyboard
    // goes back to the editor, as it does when a panel is put away by hand.
    private void CollapseEmptyPanel(ToolPanel panel)
    {
        if (IsPanelCollapsed(panel))
            return;

        SetPanelCollapsed(panel, true);
        SaveLayoutAfterLayoutPasses();
        ActiveEditorHost()?.FocusTerminal();
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
        var host = panel switch
        {
            ToolPanel.Left => ActiveLeftHost(),
            ToolPanel.Runner => RunnerSlot.FocusedHost,
            _ => AgentSlot.Content as NativeTerminalHost,
        };

        // A runner in a split has more than one pane to nudge, so every host on screen
        // is asked rather than only the one with focus.
        var also = panel == ToolPanel.Runner
            ? _runnerSessions.Select(s => s.Host).Where(h => h is not null && RunnerSlot.IsShown(h)).ToArray()
            : [];

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            host?.Refresh();
            foreach (var other in also)
                other?.Refresh();
        }));
    }

    private NativeTerminalHost? ActiveLeftHost() => _leftHost;

    private NativeTerminalHost? ActiveEditorHost() => _activeEditorTab?.Host;

    // Agent new session: opens the flyout
    //
    // Only the agents that are installed are offered. Looked up as the flyout opens, so
    // one installed while Helide is running shows up without a restart.
    private void AgentNewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var any = false;

        foreach (var (type, button) in new (string, Button)[]
                 {
                     (AgentProviders.OpenCode, NewOpenCodeButton),
                     (AgentProviders.Codex, NewCodexButton),
                     (AgentProviders.Claude, NewClaudeButton),
                 })
        {
            var installed = AgentProviders.IsInstalled(type);
            button.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            any |= installed;
        }

        NoAgentsText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        AgentNewSessionFlyout.IsOpen = true;
    }

    // Create a new OpenCode session replacing the panel
    private void NewOpenCodeButton_Click(object sender, RoutedEventArgs e)
    {
        AgentNewSessionFlyout.IsOpen = false;
        CreateAgentSession("opencode", "OpenCode");
    }

    // Create a new Claude session replacing the panel
    private void NewClaudeButton_Click(object sender, RoutedEventArgs e)
    {
        AgentNewSessionFlyout.IsOpen = false;
        CreateAgentSession("claude", "Claude");
    }

    // Create a new Codex session replacing the panel
    private void NewCodexButton_Click(object sender, RoutedEventArgs e)
    {
        AgentNewSessionFlyout.IsOpen = false;
        CreateAgentSession("codex", "Codex");
    }

    private void AttachStatusSource(AgentSessionView session, string type)
    {
        if (session.Host is not { } host)
            return;

        // An OpenCode pane shares the user's own opencode store, so status is a read of one
        // session row whose id the pane already knows, because Helide chose it. Codex writes
        // task_started/task_complete into the rollout file it already maintains, which
        // is the same kind of first-party signal rather than an inference.
        //
        // The rollout is looked up by session id, which resolves for a pane that resumed
        // a known thread and for one whose id was just captured, and stays null until
        // the pane has been used at all. Only then is there genuinely nothing to read,
        // and the quiet-period source covers that window instead of guessing once a real
        // signal exists.
        ISessionStatusSource source = type switch
        {
            "opencode" when session.SessionId is { Length: > 0 } openCodeId =>
                new OpenCodeStatusSource(OpenCodeSessions.DatabasePath, openCodeId, Dispatcher),
            "codex" => new TranscriptStatusSource(
                () => session.SessionId is { Length: > 0 } id
                    ? CodexSessions.FindRolloutPath(id)
                    : null,
                CodexSessions.ReadTurn,
                Dispatcher),
            // Claude keeps a transcript per session id, and the pane was given its id, so
            // the file is known by name and simply does not exist until the first message.
            //
            // Read from the pane each time rather than captured, because the pane's id
            // can move: /resume and /clear put the same process on another conversation,
            // and the capture timer follows it (CaptureAgentSessionIds).
            "claude" when session.SessionId is { Length: > 0 } =>
                new TranscriptStatusSource(
                    () => session.SessionId is { Length: > 0 } id
                        ? ClaudeSessions.FindTranscriptPath(id)
                        : null,
                    ClaudeSessions.ReadTurn,
                    Dispatcher),
            _ => new QuietPeriodStatusSource(host),
        };

        // The usage figures, open editors and turn alerts follow every turn that ends,
        // in whichever tab.
        source.BecameIdle += () => OnAgentTurnFinished(session);
        source.BusyChanged += busy =>
        {
            if (busy)
                ClearTurnEdits(session);
        };
        session.AttachStatusSource(source);
    }

    // The command line and the status source must agree on the port, so both are
    // built together here rather than by each caller. That is also why this no
    // longer takes a commandLine: passing one in was how they drifted apart.
    // ---- OpenCode 1 --------------------------------------------------------------
    //
    // A held tab is an OpenCode tab with no terminal behind it. It exists so the pane,
    // the tab strip and the saved state all look the way they will once OpenCode is
    // updated, and the update is then a matter of starting what is already there.

    private OpenCodeUpdateCard? _openCodeCard;
    private DispatcherTimer? _openCodeUpdateWatch;

    private static bool IsHeld(AgentSessionView session) =>
        session.Host is null && session.Type == AgentProviders.OpenCode;

    // What the agent pane shows for a tab: its terminal, or the notice for a held one.
    private object? AgentContentFor(AgentSessionView session) =>
        session.Host is not null ? session.Host : IsHeld(session) ? OpenCodeCard() : null;

    private OpenCodeUpdateCard OpenCodeCard()
    {
        if (_openCodeCard is null)
        {
            _openCodeCard = new OpenCodeUpdateCard();
            _openCodeCard.SetCommand(OpenCodeUpdateCommand());
            _openCodeCard.UpdateRequested += StartOpenCodeUpdate;
            _openCodeCard.CheckRequested += () => _ = RecheckOpenCode(announce: true);
        }

        return _openCodeCard;
    }

    // OpenCode 1's own updater is not relied on where npm is there to do it. Left to
    // itself it can pick its "curl" method, which pipes an install script into a shell
    // Windows does not have. Told to use npm it does install the package, but a recent
    // npm only runs an install script for a package that is named to it, and this
    // package's script is what fetches the real binary -- so the update "succeeded" and
    // left a stand-in that Windows refuses to start. Installing it directly lets the
    // script be allowed by name. An older npm does not know the flag and ignores it.
    //
    // Version 2 is its own package, so this sits beside a version 1 rather than
    // replacing it, and OpenCodeVersion is what picks the right one of the two.
    private static string OpenCodeUpdateCommand() => ToolDefaults.OnPath("npm.cmd")
        ? "npm install -g @opencode/cli --allow-scripts=@opencode/cli"
        : "opencode upgrade --major";

    private void HoldOpenCodeSession(string typeLabel, string? sessionId, string paneDirectory)
    {
        var held = new AgentSessionView(AgentProviders.OpenCode, typeLabel)
        {
            WorkingDirectory = paneDirectory,
            SessionId = sessionId,
        };

        foreach (var s in _agentSessions)
            s.IsActive = false;
        held.IsActive = true;
        _agentSessions.Add(held);
        _activeAgentSession = held;

        AgentSlot.Content = OpenCodeCard();
        UpdateAgentSessionUI();

        if (!_restoringAgentSessions)
            PersistAgentSessions();
    }

    // The update runs in a runner tab rather than out of sight: it downloads, it can ask
    // a question, and it can fail, and all three are things the user should see. The
    // shell is left open afterwards so what it said is still there to read.
    private void StartOpenCodeUpdate()
    {
        if (_currentProject is null)
            return;

        var command = OpenCodeUpdateCommand();
        OpenCodeCard().SetCommand(command);

        SetPanelCollapsed(ToolPanel.Runner, false);
        CreateRunnerSession("runner", "opencode update", "cmd.exe /k " + command);

        OpenCodeCard().SetStatus(
            "Updating in the runner pane below. This pane opens by itself once OpenCode 2 is there.\n\n"
            + "If the update fails there, install OpenCode 2 yourself and check again: "
            + "see opencode.ai/v2/docs.");

        // Nothing announces that the update has finished, so the version is asked for
        // again every few seconds until it changes or there is no tab left waiting.
        _openCodeUpdateWatch?.Stop();
        var checks = 0;
        var watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        watch.Tick += async (_, _) =>
        {
            if (++checks > 150 || !_agentSessions.Any(IsHeld))
            {
                watch.Stop();
                return;
            }

            if (await RecheckOpenCode(announce: false))
                watch.Stop();
        };
        _openCodeUpdateWatch = watch;
        watch.Start();
    }

    private bool _recheckingOpenCode;

    // Looks at the installed version again and, if it is now 2, starts every held tab.
    // True when it did.
    private async Task<bool> RecheckOpenCode(bool announce)
    {
        if (_recheckingOpenCode)
            return false;

        _recheckingOpenCode = true;
        try
        {
            if (announce)
                OpenCodeCard().SetStatus("Checking…");

            if (await OpenCodeVersion.IsLegacyAsync())
            {
                if (announce)
                    OpenCodeCard().SetStatus("Still OpenCode 1. Type /update to update it.");

                return false;
            }

            OpenCodeCard().SetStatus(null);
            ReleaseHeldOpenCodeSessions();
            return true;
        }
        finally
        {
            _recheckingOpenCode = false;
        }
    }

    // Each held tab is replaced, in place, by a real one on the id it was holding.
    private void ReleaseHeldOpenCodeSessions()
    {
        var showing = _activeAgentSession is null ? -1 : _agentSessions.IndexOf(_activeAgentSession);

        foreach (var held in _agentSessions.Where(IsHeld).ToList())
        {
            var index = _agentSessions.IndexOf(held);
            _agentSessions.Remove(held);

            var before = _agentSessions.Count;
            CreateAgentSession(
                AgentProviders.OpenCode,
                held.Name,
                held.SessionId,
                workingDirectory: held.WorkingDirectory);

            // It is appended, and belongs where the tab it replaces was. A launch that
            // failed adds nothing, and there is then nothing to move.
            if (_agentSessions.Count > before && index < _agentSessions.Count - 1)
                _agentSessions.Move(_agentSessions.Count - 1, index);
        }

        if (showing >= 0 && showing < _agentSessions.Count)
            ActivateAgentSession(_agentSessions[showing]);

        UpdateAgentSessionUI();
        PersistAgentSessions();
    }

    private void CreateAgentSession(string type, string typeLabel, string? resumeSessionId = null, bool resuming = false, string? workingDirectory = null)
    {
        // Nothing starts an agent while AI is off, whichever path asked for one.
        if (_currentProject is null || !_state.AiEnabled)
            return;

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

        // Taken before the host is built, so it always precedes anything the
        // process is about to write.
        var launchedUtc = DateTime.UtcNow;

        // OpenCode 2.x removed --port from the TUI, and keeps every session in one
        // store that the TUI lists in full for the current project.
        //
        // A pane runs the user's own opencode against their own data directory, so its
        // conversations are the same ones a terminal or another client would see. That
        // store holds every session on the machine, so the pane has to know which one is
        // its own -- and it does, because Helide picks the id. `opencode -s <id>` continues
        // that session, or creates it under that id when there is none, in the directory
        // the pane was started in. A fresh pane is therefore handed a new id and a restored
        // one its saved id, and both are launched the same way.
        //
        // Everything else that was tried had to find the pane's session after the fact,
        // and none of it held: filtering a shared event stream, asking the service to
        // create the session (it lands in the service's directory, and the pane opens
        // blank), and claiming whichever session appeared in the project after launch
        // (two fresh panes race for it).
        //
        // Claude takes its id the same way, so it is handled the same way. Codex is the
        // one that cannot be told: it names its own thread, which is why it alone still
        // has an id to learn after it starts.
        var sessionId = resumeSessionId is { Length: > 0 }
            ? resumeSessionId
            : type switch
            {
                "opencode" => OpenCodeSessions.NewId(),
                "claude" => ClaudeSessions.NewId(),
                _ => resumeSessionId,
            };

        // OpenCode 1 is not started. The tab is made all the same, holding the id and
        // the directory it will open with, and shows the update notice until then.
        if (type == "opencode" && OpenCodeVersion.IsLegacy())
        {
            HoldOpenCodeSession(typeLabel, sessionId, workingDirectory ?? _currentProject!);
            return;
        }

        string commandLine;

        if (type == "codex")
        {
            // The file that was actually found on PATH: npm installs a .cmd shim and
            // other installers an .exe, and no shell is in front of this to work out which.
            var codex = AgentProviders.Resolve(type) ?? "codex.cmd";

            commandLine = codexResumeId is { } codexLaunchId
                ? ToolCommand(codex, "resume", codexLaunchId)
                : ToolCommand(codex);
        }
        else if (type == "claude")
        {
            var claude = AgentProviders.Resolve(type) ?? "claude.exe";

            // A conversation that is running in the background cannot be resumed: Claude
            // opens an empty copy of it and prints one line saying so, which is what piled
            // up in the pane on every restart. It is attached to instead, which shows the
            // conversation as it is.
            //
            // Past that, resuming an id that has no conversation yet makes claude exit,
            // and that is exactly what a pane that was opened and never typed into has.
            // So the transcript decides: continue it if it is there, otherwise start it.
            //
            // A pane it starts or resumes also relays its status line, which is the only
            // place Claude's live usage limits are handed out (ClaudeStatusRelay).
            var relay = ClaudeStatusRelay.SettingsFor(_stateStore.StateDirectory, workingDirectory ?? _currentProject!);
            string[] relaySettings = relay is null ? [] : ["--settings", relay];

            if (ClaudeSessions.FindBackgroundJobId(sessionId!) is { } jobId)
                commandLine = ToolCommand(claude, "attach", jobId);
            else if (ClaudeSessions.FindTranscriptPath(sessionId!) is null)
                commandLine = ToolCommand(claude, ["--session-id", sessionId!, .. relaySettings]);
            else
                commandLine = ToolCommand(claude, ["--resume", sessionId!, .. relaySettings]);
        }
        else
        {
            commandLine = OpenCodePaneConfig.BuildLaunchCommand(sessionId, QuoteArgument);
        }


        try
        {
            // Create the new session host
            var paneDirectory = workingDirectory ?? _currentProject!;
            var host = new NativeTerminalHost(type, commandLine, paneDirectory, revealWhenSettled: true);

            // Replace the agent panel content
            AgentSlot.Content = host;
            _terminalHosts.Add(host);

            var newSession = new AgentSessionView(type, typeLabel);
            newSession.Host = host;
            newSession.WorkingDirectory = paneDirectory;
            newSession.SessionId = sessionId;
            if (startsFreshCodex)
                newSession.AwaitingSessionSince = launchedUtc;
            AttachStatusSource(newSession, type);
            foreach (var s in _agentSessions)
                s.IsActive = false;
            newSession.IsActive = true;
            _agentSessions.Add(newSession);
            _activeAgentSession = newSession;

            // Update UI
            UpdateAgentSessionUI();

            if (!_restoringAgentSessions)
                PersistAgentSessions();
        }
        catch (Exception exception)
        {
            AgentSlot.Content = CreateErrorPanel(type, exception);
        }
    }
    // Update session list and title
    private void UpdateAgentSessionUI()
    {
        AgentTabStrip.ItemsSource = null;
        AgentTabStrip.ItemsSource = _agentSessions;
        ScrollStripToEnd(AgentTabStripScroll);
        OnAgentTabsChanged();
    }

    private void InitializeAgentSessions(string project)
    {
        TearDownAgentSessions();

        // The saved list is not written back while it is being replayed: a pane that
        // failed to come up would otherwise drop the ones after it from the file.
        _restoringAgentSessions = true;
        try
        {
            // Load saved sessions for this project from AppState
            var savedSessions = _workspace.AgentSessions.ToList();
            foreach (var saved in savedSessions)
            {
                var sessionType = saved.Type;
                var sessionName = string.IsNullOrEmpty(saved.Name) ? sessionType.ToUpper() : saved.Name;

                // A pane opened in another folder has its conversation filed under
                // that folder, and reopening it from the project root shows nothing.
                var savedDirectory = saved.WorkingDirectory is { Length: > 0 } directory
                                     && Directory.Exists(directory)
                    ? directory
                    : null;

                // An agent that is no longer installed cannot be started, but its
                // conversation is still there. The entry is carried along untouched
                // so it comes back when the agent does.
                if (!AgentProviders.IsInstalled(sessionType))
                {
                    _dormantAgentSessions.Add(saved);
                    continue;
                }

                try
                {
                    CreateAgentSession(
                        sessionType,
                        sessionName,
                        saved.SessionId,
                        resuming: true,
                        workingDirectory: savedDirectory);
                }
                catch
                {
                    // Skip failed sessions
                }
            }
        }
        finally
        {
            _restoringAgentSessions = false;
        }

        // A workspace with nothing saved gets one session to start with.
        EnsureAgentSession();

        // Each newly created session is made active, so without re-asserting this on
        // restore the last-created one always wins rather than the one you left on.
        var activeIndex = _workspace.ActiveAgentIndex;
        if (_dormantAgentSessions.Count == 0 && activeIndex >= 0 && activeIndex < _agentSessions.Count)
        {
            foreach (var s in _agentSessions)
                s.IsActive = false;

            var target = _agentSessions[activeIndex];
            target.IsActive = true;
            _activeAgentSession = target;

            if (AgentContentFor(target) is { } content)
            {
                AgentSlot.Content = content;
            }

            UpdateAgentSessionUI();
            AgentTabStrip.Items.Refresh();
        }

        // A pane saved before ids were chosen up front has just been given one, and it is
        // written now rather than left to depend on a clean exit.
        PersistAgentSessions();
    }

    private void InitializeRunnerSessions(string project)
    {
        foreach (var session in _runnerSessions)
            session.DisposeStatusSource();

        _runnerSessions.Clear();
        // Default runner session with startup message is created in OpenWorkspace
    }

    private int RestoreEditorTabs()
    {
        var restored = 0;

        // By the position each had in the saved list, which is what the saved panes name.
        // A tab that was skipped leaves a gap, and the pane that wanted it is dropped.
        var bySavedIndex = new Dictionary<int, EditorTab>();

        _restoringEditorTabs = true;
        try
        {
            for (var index = 0; index < _workspace.EditorTabs.Count; index++)
            {
                var saved = _workspace.EditorTabs[index];

                // A file that moved or was deleted between sessions opens as a Helix
                // error pane, so skip it and let the file picker take over.
                if (saved.Path is not null && !File.Exists(saved.Path))
                    continue;

                if (CreateEditorTab(saved.Path) is { } tab)
                {
                    restored++;
                    bySavedIndex[index] = tab;
                }
            }

            // Every tab was shown in the one pane as it was made, so the panes are put
            // back now that they all exist.
            if (restored > 1 && _workspace.EditorSplit is { } split)
            {
                var focus = bySavedIndex.TryGetValue(_workspace.EditorFocusedTab, out var focused)
                    ? focused.Host
                    : null;

                if (EditorSlot.Import(
                        split,
                        index => bySavedIndex.TryGetValue(index, out var tab) ? tab.Host : null,
                        focus)
                    && EditorSlot.FocusedHost is { } host)
                {
                    var active = _editorTabs.FirstOrDefault(tab => ReferenceEquals(tab.Host, host));
                    if (active is not null)
                        MarkActiveEditorTab(active);

                    // Each tab asked for the keyboard when it was made, and those requests
                    // are still queued; the one that runs last wins. So this one is queued
                    // behind them.
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.ContextIdle,
                        new Action(() => EditorSlot.FocusedHost?.BringToFront()));
                }
            }
        }
        finally
        {
            _restoringEditorTabs = false;
        }

        return restored;
    }

    // Brings back a split terminal: a fresh shell for each pane that was open, in the
    // arrangement they were in. The processes themselves are not kept between launches,
    // which is why a workspace that was not split just starts its one shell.
    private bool RestoreRunnerLayout()
    {
        var split = _workspace.RunnerSplit;
        if (split is null)
            return false;

        var wanted = new SortedSet<int>();
        CollectPaneTabs(split, wanted);
        if (wanted.Count < 2)
            return false;

        var bySavedIndex = new Dictionary<int, AgentSessionView>();

        _restoringEditorTabs = true;
        try
        {
            foreach (var index in wanted)
            {
                var directory = index >= 0 && index < _workspace.RunnerPanes.Count
                    ? _workspace.RunnerPanes[index].WorkingDirectory
                    : null;
                if (directory is not null && !Directory.Exists(directory))
                    directory = null;

                var before = _runnerSessions.Count;
                CreateRunnerShell(bySavedIndex.Count == 0 ? $" Try {_runCommand}" : null, directory);
                if (_runnerSessions.Count > before)
                    bySavedIndex[index] = _runnerSessions[^1];
            }

            if (bySavedIndex.Count == 0)
                return false;

            var focus = bySavedIndex.TryGetValue(_workspace.RunnerFocusedTab, out var focused)
                ? focused.Host
                : null;

            if (bySavedIndex.Count > 1
                && RunnerSlot.Import(
                    split,
                    index => bySavedIndex.TryGetValue(index, out var session) ? session.Host : null,
                    focus)
                && RunnerSlot.FocusedHost is { } host)
            {
                var active = _runnerSessions.FirstOrDefault(session => ReferenceEquals(session.Host, host));
                if (active is not null)
                    MarkActiveRunnerSession(active);
            }

            return true;
        }
        finally
        {
            _restoringEditorTabs = false;
        }
    }

    private static void CollectPaneTabs(PaneNodeState node, ISet<int> into)
    {
        if (node.Tab is { } tab)
            into.Add(tab);

        foreach (var child in node.Children ?? [])
            CollectPaneTabs(child, into);
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
        // The workspace's own, or failing that wherever the last window was: a project
        // opened for the first time and a window starting on the welcome screen have
        // nothing saved of their own.
        var geometry = _workspace.Window;
        if (geometry.Width < MinWidth || geometry.Height < MinHeight)
            geometry = _state.Window;
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

        // After the bounds, which are then what the window restores down to.
        if (geometry.IsMaximized)
            WindowState = WindowState.Maximized;
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
        _workspace.Window.IsMaximized = WindowState == WindowState.Maximized;

        // The same again for the app as a whole, as the default for a window that has
        // no workspace geometry to go by.
        _state.Window.Left = bounds.Left;
        _state.Window.Top = bounds.Top;
        _state.Window.Width = bounds.Width;
        _state.Window.Height = bounds.Height;
        _state.Window.IsMaximized = _workspace.Window.IsMaximized;
    }

    // ---- Zen mode ----------------------------------------------------------------
    //
    // The editor and nothing else: the three panels closed, and the title bar and the
    // editor's own tab strip folded away with them. It is part of the workspace's
    // layout, so a project left in zen mode opens in it.

    private void ZenToggle_Click(object sender, RoutedEventArgs e) => ToggleZen();

    private void ToggleZen()
    {
        if (_currentProject is null || WorkspaceRoot.Visibility != Visibility.Visible)
            return;

        var layout = _workspace.Layout;

        if (!layout.Zen)
        {
            // What was open is written down first, so leaving zen mode puts back this
            // arrangement rather than opening everything.
            layout.ZenLeftWasOpen = !layout.LeftCollapsed;
            layout.ZenRunnerWasOpen = !layout.RunnerCollapsed;
            layout.ZenAgentWasOpen = !layout.AgentCollapsed;
            layout.ZenWasMaximized = WindowState == WindowState.Maximized;
            layout.Zen = true;

            SetPanelCollapsed(ToolPanel.Left, true);
            SetPanelCollapsed(ToolPanel.Runner, true);
            SetPanelCollapsed(ToolPanel.Agent, true);
        }
        else
        {
            layout.Zen = false;

            if (layout.ZenLeftWasOpen)
                SetPanelCollapsed(ToolPanel.Left, false);
            if (layout.ZenRunnerWasOpen)
                SetPanelCollapsed(ToolPanel.Runner, false);
            // There is no agent pane to bring back while AI is off.
            if (layout.ZenAgentWasOpen && _state.AiEnabled)
                SetPanelCollapsed(ToolPanel.Agent, false);

            // Back to the size zen mode took over, unless it was already maximised.
            if (!layout.ZenWasMaximized && WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        }

        ApplyZenChrome(layout.Zen);
        ActiveEditorHost()?.FocusTerminal();

        // Saved once the tracks have settled, and written now rather than left for the
        // window closing: zen mode should survive a crash like any other layout change.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            SaveWorkspaceLayout();
            WriteWorkspace();
        }));
    }

    // The chrome only: which bars are showing. Kept apart from the panels so the
    // welcome screen can have its title bar back without the workspace behind it
    // leaving zen mode.
    //
    // In zen mode nothing is left but the editor: the title bar, the editor's tab strip
    // and the status bar all go, and the window fills the screen whatever size it was.
    // What they held is one touch of the top edge away, in the zen bar.
    private void ApplyZenChrome(bool zen)
    {
        TitleBar.Visibility = zen ? Visibility.Collapsed : Visibility.Visible;
        ZenReveal.Visibility = zen ? Visibility.Visible : Visibility.Collapsed;

        EditorHeader.Visibility = zen ? Visibility.Collapsed : Visibility.Visible;
        EditorHeaderRow.Height = new GridLength(zen ? 0 : 27);

        StatusBar.Visibility = zen ? Visibility.Collapsed : Visibility.Visible;
        StatusBarRow.Height = new GridLength(zen ? 0 : 28);

        ZenToggle.IsChecked = zen;

        // Set before the window is maximised, since the size is read as it happens.
        MaximizeBounds.FillMonitor = zen && _state.ZenFullScreen;

        if (zen)
        {
            if (WindowState != WindowState.Maximized)
                WindowState = WindowState.Maximized;
        }
        else
        {
            ZenBarPopup.IsOpen = false;
        }

        // A window that was already maximised is not asked its size again, so it is moved.
        // Once now, and once more when the layout pass has finished: a window restored
        // maximised is put at the stock size again by its own startup.
        ReapplyMaximizeBounds();
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(ReapplyMaximizeBounds));
    }

    private void ReapplyMaximizeBounds()
    {
        MaximizeBounds.Reapply(this);
        ApplyMaximizeInset();
    }

    private void ToggleZenFullScreen()
    {
        _state.ZenFullScreen = !_state.ZenFullScreen;
        _stateStore.Save(_state);

        // Takes effect now when zen mode is already on.
        if (_workspace.Layout.Zen)
            ApplyZenChrome(true);
    }

    // The top edge opens the zen bar. It is placed over the reveal strip, so the
    // pointer that opened it is already inside it and it stays for as long as the
    // pointer does.
    private void ZenReveal_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_workspace.Layout.Zen || ZenBarPopup.IsOpen)
            return;

        ZenNameText.Text = ProjectNamePillText.Text;
        ZenPathText.Text = ProjectPathPillText.Text;
        ZenBranchText.Text = GitBranchPillText.Text;
        ZenBranchPill.Visibility = string.IsNullOrEmpty(GitBranchPillText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ZenBarPopup.PlacementTarget = ZenReveal;
        ZenBarPopup.HorizontalOffset = 0;
        ZenBarPopup.VerticalOffset = 0;
        ZenBar.Width = ZenReveal.ActualWidth;
        ZenBarPopup.IsOpen = true;
    }

    private void ZenBar_MouseLeave(object sender, MouseEventArgs e)
    {
        // A tab being dragged holds the mouse, and the bar must not vanish from under it.
        if (_draggingTab)
            return;

        ZenBarPopup.IsOpen = false;
    }

    private void CloseWorkspace()
    {
        // Detach every host before disposing any of them, for the same reason
        // CloseEditorTab does: a host that is still in the visual tree can be sent
        // a focus change, and answering that on a torn-down pseudoconsole throws
        // out of an HWND message hook.
        LeftToolStack.Children.Clear();
        EditorSlot.Clear();
        RunnerSlot.Clear();
        AgentSlot.Content = null;

        foreach (var host in _terminalHosts.ToArray())
            host.Dispose();

        _terminalHosts.Clear();
        _editorTabs.Clear();
        _activeEditorTab = null;
        _leftHost = null;
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
    private static string PowerShellCommand(string executable, string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"{executable} -NoLogo -NoExit -EncodedCommand {encoded}";
    }

    // A runner tab on whichever shell this machine has, optionally opening with a line
    // of text. The shell is chosen per tab rather than once, so a pwsh installed while
    // Helide is open is what the next tab gets.
    private void CreateRunnerShell(
        string? greeting = null,
        string? workingDirectory = null,
        (NativeTerminalHost Anchor, DropEdge Edge)? beside = null)
    {
        var shell = ToolDefaults.RunnerShell();

        string commandLine;
        if (shell.IsPowerShell)
        {
            commandLine = greeting is null
                ? $"{shell.Executable} -NoLogo"
                : PowerShellCommand(
                    shell.Executable,
                    $"Write-Host {PowerShellLiteral(greeting)} -ForegroundColor DarkCyan");
        }
        else
        {
            // cmd has no encoded form, so the greeting is dropped rather than risk a
            // project name with an ampersand in it being run as a command.
            commandLine = QuoteArgument(shell.Executable);
        }

        CreateRunnerSession("runner", shell.Label, commandLine, workingDirectory, beside);
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

        // How they were split. A pane names its tab by its place in the list above, which
        // is the order they are reopened in.
        int EditorIndex(NativeTerminalHost host)
        {
            for (var i = 0; i < _editorTabs.Count; i++)
                if (ReferenceEquals(_editorTabs[i].Host, host))
                    return i;

            return -1;
        }

        _workspace.EditorSplit = EditorSlot.Export(EditorIndex);
        _workspace.EditorFocusedTab = EditorSlot.FocusedHost is { } focused ? EditorIndex(focused) : -1;

        // Not before the runner has been brought up for this workspace: until then there
        // are no shells, and what is saved would replace the layout that is about to be
        // read back with an empty one.
        if (!_runnerLayoutReady)
            return;

        int RunnerIndex(NativeTerminalHost host)
        {
            for (var i = 0; i < _runnerSessions.Count; i++)
                if (ReferenceEquals(_runnerSessions[i].Host, host))
                    return i;

            return -1;
        }

        _workspace.RunnerPanes = _runnerSessions
            .Select(session => new RunnerPaneState
            {
                WorkingDirectory = string.Equals(
                    session.WorkingDirectory, _currentProject, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : session.WorkingDirectory,
            })
            .ToList();
        _workspace.RunnerSplit = RunnerSlot.Export(RunnerIndex);
        _workspace.RunnerFocusedTab = RunnerSlot.FocusedHost is { } runnerFocused ? RunnerIndex(runnerFocused) : -1;
    }

    // True once the runner has been brought up for the open workspace; see SaveEditorTabs.
    private bool _runnerLayoutReady;

    // Set while saved tabs are being reopened, when the saved list is the thing being
    // read and must not be rewritten from a half-built set of tabs.
    private bool _restoringEditorTabs;

    // Writes the open editor tabs to disk as soon as the set changes, for the reason
    // the agent panes are: what was open should not depend on Helide exiting cleanly.
    private void PersistEditorTabs()
    {
        if (_currentProject is null || _restoringEditorTabs)
            return;

        SaveEditorTabs();
        WriteWorkspace();
    }

    // False while this window sits on the welcome screen with a workspace kept alive
    // behind it. It has let go of the project then, so the project's file may be
    // another window's to write.
    private bool _ownsProject;

    // The only place the workspace file is written from.
    private void WriteWorkspace()
    {
        if (_currentProject is null || !_ownsProject)
            return;

        AccrueOpenTime();
        _stateStore.SaveWorkspace(_workspace, _currentProject);
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
        var learned = false;

        // An OpenCode pane has nothing to capture: its id was chosen when it was created.
        //
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
            learned = true;
        }

        // Claude: the id a pane was launched with is only where it began. /resume and
        // /clear move the running process onto another conversation, and without
        // following it the saved id keeps naming the first one -- which for a pane that
        // was opened, sent to /resume and cancelled holds nothing but "Resume cancelled",
        // so every restart reopened that and the real conversation was lost.
        //
        // Claude records its current conversation under its own process id, so the pane
        // is asked rather than the transcript folder being watched and guessed at.
        foreach (var pane in _agentSessions)
        {
            if (pane.Type != "claude" || pane.Host?.ProcessId is not { } pid)
                continue;

            var live = ClaudeSessions.LiveSessionId([.. Helide.Interop.ProcessTree.SelfAndDescendants(pid)]);
            if (live is null || string.Equals(live, pane.SessionId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Two panes cannot be one conversation. If another already has this id, the
            // pane has been put into it deliberately and the one that was there first
            // keeps it.
            if (_agentSessions.Any(other => !ReferenceEquals(other, pane)
                && string.Equals(other.SessionId, live, StringComparison.OrdinalIgnoreCase)))
                continue;

            pane.SessionId = live;
            learned = true;
        }

        if (learned && !_restoringAgentSessions)
            PersistAgentSessions();
    }

    // Set while saved panes are being reopened, when the saved list is the thing being
    // read and must not be rewritten from a half-built set of panes.
    private bool _restoringAgentSessions;

    // Saved sessions whose agent is not installed right now. They have no pane, and they
    // are written back with the ones that do so an uninstall cannot lose a conversation.
    private readonly List<AgentSessionState> _dormantAgentSessions = [];

    // Copies the open agent panes into the workspace state.
    //
    // Does nothing while AI is off. There are no panes then, and writing that down would
    // replace every saved session with an empty list -- which is how a previous attempt
    // at the switch lost them.
    private void RecordAgentSessions()
    {
        if (!_state.AiEnabled)
            return;

        // IsActive is updated on every tab click, so it is the reliable witness of
        // what the user had selected. _activeAgentSession alone could be null at
        // close, which is how -1 was being written.
        var active = _agentSessions.Select((session, index) => (session, index))
            .FirstOrDefault(x => x.session.IsActive);
        _workspace.ActiveAgentIndex = active.session is null ? -1 : active.index;

        _workspace.AgentSessions.Clear();
        foreach (var session in _agentSessions)
        {
            _workspace.AgentSessions.Add(new AgentSessionState
            {
                Type = session.Type,
                Name = session.BaseName,
                CommandLine = session.Host is not null ? session.Host.Label : string.Empty,
                CreatedUtc = DateTime.UtcNow,
                SessionId = session.SessionId,
                WorkingDirectory = string.Equals(
                    session.WorkingDirectory, _currentProject, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : session.WorkingDirectory,
            });
        }

        _workspace.AgentSessions.AddRange(_dormantAgentSessions);
    }

    // Writes the open agent panes to disk as soon as the set changes, rather than only
    // when the window closes: a pane's session id is the only way back to its
    // conversation, and it should not depend on Helide exiting cleanly.
    private void PersistAgentSessions()
    {
        if (_currentProject is null || !_state.AiEnabled)
            return;

        RecordAgentSessions();
        WriteWorkspace();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        CaptureAgentSessionIds();
        RecordAgentSessions();

        SaveEditorTabs();
        SaveWorkspaceLayout();
        SaveWindowGeometry();

        // Only when a project is open: with no project there is nothing to key a
        // workspace file against, and writing one would create an orphan per launch
        // from the welcome screen.
        WriteWorkspace();

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

        public string ToolTipText => _changedOnDisk && _modified
            ? (Path ?? "Project file picker") + "\nChanged on disk while you had unsaved edits. Saving keeps yours; :reload takes the file's."
            : Path ?? "Project file picker";

        public System.Windows.Media.Brush Foreground => IsActive
            ? ThemePalette.Brush(ThemePalette.TextBrush)
            : ThemePalette.Brush(ThemePalette.TextSubtleBrush);

        // The active tab carries an accent underline as well as a lighter fill, so
        // which one is in use reads at a glance rather than only on hover.
        public System.Windows.Media.Brush Background => IsActive
            ? ThemePalette.Brush(ThemePalette.TerminalBackgroundBrush)
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

            source.BusyChanged += SetBusy;

            source.Start();
        }

        private bool _hovered;
        private bool _busy;

        // What the slot after the name shows, in order: a spinner while the tab is
        // working, then the green dot for something finished that was not looked at, then
        // the unsaved marker. Hover wins over all of them, so every tab can always be
        // closed; with none of them the slot is the close button.
        public bool ShowSpinner => _busy && !_hovered;

        public bool ShowAttentionDot => _needsAttention && !_busy && !_hovered;

        // Unsaved changes in the editor. Shown only while nothing else is claiming the
        // slot, since a pane that is also finishing something is the more urgent news.
        private bool _modified;

        public bool ShowModifiedDot => _modified && !_changedOnDisk && !_busy && !_needsAttention && !_hovered;

        // The file was written by something else -- an agent -- while this buffer had
        // edits of its own, so it was not reloaded under them. Red rather than the unsaved
        // yellow: saving now overwrites what was written.
        private bool _changedOnDisk;

        public bool ShowConflictDot => _modified && _changedOnDisk && !_busy && !_needsAttention && !_hovered;

        public void SetChangedOnDisk(bool changed)
        {
            if (_changedOnDisk == changed)
                return;

            _changedOnDisk = changed;
            NotifyIndicators();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
        }

        public void SetModified(bool modified)
        {
            if (_modified == modified)
                return;

            _modified = modified;

            // Saved, or reloaded: either way the buffer and the file agree again. Only
            // once it stays that way, though -- the marker drops out for a moment while
            // a command is typed on the line beneath it, and that is not a save.
            if (!modified && _changedOnDisk)
            {
                _agreeTimer ??= CreateAgreeTimer();
                _agreeTimer.Stop();
                _agreeTimer.Start();
            }

            NotifyIndicators();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
        }

        private System.Windows.Threading.DispatcherTimer? _agreeTimer;

        private System.Windows.Threading.DispatcherTimer CreateAgreeTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_modified)
                    SetChangedOnDisk(false);
            };
            return timer;
        }

        public bool IsModified => _modified;

        public bool ShowCloseButton => !ShowSpinner && !ShowAttentionDot && !ShowModifiedDot && !ShowConflictDot;

        public void SetHovered(bool hovered)
        {
            if (_hovered == hovered)
                return;

            _hovered = hovered;
            NotifyIndicators();
        }

        private void SetBusy(bool busy)
        {
            if (_busy == busy)
                return;

            _busy = busy;
            NotifyIndicators();
        }

        private void NotifyIndicators()
        {
            // The template binds the derived booleans, so each has to be announced; a
            // change to one input moves several of them.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSpinner)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAttentionDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowModifiedDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowConflictDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCloseButton)));
        }

        public void ClearAttention() => MarkAttention(clear: true);

        private void MarkAttention(bool clear = false)
        {
            if (_needsAttention == !clear)
                return;

            _needsAttention = !clear;

            // Not just NeedsAttention: the tab templates bind the derived booleans, so
            // notifying only the flag leaves the dot and the close button showing
            // whatever they were before.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsAttention)));
            NotifyIndicators();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record RecentProjectView(string Name, string Path, string Shortcut);

    // Agent tab strip click - switch sessions
    private void ActivateAgentSession(AgentSessionView session)
    {
        {
            // Deactivate all, activate this one
            foreach (var s in _agentSessions)
                s.IsActive = ReferenceEquals(s, session);

            // Switch to this session
            if (session.Host is not null)
            {
                AgentSlot.Content = session.Host;
                _activeAgentSession = session;
                session.ClearAttention();
                session.Host.BringToFront();
            }
            else if (IsHeld(session))
            {
                AgentSlot.Content = OpenCodeCard();
                _activeAgentSession = session;
                OpenCodeCard().FocusInput();
            }
            UpdateAgentSessionUI();
            AgentTabStrip.Items.Refresh();

            // Which tab was showing is part of what gets restored, and a switch to
            // another workspace reads it from the file rather than from this window.
            PersistAgentSessions();
        }
    }

    // Split out from the click handler so the command palette can close a session too.
    // It reads the session off the button, and a palette entry has no button to hand
    // it; this keeps the one place that knows how to dispose a host and drop the
    // session from both the list and the status sources.
    private void CloseAgentSession(AgentSessionView session)
    {
        session.DisposeStatusSource();
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
                AgentSlot.Content = AgentContentFor(nextSession);
                _activeAgentSession = nextSession;
                nextSession.Host?.BringToFront();
            }
            else
            {
                AgentSlot.Content = null;
                _activeAgentSession = null;
            }
        }

        UpdateAgentSessionUI();
        PersistAgentSessions();

        if (_agentSessions.Count == 0)
            CollapseEmptyPanel(ToolPanel.Agent);
    }
    private void ActivateRunnerSession(AgentSessionView session)
    {
        if (session.Host is null)
            return;

        RunnerSlot.Show(session.Host);
        MarkActiveRunnerSession(session);
        session.Host.BringToFront();
    }

    // The active session is the one in the pane that has focus; see MarkActiveEditorTab.
    private void MarkActiveRunnerSession(AgentSessionView session)
    {
        foreach (var s in _runnerSessions)
            s.IsActive = ReferenceEquals(s, session);

        _activeRunnerSession = session;
        session.ClearAttention();
        RunnerTabStrip.Items.Refresh();
    }

    private void RunnerSlot_FocusedHostChanged(NativeTerminalHost? host)
    {
        if (host is null)
            return;

        var session = _runnerSessions.FirstOrDefault(s => ReferenceEquals(s.Host, host));
        if (session is not null && !ReferenceEquals(session, _activeRunnerSession))
            MarkActiveRunnerSession(session);
    }

    // Runner session close
    private void CloseRunnerSession(AgentSessionView session)
    {
        var host = session.Host;
        var wasShown = host is not null && RunnerSlot.IsShown(host);
        var index = _runnerSessions.IndexOf(session);
        session.DisposeStatusSource();

        _runnerSessions.Remove(session);
        if (host is not null && _terminalHosts.Contains(host))
            _terminalHosts.Remove(host);

        // As for an editor tab: the pane shows a neighbour that was out of sight, or
        // closes up when there is none.
        var replacement = wasShown
            ? NearestHidden(_runnerSessions, index, RunnerSlot, s => s.Host!)
            : null;

        // Taken out of the panel before it is disposed, never after.
        if (host is not null)
        {
            RunnerSlot.Remove(host, replacement?.Host);
            host.Dispose();
        }

        if (_runnerSessions.Count > 0)
        {
            var focused = RunnerSlot.FocusedHost is { } focusedHost
                ? _runnerSessions.FirstOrDefault(s => ReferenceEquals(s.Host, focusedHost))
                : null;
            ActivateRunnerSession(focused ?? _runnerSessions.Last());
        }
        else
        {
            _activeRunnerSession = null;
            CollapseEmptyPanel(ToolPanel.Runner);
        }

        RunnerTabStrip.Items.Refresh();
    }

    private void RunnerNewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        CreateRunnerShell();
    }

    // `beside` puts the new session in a pane of its own next to the one showing that
    // host, instead of in the pane that has focus.
    private void CreateRunnerSession(
        string type,
        string label,
        string commandLine,
        string? workingDirectory = null,
        (NativeTerminalHost Anchor, DropEdge Edge)? beside = null)
    {
        if (_currentProject is null) return;
        try
        {
            var host = new NativeTerminalHost(type, commandLine, workingDirectory ?? _currentProject!)
            {
                CapturesCommandOutput = true,
            };
            RunnerSlot.Add(host);
            _terminalHosts.Add(host);

            var session = new AgentSessionView(type, label);
            session.Host = host;
            session.CommandLine = commandLine;
            session.WorkingDirectory = workingDirectory;
            _runnerSessions.Add(session);
            session.AttachStatusSource(new ShellStatusSource(host));

            if (beside is { } place && RunnerSlot.IsShown(place.Anchor))
                RunnerSlot.SplitBeside(place.Anchor, place.Edge, host);
            else
                RunnerSlot.Show(host);

            RunnerTabStrip.ItemsSource = null;
            RunnerTabStrip.ItemsSource = _runnerSessions;
            MarkActiveRunnerSession(session);
            ScrollStripToEnd(RunnerTabStripScroll);
        }
        catch (Exception exception)
        {
            RunnerSlot.Children.Add(CreateErrorPanel(type, exception));
        }
    }

    private sealed class AgentSessionView : INotifyPropertyChanged, IAttentiveTab
    {
        private bool _isActive;
        private bool _needsAttention;

        // See the same note on EditorTab: cleared on focus, so the dot always means
        // "not looked at yet".
        public bool NeedsAttention => _needsAttention;

        public string Type { get; }  // "opencode", "codex" or "claude"
        public string TypeLabel => AgentProviders.LabelFor(Type);
        // What the tab says: the conversation's title once it has one, else the name it
        // was opened with. Only that name is saved; the title is read again each time.
        public string Name => _title ?? BaseName;

        public string BaseName { get; }

        private string? _title;

        public string? Title
        {
            get => _title;
            set
            {
                if (_title == value)
                    return;

                _title = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
            }
        }
        public NativeTerminalHost? Host { get; set; }

        // Only set for runner sessions, which are a shell running a command line and
        // can therefore be recreated from it. Agent sessions build their own command
        // line from Type.
        public string CommandLine { get; set; } = string.Empty;

        // The directory the pane's process was started in. For an OpenCode pane this is
        // also the directory its conversation is filed under, so it is saved and the pane
        // is reopened there.
        public string? WorkingDirectory { get; set; }

        // The conversation this pane is on, saved so the next launch can reopen it. An
        // OpenCode pane is given one when it is created; a codex pane learns its own from
        // the rollout file it writes.
        public string? SessionId { get; set; }

        // Set when this pane was launched as a brand new codex conversation, i.e.
        // with no resume argument, and holds the moment the process was started.
        // Only such a pane has a session id still to learn; a pane launched with
        // `resume <id>` was already told which thread to open. The timestamp is what
        // decides which rollout file belongs to which pane when several start at once.
        public DateTime? AwaitingSessionSince { get; set; }

        // The files this pane's last turn edited, as its agent recorded them, each with
        // the line its first change sits on.
        public IReadOnlyList<TurnEdit> TurnEdits { get; set; } = [];

        public AgentSessionView(string type, string name)
        {
            Type = type;
            BaseName = name;
        }

        // What the shared tab template shows on hover: the whole title, which the tab
        // itself cuts short, and the agent it is in.
        public string ToolTipText => _title is null ? BaseName : $"{BaseName} · {_title}";

        // The source polls on a timer of its own, so a pane that is closed has to stop
        // it or it keeps reading a file for a tab that is gone.
        public void DisposeStatusSource()
        {
            _statusSource?.Dispose();
            _statusSource = null;
        }

        public System.Windows.Media.Brush Background => IsActive
            ? ThemePalette.Brush(ThemePalette.TerminalBackgroundBrush)
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

            source.BusyChanged += SetBusy;

            source.Start();
        }

        private bool _hovered;
        private bool _busy;

        // What the slot after the name shows, in order: a spinner while the tab is
        // working, then the green dot for something finished that was not looked at, then
        // the unsaved marker. Hover wins over all of them, so every tab can always be
        // closed; with none of them the slot is the close button.
        public bool ShowSpinner => _busy && !_hovered;

        public bool ShowAttentionDot => _needsAttention && !_busy && !_hovered;

        // Only an editor tab has unsaved changes.
        public bool ShowModifiedDot => false;

        public bool ShowCloseButton => !ShowSpinner && !ShowAttentionDot && !ShowModifiedDot;

        public void SetHovered(bool hovered)
        {
            if (_hovered == hovered)
                return;

            _hovered = hovered;
            NotifyIndicators();
        }

        private void SetBusy(bool busy)
        {
            if (_busy == busy)
                return;

            _busy = busy;
            NotifyIndicators();
        }

        private void NotifyIndicators()
        {
            // The template binds the derived booleans, so each has to be announced; a
            // change to one input moves several of them.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSpinner)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAttentionDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowModifiedDot)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCloseButton)));
        }

        public void ClearAttention() => MarkAttention(clear: true);

        private void MarkAttention(bool clear = false)
        {
            if (_needsAttention == !clear)
                return;

            _needsAttention = !clear;

            // Not just NeedsAttention: the tab templates bind the derived booleans, so
            // notifying only the flag leaves the dot and the close button showing
            // whatever they were before.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsAttention)));
            NotifyIndicators();
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
