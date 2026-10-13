using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Helide.Projects;
using Helide.Services;
using Helide.Shell;
using Button = System.Windows.Controls.Button;

namespace Helide;

// The title bar's project and branch pickers, and what the left pane's strip does. Kept
// out of MainWindow.xaml.cs, which is already most of the application.
public partial class MainWindow
{
    // Segoe Fluent Icons: "more", and a check. A button shows the first while its
    // command runs and the second for a moment once it has worked.
    private const string BusyGlyph = "";
    private const string DoneGlyph = "";
    private static readonly TimeSpan DoneGlyphTime = TimeSpan.FromMilliseconds(900);

    private static readonly PaletteMode ProjectsMode =
        new("Search projects", "project", "projects", "open");

    private static readonly PaletteMode BranchesMode =
        new("Search branches", "branch", "branches", "switch");

    private bool _isGitRepository;
    private bool _gitOperationRunning;
    private FileSystemWatcher? _gitHeadWatcher;
    private string? _gitHeadWatchedProject;
    private DispatcherTimer? _gitHeadDebounce;
    private DispatcherTimer? _gitStatusTimer;
    private bool _gitStatusRunning;

    // ---- Branch ----------------------------------------------------------------

    // Reads HEAD and brings the branch pill, and whether the git strip is usable, in
    // line with it. Called when a project opens, when HEAD changes, when the window is
    // activated, and after anything here that could have moved the branch.
    private void RefreshGitBranch()
    {
        if (_currentProject is not { } project)
            return;

        var branch = ProjectDetector.DetectGitBranch(project);
        _isGitRepository = branch is not null;

        GitBranchPillText.Text = branch ?? string.Empty;

        // Not shown while the welcome screen is: a workspace kept alive behind it still
        // updates the text, and ShowWorkspace makes the pill visible again.
        GitBranchPill.Visibility = branch is null || WorkspaceRoot.Visibility != Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Re-checked here so a repository created after the project was opened is picked
        // up the next time the window is activated.
        if (_gitHeadWatcher is null
            || !string.Equals(_gitHeadWatchedProject, project, StringComparison.OrdinalIgnoreCase))
            WatchGitHead(project);

        UpdateLeftStrip();
        StartGitStatusTimer();
        _ = RefreshGitStatusAsync();
        RefreshProjectInfo();
    }

    // The pill used to be read once when the project opened, so a checkout made in
    // lazygit left it naming the old branch. A picker that names the wrong branch is
    // worse than no picker, so HEAD is watched.
    private void WatchGitHead(string project)
    {
        StopWatchingGitHead();

        if (ProjectDetector.ResolveGitDirectory(project) is not { } gitDirectory)
            return;

        try
        {
            // git changes HEAD by writing HEAD.lock and renaming it over HEAD, which
            // arrives as a rename or a creation depending on the moment, so all three
            // are listened to.
            var watcher = new FileSystemWatcher(gitDirectory, "HEAD")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            watcher.Changed += GitHead_Changed;
            watcher.Created += GitHead_Changed;
            watcher.Renamed += GitHead_Changed;
            watcher.EnableRaisingEvents = true;

            _gitHeadWatcher = watcher;
            _gitHeadWatchedProject = project;
        }
        catch (Exception)
        {
            // A branch pill that does not follow checkouts is a loss, not a failure.
        }
    }

    private void StopWatchingGitHead()
    {
        _gitHeadWatcher?.Dispose();
        _gitHeadWatcher = null;
        _gitHeadWatchedProject = null;

        _gitStatusTimer?.Stop();
        _gitStatusTimer = null;
    }

    private void GitHead_Changed(object sender, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            // Several events for one checkout; the timer folds them into one read.
            if (_gitHeadDebounce is null)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    RefreshGitBranch();
                };
                _gitHeadDebounce = timer;
            }

            _gitHeadDebounce.Stop();
            _gitHeadDebounce.Start();
        });

    // ---- Git readout -----------------------------------------------------------

    // Edits made in the editor and in lazygit do not announce themselves to the window,
    // so the readout is asked again on a timer -- but only while someone could be
    // looking at it. Hidden, or with another application in front, it does nothing.
    private void StartGitStatusTimer()
    {
        if (_gitStatusTimer is not null)
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            if (IsActive && GitToolStrip.IsVisible)
                _ = RefreshGitStatusAsync();
        };
        timer.Start();
        _gitStatusTimer = timer;
    }

    private async Task RefreshGitStatusAsync()
    {
        if (_gitStatusRunning || _currentProject is not { } project)
            return;

        if (!_isGitRepository)
        {
            GitStatusText.Text = "not a repository";
            return;
        }

        _gitStatusRunning = true;
        try
        {
            var status = await GitCommands.StatusAsync(project);

            // The window may have moved to another project while git was answering.
            if (status is null || !string.Equals(project, _currentProject, StringComparison.OrdinalIgnoreCase))
                return;

            var parts = new List<string>();

            if (!status.HasUpstream)
            {
                parts.Add("no upstream");
            }
            else
            {
                if (status.Ahead > 0)
                    parts.Add($"↑{status.Ahead}");
                if (status.Behind > 0)
                    parts.Add($"↓{status.Behind}");
            }

            parts.Add(status.Changed == 0 ? "clean" : $"{status.Changed} changed");

            GitStatusText.Text = string.Join("  ·  ", parts);
        }
        finally
        {
            _gitStatusRunning = false;
        }
    }

    // ---- Title bar pickers ------------------------------------------------------

    private void ProjectNamePill_Click(object sender, RoutedEventArgs e)
    {
        if (!_paletteOpen)
            ShowPalette(BuildProjectPickerCommands(), ProjectsMode);
    }

    private async void GitBranchPill_Click(object sender, RoutedEventArgs e) =>
        await OpenBranchPickerAsync();

    // The recent projects, and the ways of getting to one that is not among them. The
    // open project is left out: it is what the pill already says, and choosing it would
    // do nothing.
    private List<PaletteCommand> BuildProjectPickerCommands()
    {
        var commands = new List<PaletteCommand>();

        foreach (var project in _state.RecentProjects)
        {
            if (_currentProject is not null
                && string.Equals(project.Path, _currentProject, StringComparison.OrdinalIgnoreCase))
                continue;

            var path = project.Path;
            commands.Add(new PaletteCommand(
                project.Name,
                () => OpenRecentProject(path),
                "Recent",
                path,
                keywords: path));
        }

        commands.Add(new PaletteCommand(
            "Open Folder…",
            OpenProjectFolder,
            "File",
            PaletteCommand.Gesture(Key.O),
            keywords: "new project browse"));
        commands.Add(new PaletteCommand(
            "Close Workspace",
            ShowWelcome,
            "File",
            "project chooser",
            keywords: "welcome home"));
        commands.Add(new PaletteCommand(
            "Open New Window",
            () => OpenInNewWindow(null),
            "Window",
            "project chooser",
            keywords: "blank empty new instance"));

        return commands;
    }

    private async Task OpenBranchPickerAsync()
    {
        if (_currentProject is not { } project || _paletteOpen)
            return;

        var listing = await GitCommands.ListBranchesAsync(project);

        // The window may have moved on, or opened the palette, while git was answering.
        if (!string.Equals(project, _currentProject, StringComparison.OrdinalIgnoreCase) || _paletteOpen)
            return;

        if (listing.Error is not null)
        {
            ShowNotice("Switch branch", listing.Error, monospace: true);
            return;
        }

        var commands = new List<PaletteCommand>();
        foreach (var branch in listing.Branches)
        {
            var target = branch;
            commands.Add(new PaletteCommand(
                branch.Name,
                () => _ = SwitchBranchAsync(project, target),
                branch.IsRemote ? "Remote" : "Branch",
                branch.IsCurrent ? "current" : branch.IsRemote ? "creates a local branch" : null));
        }

        ShowPalette(commands, BranchesMode);
    }

    private async Task SwitchBranchAsync(string project, GitBranch branch)
    {
        if (branch.IsCurrent)
            return;

        if (_gitOperationRunning)
        {
            ShowNotice(
                "Switch branch",
                "A git command is still running. Try again when it has finished.",
                suppressKey: "git-busy");
            return;
        }

        // A remote-only branch gets a local branch that tracks it, which is what
        // checking one out means everywhere else.
        string[] arguments = branch.IsRemote
            ? ["checkout", "--track", branch.Name]
            : ["checkout", branch.Name];

        _gitOperationRunning = true;
        UpdateLeftStrip();

        var result = await GitCommands.RunAsync(project, arguments);

        _gitOperationRunning = false;
        if (!IsLoaded)
            return;

        UpdateLeftStrip();
        RefreshGitBranch();

        // A failure here is nearly always a working tree that has changes in the way,
        // and git's own wording says which files.
        if (!result.Success)
            ShowNotice($"Could not switch to {branch.Name}", result.Message, monospace: true);
    }

    // ---- Git strip --------------------------------------------------------------

    private void UpdateLeftStrip()
    {
        var enabled = _isGitRepository && !_gitOperationRunning;

        foreach (var button in new[]
        {
            GitFetchButton, GitPullButton, GitPushButton, GitForcePushButton,
            GitOpenRemoteButton, GitCopyBranchButton,
        })
            button.IsEnabled = enabled;
    }

    private async void GitFetchButton_Click(object sender, RoutedEventArgs e) =>
        await RunGitStripOperationAsync(GitFetchButton, "Fetch", project =>
            GitCommands.RunAsync(project, ["fetch", "--all", "--prune"], talksToRemote: true));

    // --no-edit: a merge made by a pull would otherwise open an editor, and there is no
    // terminal here to show one in.
    private async void GitPullButton_Click(object sender, RoutedEventArgs e) =>
        await RunGitStripOperationAsync(GitPullButton, "Pull", project =>
            GitCommands.RunAsync(project, ["pull", "--no-edit"], talksToRemote: true));

    private async void GitPushButton_Click(object sender, RoutedEventArgs e) =>
        await RunGitStripOperationAsync(GitPushButton, "Push", project => PushAsync(project, force: false));

    private async void GitForcePushButton_Click(object sender, RoutedEventArgs e)
    {
        var branch = string.IsNullOrEmpty(GitBranchPillText.Text) ? "this branch" : $"'{GitBranchPillText.Text}'";

        var confirmed = await ConfirmAsync(
            "Force push",
            $"Force push {branch}?\n\n"
            + "The remote branch is replaced with yours, and commits that exist only there are lost.\n\n"
            + "It uses --force-with-lease, so it is refused if someone else has pushed since you last fetched.",
            "Force push",
            danger: true,
            suppressKey: "git-force-push");

        if (!confirmed)
            return;

        await RunGitStripOperationAsync(GitForcePushButton, "Force push", project => PushAsync(project, force: true));
    }

    // A branch with no upstream is published to one rather than failing with git's
    // "has no upstream branch" and leaving the person to type the fix.
    private static async Task<GitResult> PushAsync(string project, bool force)
    {
        var arguments = new List<string> { "push" };
        if (force)
            arguments.Add("--force-with-lease");

        if (await GitCommands.UpstreamAsync(project) is null)
        {
            var remote = await GitCommands.PreferredRemoteAsync(project);
            if (remote is null)
                return new GitResult(false, string.Empty, "This repository has no remote to push to.");

            arguments.AddRange(["--set-upstream", remote, "HEAD"]);
        }

        return await GitCommands.RunAsync(project, arguments, talksToRemote: true);
    }

    // The button that was pressed shows that it is working and then that it worked; the
    // rest are held until it is done, so two commands never race over one repository. A
    // failure is a card over the window, because it is the one result that needs reading.
    private async Task RunGitStripOperationAsync(Button button, string title, Func<string, Task<GitResult>> work)
    {
        if (_currentProject is not { } project || _gitOperationRunning || button.Content is not GlyphIcon icon)
            return;

        var idleGlyph = icon.Glyph;

        _gitOperationRunning = true;
        UpdateLeftStrip();
        button.IsEnabled = true;
        icon.Glyph = BusyGlyph;

        GitResult result;
        try
        {
            result = await work(project);
        }
        catch (Exception exception)
        {
            result = new GitResult(false, string.Empty, exception.Message);
        }

        if (!IsLoaded)
            return;

        if (result.Success)
        {
            icon.Glyph = DoneGlyph;
            await Task.Delay(DoneGlyphTime);
        }

        icon.Glyph = idleGlyph;
        _gitOperationRunning = false;
        UpdateLeftStrip();
        RefreshGitBranch();

        if (!result.Success)
            ShowNotice($"{title} failed", result.Message, monospace: true);
    }

    private async void GitOpenRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is not { } project)
            return;

        var url = await GitCommands.RemoteWebUrlAsync(project);
        if (url is null)
        {
            ShowNotice(
                "Open on remote",
                "This repository has no remote with a web address.",
                suppressKey: "git-no-web-remote");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowNotice("Open on remote", exception.Message);
        }
    }

    private async void GitCopyBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && GitBranchPillText.Text.Length > 0)
            await CopyAcknowledgedAsync(button, GitBranchPillText.Text);
    }

    // ---- Project strip ----------------------------------------------------------

    // The top level of the project, which is the one count that does not depend on where
    // yazi has been navigated to. Hidden entries are not counted, as yazi does not show
    // them by default.
    private void RefreshProjectInfo()
    {
        if (_currentProject is not { } project)
            return;

        try
        {
            var folders = 0;
            var files = 0;

            foreach (var entry in new DirectoryInfo(project).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.Hidden) || entry.Name.StartsWith('.'))
                    continue;

                if (entry is DirectoryInfo)
                    folders++;
                else
                    files++;
            }

            ProjectInfoText.Text = $"{folders} folders  ·  {files} files";
        }
        catch (Exception)
        {
            ProjectInfoText.Text = string.Empty;
        }
    }

    private async void ProjectNewFileButton_Click(object sender, RoutedEventArgs e) =>
        await CreateInProjectAsync(folder: false);

    private async void ProjectNewFolderButton_Click(object sender, RoutedEventArgs e) =>
        await CreateInProjectAsync(folder: true);

    // Made in the project root, because that is the one place that is not a guess: what
    // yazi is looking at is not something Helide can read. A relative path is allowed,
    // so a file can still be put in a subfolder, but not outside the project.
    private async Task CreateInProjectAsync(bool folder)
    {
        if (_currentProject is not { } project)
            return;

        var kind = folder ? "folder" : "file";
        var name = await PromptAsync(
            $"New {kind}",
            "Created in the project root. A path such as src\\app.ts puts it in a subfolder.",
            $"{kind} name",
            "Create");

        if (string.IsNullOrWhiteSpace(name))
            return;

        string full;
        try
        {
            var root = Path.GetFullPath(project).TrimEnd('\\') + "\\";
            full = Path.GetFullPath(Path.Combine(project, name.Trim().Replace('/', '\\')));

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                ShowNotice($"New {kind}", "That path is outside the project.");
                return;
            }

            if (File.Exists(full) || Directory.Exists(full))
            {
                ShowNotice($"New {kind}", $"{Path.GetFileName(full)} already exists.");
                return;
            }

            if (folder)
            {
                Directory.CreateDirectory(full);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using (File.Create(full))
                {
                }
            }
        }
        catch (Exception exception)
        {
            ShowNotice($"Could not create the {kind}", exception.Message);
            return;
        }

        RefreshProjectInfo();

        if (!folder)
            OpenEditorTab(full);
    }

    // yazi opens in the project root, so starting it again is the way back there. That
    // also drops whatever it had selected, which is the price of it: its working
    // directory cannot be set from outside.
    private void ProjectRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeLeftTool != LeftTool.Project || _leftHost is null || _yaziPromptPending)
            return;

        StopLeftTool();
        FocusWhenReady(StartLeftTool(LeftTool.Project));
    }

    private void ProjectRevealButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is not { } project)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { project },
                UseShellExecute = false,
            });
        }
        catch (Exception exception)
        {
            ShowNotice("Show in File Explorer", exception.Message);
        }
    }

    private async void ProjectCopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is { } project && sender is Button button)
            await CopyAcknowledgedAsync(button, project);
    }

    // Copies, and shows a check on the button for a moment so the click is seen to have
    // done something.
    private async Task CopyAcknowledgedAsync(Button button, string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program has the clipboard open. Nothing was copied, so nothing is
            // acknowledged.
            return;
        }

        if (button.Content is not GlyphIcon icon)
            return;

        var idleGlyph = icon.Glyph;
        icon.Glyph = DoneGlyph;
        await Task.Delay(DoneGlyphTime);
        icon.Glyph = idleGlyph;
    }
}
