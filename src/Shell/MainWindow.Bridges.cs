using System.IO;
using System.Windows;
using System.Windows.Threading;
using Helide.Sessions;
using Helide.Shell;
using Helide.Terminal;
using Forms = System.Windows.Forms;

namespace Helide;

// The seams between panels: an agent's edits reaching the open editors, a file or a
// command's output handed to an agent, and a finished turn reaching you when Helide is
// not in front. None of it changes a tool's own configuration; each tool only sees input
// a person could have typed.
public partial class MainWindow
{
    // ---- turns ----------------------------------------------------------------------

    private void OnAgentTurnFinished(AgentSessionView session)
    {
        RefreshUsageSummary();
        RefreshUsageLimits();
        ScheduleEditorReload();
        AlertTurnFinished(session);
        LoadTurnEdits(session);
        RefreshContextMeter();
    }

    // ---- editors follow the files an agent writes ------------------------------------

    // Helix does not notice a file changing on disk. Left alone, an editor tab goes on
    // showing what the agent has just rewritten, and saving it puts the old text back.

    private FileSystemWatcher? _projectWatcher;
    private readonly HashSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _changesOverflowed;
    private DispatcherTimer? _editorReloadTimer;

    // More than this between two turns and the names stop being worth keeping: every
    // editor is treated as possibly stale.
    private const int ChangedFilesLimit = 4096;

    private void WatchProjectFiles(string project)
    {
        _projectWatcher?.Dispose();
        _projectWatcher = null;

        lock (_changedFiles)
        {
            _changedFiles.Clear();
            _changesOverflowed = false;
        }

        try
        {
            var watcher = new FileSystemWatcher(project)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Changed += (_, e) => RecordChange(e.FullPath);
            watcher.Created += (_, e) => RecordChange(e.FullPath);
            watcher.Deleted += (_, e) => RecordChange(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                RecordChange(e.OldFullPath);
                RecordChange(e.FullPath);
            };

            // Events were lost: which files changed is no longer known.
            watcher.Error += (_, _) =>
            {
                lock (_changedFiles)
                    _changesOverflowed = true;
            };

            watcher.EnableRaisingEvents = true;
            _projectWatcher = watcher;
        }
        catch (Exception)
        {
            // Without a watcher the editors are not reloaded; the palette command still is.
        }
    }

    // Runs on the watcher's thread.
    private void RecordChange(string path)
    {
        if (path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(Path.DirectorySeparatorChar + ".git", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_changedFiles)
        {
            if (_changesOverflowed)
                return;

            _changedFiles.Add(path);
            if (_changedFiles.Count > ChangedFilesLimit)
            {
                _changedFiles.Clear();
                _changesOverflowed = true;
            }
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScheduleOpenFileReload));
    }

    // A tab whose own file changed is not left until the turn ends: with Helix saving on
    // its own -- after a pause, or as focus leaves -- one keystroke in a stale buffer
    // writes it back over what the agent just wrote. So it is reloaded as soon as the
    // writing stops.
    //
    // Except the editor that has the keyboard. Its own saves arrive here too, and a reload
    // starts with Escape, which would throw you out of insert mode each time you paused.
    // That one is brought up to date when the turn ends, like the file picker tab, which
    // can have any file open and would otherwise be interrupted on every write.
    private DispatcherTimer? _openFileReloadTimer;

    private void ScheduleOpenFileReload()
    {
        if (_openFileReloadTimer is null)
        {
            _openFileReloadTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };
            _openFileReloadTimer.Tick += (_, _) =>
            {
                _openFileReloadTimer.Stop();
                ReloadOpenFileTabs();
            };
        }

        _openFileReloadTimer.Stop();
        _openFileReloadTimer.Start();
    }

    private void ReloadOpenFileTabs()
    {
        HashSet<string> changed;
        lock (_changedFiles)
        {
            if (_changesOverflowed || _changedFiles.Count == 0)
                return;

            changed = new HashSet<string>(_changedFiles, StringComparer.OrdinalIgnoreCase);
        }

        var handled = new List<string>();
        foreach (var tab in _editorTabs.ToArray())
        {
            if (FullPathOrNull(tab.Path) is not { } path
                || !changed.Contains(path)
                || tab.Host.State != TerminalHostState.Ready)
                continue;

            // Saying so touches nothing in the editor, so that is done wherever the
            // keyboard is; only a reload has to wait for it to be elsewhere.
            if (tab.IsModified)
                tab.SetChangedOnDisk(true);
            else if (!tab.Host.HasKeyboardFocus())
                tab.Host.RunEditorCommand(":reload-all");
            else
                continue;

            handled.Add(path);
        }

        lock (_changedFiles)
        {
            foreach (var path in handled)
                _changedFiles.Remove(path);
        }
    }

    // An agent can still be writing as it reports the turn over, so the reload waits for
    // a moment of quiet.
    private void ScheduleEditorReload()
    {
        _editorReloadTimer ??= CreateEditorReloadTimer();
        _editorReloadTimer.Stop();
        _editorReloadTimer.Start();
    }

    private DispatcherTimer CreateEditorReloadTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ReloadChangedEditors(everything: false);
        };
        return timer;
    }

    /// <summary>
    /// Brings editors up to date with the files on disk.
    /// </summary>
    /// <remarks>
    /// Only an editor showing a file that changed is reloaded, so the rest are not touched
    /// at all. The file picker tab can have any file open, so any change reloads it. An
    /// editor with unsaved edits is never reloaded -- that would throw the edits away --
    /// and if its file changed underneath, the tab says so instead.
    /// </remarks>
    private void ReloadChangedEditors(bool everything)
    {
        HashSet<string> changed;
        bool overflowed;
        lock (_changedFiles)
        {
            changed = new HashSet<string>(_changedFiles, StringComparer.OrdinalIgnoreCase);
            overflowed = _changesOverflowed;
            _changedFiles.Clear();
            _changesOverflowed = false;
        }

        if (!everything && !overflowed && changed.Count == 0)
            return;

        foreach (var tab in _editorTabs.ToArray())
        {
            if (tab.Host.State != TerminalHostState.Ready)
                continue;

            var path = FullPathOrNull(tab.Path);
            var touched = everything || overflowed || (path is null ? changed.Count > 0 : changed.Contains(path));

            if (!touched)
                continue;

            if (tab.IsModified)
            {
                if (path is not null && changed.Contains(path))
                    tab.SetChangedOnDisk(true);
                continue;
            }

            tab.Host.RunEditorCommand(":reload-all");
        }
    }

    private static string? FullPathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- handing things to the agent -------------------------------------------------

    /// <summary>
    /// Puts <c>@path</c> for the file in the editor into the agent's input, without
    /// sending it, and moves the keyboard there to finish the message.
    /// </summary>
    private void MentionFileInAgent()
    {
        if (_activeEditorTab?.Path is not { } file)
        {
            ShowNotice("Mention File in Agent", "Open a file in the editor first.");
            return;
        }

        if (_activeAgentSession?.Host is not { } agent)
        {
            ShowNotice("Mention File in Agent", "Open an agent tab first.");
            return;
        }

        // Relative to where the agent runs, which is how all three agents resolve a
        // mention; a file outside that folder keeps its full path.
        var root = _activeAgentSession.WorkingDirectory ?? _currentProject!;
        var relative = Path.GetRelativePath(root, file);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            relative = file;

        if (agent.PasteText("@" + relative.Replace('\\', '/') + " "))
            FocusPanel(ToolPanel.Agent, agent);
    }

    /// <summary>
    /// Pastes the terminal's selection into the agent's input, or, with nothing
    /// selected, what the last command printed. Not sent: the question is yours to add.
    /// </summary>
    private void SendTerminalOutputToAgent()
    {
        if (_activeRunnerSession?.Host is not { } shell)
        {
            ShowNotice("Send Terminal Output to Agent", "Open a terminal tab first.");
            return;
        }

        if (_activeAgentSession?.Host is not { } agent)
        {
            ShowNotice("Send Terminal Output to Agent", "Open an agent tab first.");
            return;
        }

        var text = shell.SelectedText() ?? shell.LastCommandOutput();
        if (text is null)
        {
            ShowNotice(
                "Send Terminal Output to Agent",
                "Nothing to send yet. Run a command in the terminal, or select the output to send.");
            return;
        }

        if (agent.PasteText("Terminal output:\n```\n" + text.TrimEnd() + "\n```\n"))
            FocusPanel(ToolPanel.Agent, agent);
    }

    // ---- telling you a turn finished -------------------------------------------------

    private Forms.NotifyIcon? _turnNotifier;
    private AgentSessionView? _notifiedSession;

    private void AlertTurnFinished(AgentSessionView session)
    {
        // In front, the tab's spinner turning into a dot already says it.
        if (IsActive && WindowState != WindowState.Minimized)
            return;

        if (_state.AgentDoneSound)
            System.Media.SystemSounds.Asterisk.Play();

        if (_state.AgentDoneNotification)
            ShowTurnNotification(session);
    }

    // A tray balloon is what Windows shows as a notification for a desktop app that is not
    // packaged; it needs no registration and nothing installed. The icon only stays in the
    // tray while there is something to come back to.
    private void ShowTurnNotification(AgentSessionView session)
    {
        try
        {
            if (_turnNotifier is null)
            {
                _turnNotifier = new Forms.NotifyIcon
                {
                    Icon = Environment.ProcessPath is { } exe
                        ? System.Drawing.Icon.ExtractAssociatedIcon(exe)
                        : System.Drawing.SystemIcons.Application,
                    Text = "Helide",
                };
                _turnNotifier.BalloonTipClicked += (_, _) => ReturnToNotifiedSession();
                _turnNotifier.MouseClick += (_, _) => ReturnToNotifiedSession();

                Activated += (_, _) =>
                {
                    if (_turnNotifier is not null)
                        _turnNotifier.Visible = false;
                };
                Closed += (_, _) => _turnNotifier?.Dispose();
            }

            _notifiedSession = session;
            var project = _currentProject is null ? "Helide" : new DirectoryInfo(_currentProject).Name;

            _turnNotifier.Visible = true;
            _turnNotifier.ShowBalloonTip(
                5000,
                $"{session.TypeLabel} finished",
                $"{project} · {session.Name}",
                Forms.ToolTipIcon.None);
        }
        catch (Exception)
        {
            // No notification area, or no icon: the sound and the tab's dot still say it.
        }
    }

    private void ReturnToNotifiedSession()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();

        if (_notifiedSession is { } session && _agentSessions.Contains(session))
        {
            ActivateAgentSession(session);
            FocusPanel(ToolPanel.Agent, session.Host);
        }
    }

    // ---- palette -----------------------------------------------------------------------

    private PaletteCommand ReloadFilesCommand(Func<bool> workspace) => new(
        "Reload Open Files From Disk",
        () => ReloadChangedEditors(everything: true),
        "File",
        "skips unsaved",
        keywords: "refresh helix buffers changed agent",
        canInvoke: () => workspace() && _editorTabs.Count > 0);

    private void AddBridgeCommands(List<PaletteCommand> commands, Func<bool> workspace)
    {
        if (!_state.AiEnabled)
            return;

        commands.Add(new PaletteCommand(
            "Mention Current File in Agent",
            MentionFileInAgent,
            "AI",
            PaletteCommand.Gesture(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift),
            keywords: "@ reference file path send editor",
            canInvoke: () => workspace() && _activeEditorTab?.Path is not null && _activeAgentSession is not null));

        commands.Add(new PaletteCommand(
            "Open Past Conversation",
            ShowPastConversations,
            "AI",
            "all agents, this project",
            keywords: "resume session history claude codex opencode",
            canInvoke: workspace));

        commands.Add(new PaletteCommand(
            "Send Terminal Output to Agent",
            SendTerminalOutputToAgent,
            "AI",
            "selection, or last command",
            keywords: "error log runner paste failed",
            canInvoke: () => workspace() && _activeRunnerSession is not null && _activeAgentSession is not null));

        commands.Add(new PaletteCommand(
            _state.AgentDoneSound ? "Turn Off Sound When an Agent Finishes" : "Turn On Sound When an Agent Finishes",
            () =>
            {
                _state.AgentDoneSound = !_state.AgentDoneSound;
                _stateStore.Save(_state);
                if (_state.AgentDoneSound)
                    System.Media.SystemSounds.Asterisk.Play();
            },
            "AI",
            "while Helide is in the background",
            keywords: "bell chime alert done turn"));

        commands.Add(new PaletteCommand(
            _state.AgentDoneNotification ? "Turn Off Notification When an Agent Finishes" : "Turn On Notification When an Agent Finishes",
            () =>
            {
                _state.AgentDoneNotification = !_state.AgentDoneNotification;
                _stateStore.Save(_state);
            },
            "AI",
            "while Helide is in the background",
            keywords: "toast windows alert done turn"));
    }
}
