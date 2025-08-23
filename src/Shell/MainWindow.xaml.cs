using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Helide.Interop;
using Helide.Persistence;
using Helide.Projects;
using Helide.Terminal;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Helide;

public partial class MainWindow : Window
{
    private readonly AppStateStore _stateStore;
    private readonly AppState _state;
    private readonly string? _startupProject;
    private readonly List<NativeTerminalHost> _terminalHosts = [];
    private NativeTerminalHost? _runnerHost;
    private string? _currentProject;
    private string _runCommand = "pwsh";
    private bool _loaded;
    private bool _closingWorkspace;

    internal MainWindow(AppStateStore stateStore, AppState state, string? startupProject = null)
    {
        _stateStore = stateStore;
        _state = state;
        _startupProject = startupProject;

        InitializeComponent();
        RestoreWindowGeometry();
        RestoreWorkspaceLayout();

        SourceInitialized += (_, _) => DwmNative.Apply(this);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
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

    private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowWelcome();

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
        SaveWorkspaceLayout();
        WorkspaceRoot.Visibility = Visibility.Collapsed;
        WelcomeRoot.Visibility = Visibility.Visible;
        WindowTitleText.Text = "Open Recent Project";
        RunCommandButton.Visibility = Visibility.Collapsed;
        OpenFolderTopButton.Content = "Open folder…";
        Title = "Helide — Open Recent Project";
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
        WindowTitleText.Text = projectName;
        RunCommandButton.Visibility = Visibility.Visible;
        OpenFolderTopButton.Content = "Open…";
        Title = $"Helide — {projectName}";
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

        try
        {
            SaveWorkspaceLayout();
            CloseWorkspace();

            _currentProject = project;
            _runCommand = ProjectDetector.DetectRunCommand(project);
            var projectName = new DirectoryInfo(project).Name;
            var branch = ProjectDetector.DetectGitBranch(project);

            ProjectStatus.Text = branch is null
                ? project
                : $"{project}  ·  {branch}";
            RunCommandButton.Content = _runCommand == "pwsh" ? "Focus runner" : $"Run  {_runCommand}";
            RunCommandButton.ToolTip = _runCommand;
            ResetPaneStates();

            ShowWorkspace();

            var failures = 0;
            if (CreateTerminal(GitSlot, GitPaneState, "lazygit", PowerShellCommand("lazygit"), project) is null)
                failures++;
            if (CreateTerminal(EditorSlot, EditorPaneState, "helix", PowerShellCommand("hx ."), project) is null)
                failures++;
            _runnerHost = CreateTerminal(
                RunnerSlot,
                RunnerPaneState,
                "runner",
                PowerShellCommand(
                    $"Write-Host {PowerShellLiteral($"Ready to run: {_runCommand}")} -ForegroundColor DarkCyan"),
                project);
            if (_runnerHost is null)
                failures++;
            if (CreateTerminal(AgentSlot, AgentPaneState, "opencode", PowerShellCommand("opencode"), project) is null)
                failures++;

            RunCommandButton.IsEnabled = _runnerHost is not null;

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
            UpdateWorkspaceHealth();
            return host;
        }
        catch (Exception exception)
        {
            slot.Content = CreateErrorPanel(label, exception);
            stateText.Text = "failed";
            stateText.Foreground = new SolidColorBrush(Color.FromRgb(243, 139, 168));
            UpdateWorkspaceHealth();
            return null;
        }
    }

    private void UpdatePaneState(TextBlock stateText, TerminalHostState state)
    {
        if (_closingWorkspace)
            return;

        stateText.Text = state switch
        {
            TerminalHostState.Starting => "starting",
            TerminalHostState.Ready => "ready",
            TerminalHostState.Failed => "attention",
            _ => "stopped",
        };
        stateText.Foreground = new SolidColorBrush(state switch
        {
            TerminalHostState.Ready => Color.FromRgb(166, 227, 161),
            TerminalHostState.Failed => Color.FromRgb(243, 139, 168),
            _ => Color.FromRgb(102, 103, 121),
        });
        UpdateWorkspaceHealth();
    }

    private void UpdateWorkspaceHealth()
    {
        if (_closingWorkspace)
            return;

        var ready = _terminalHosts.Count(host => host.State == TerminalHostState.Ready);
        WorkspaceHealthText.Text = _terminalHosts.Count == 4 && ready == 4
            ? "workspace ready"
            : $"{ready}/{Math.Max(_terminalHosts.Count, 4)} tools ready";
        WorkspaceHealthText.Foreground = new SolidColorBrush(
            ready == 4 ? Color.FromRgb(166, 227, 161) : Color.FromRgb(119, 120, 136));
    }

    private void ResetPaneStates()
    {
        foreach (var stateText in new[] { GitPaneState, EditorPaneState, RunnerPaneState, AgentPaneState })
        {
            stateText.Text = "starting";
            stateText.Foreground = new SolidColorBrush(Color.FromRgb(102, 103, 121));
        }
        WorkspaceHealthText.Text = "0/4 tools ready";
    }

    private static Border CreateErrorPanel(string label, Exception exception) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 46)),
        Child = new TextBlock
        {
            Margin = new Thickness(18),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 139, 168)),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 10,
            Text = $"{label} could not start.\n\n{exception.Message}",
        },
    };

    private void RunCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runnerHost is null)
            return;

        _runnerHost.FocusTerminal();
        if (_runCommand != "pwsh")
            _runnerHost.WriteLine(_runCommand);
    }

    private void MainWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
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

        if (WorkspaceRoot.Visibility != Visibility.Visible || number is < 1 or > 4)
            return;

        var host = number <= _terminalHosts.Count
            ? _terminalHosts[number - 1]
            : null;
        if (host is not null)
        {
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
        var layout = _state.Layout;
        LeftPaneColumn.Width = new GridLength(ClampRatio(layout.LeftRatio, 0.21), GridUnitType.Star);
        CenterPaneColumn.Width = new GridLength(ClampRatio(layout.CenterRatio, 0.58), GridUnitType.Star);
        RightPaneColumn.Width = new GridLength(ClampRatio(layout.RightRatio, 0.21), GridUnitType.Star);
        EditorPaneRow.Height = new GridLength(ClampRatio(layout.EditorRatio, 0.80), GridUnitType.Star);
        RunnerPaneRow.Height = new GridLength(ClampRatio(layout.RunnerRatio, 0.20), GridUnitType.Star);
    }

    private static double ClampRatio(double value, double fallback) =>
        double.IsFinite(value) && value > 0.02 ? value : fallback;

    private void SaveWorkspaceLayout()
    {
        var totalWidth = LeftPaneColumn.ActualWidth + CenterPaneColumn.ActualWidth + RightPaneColumn.ActualWidth;
        if (totalWidth > 0)
        {
            _state.Layout.LeftRatio = LeftPaneColumn.ActualWidth / totalWidth;
            _state.Layout.CenterRatio = CenterPaneColumn.ActualWidth / totalWidth;
            _state.Layout.RightRatio = RightPaneColumn.ActualWidth / totalWidth;
        }

        var totalHeight = EditorPaneRow.ActualHeight + RunnerPaneRow.ActualHeight;
        if (totalHeight > 0)
        {
            _state.Layout.EditorRatio = EditorPaneRow.ActualHeight / totalHeight;
            _state.Layout.RunnerRatio = RunnerPaneRow.ActualHeight / totalHeight;
        }
    }

    private void RestoreWindowGeometry()
    {
        var geometry = _state.Window;
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
        _state.Window.Left = bounds.Left;
        _state.Window.Top = bounds.Top;
        _state.Window.Width = bounds.Width;
        _state.Window.Height = bounds.Height;
        // Always reopen restored
        _state.Window.IsMaximized = false;
    }

    private void CloseWorkspace()
    {
        _closingWorkspace = true;
        try
        {
            foreach (var host in _terminalHosts.ToArray())
                host.Dispose();

            _terminalHosts.Clear();
            _runnerHost = null;
            GitSlot.Content = null;
            EditorSlot.Content = null;
            RunnerSlot.Content = null;
            AgentSlot.Content = null;
        }
        finally
        {
            _closingWorkspace = false;
        }
    }

    private static string PowerShellCommand(string command)
    {
        var escaped = command.Replace("`", "``").Replace("\"", "`\"");
        return $"pwsh.exe -NoLogo -NoExit -Command \"{escaped}\"";
    }

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''") + "'";

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SaveWorkspaceLayout();
        SaveWindowGeometry();
        _stateStore.Save(_state);
        CloseWorkspace();
    }

    private sealed record RecentProjectView(string Name, string Path, string Shortcut);
}
