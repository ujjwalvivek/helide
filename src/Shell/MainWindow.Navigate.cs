using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Helide.Sessions;
using Helide.Shell;
using Helide.Terminal;

namespace Helide;

// Getting from one panel to a place in another: an error to the line it names, and the
// file in the editor to the project browser, to its history, to the agent.
public partial class MainWindow
{
    private static readonly PaletteMode LocationsMode = new("Search locations", "location", "locations", "open");

    // How the project browser is reached from outside: yazi answers `ya emit-to` for the
    // id it was started with. One per window, so two windows never reveal in each other.
    private readonly long YaziClientId = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000);

    // ---- errors to lines -------------------------------------------------------------

    /// <summary>
    /// Opens the file and line that output points at: the selection in the pane you are
    /// in, else the agent's last reply when you are in the agent, else what the last
    /// terminal command printed. Several in one place are offered as a list.
    /// </summary>
    private void OpenErrorLocation()
    {
        if (_currentProject is not { } project)
            return;

        foreach (var (text, directory) in ErrorSources())
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var locations = SourceLocations.Find(text, [directory ?? project, project]);
            if (locations.Count == 0)
                continue;

            if (locations.Count == 1)
            {
                OpenEditorAt(locations[0]);
                return;
            }

            ShowPalette(
                locations.Select(location => new PaletteCommand(
                        $"{Path.GetFileName(location.Path)}:{location.Line}" + (location.Column is { } col ? $":{col}" : string.Empty),
                        () => OpenEditorAt(location),
                        RelativeDirectory(location.Path, project),
                        keywords: location.Context))
                    .ToList(),
                LocationsMode);
            return;
        }

        ShowNotice(
            "Open Error Location",
            "No file location found in the selection, the agent's last reply, or the last terminal command's output.");
    }

    // Where to look, most specific first: what you selected, then the output nearest to
    // where the keyboard is.
    private IEnumerable<(string? Text, string? Directory)> ErrorSources()
    {
        var focused = _terminalHosts.FirstOrDefault(host => host.HasKeyboardFocus());
        if (focused?.SelectedText() is { } selected)
            yield return (selected, null);

        var agent = _activeAgentSession;
        var agentReply = agent is { SessionId: { Length: > 0 } id }
            ? (Func<string?>)(() => AgentTranscripts.LastReply(agent.Type, id))
            : () => null;
        var shell = _activeRunnerSession?.Host;

        if (AgentHasKeyboard())
        {
            yield return (agentReply(), agent?.WorkingDirectory);
            yield return (shell?.LastCommandOutput(), _activeRunnerSession?.WorkingDirectory);
        }
        else
        {
            yield return (shell?.SelectedText() ?? shell?.LastCommandOutput(), _activeRunnerSession?.WorkingDirectory);
            yield return (agentReply(), agent?.WorkingDirectory);
        }
    }

    private void OpenEditorAt(SourceLocation location)
    {
        var position = location.Line + (location.Column is { } column ? ":" + column : string.Empty);

        // Already open: Helix is told to go there. Helix's own :goto takes a line only.
        foreach (var tab in _editorTabs)
        {
            if (string.Equals(tab.Path, location.Path, StringComparison.OrdinalIgnoreCase))
            {
                ActivateEditorTab(tab);
                tab.Host.RunEditorCommand($":goto {location.Line}");
                FocusPanel(null, tab.Host);
                return;
            }
        }

        // A new tab is started on the spot, as `hx file:line:col`. The opening picker tab
        // gives way to it, as it does to any first file.
        if (_editorTabs.Count == 1 && _editorTabs[0].Path is null)
            CloseEditorTab(_editorTabs[0], refill: false);

        if (CreateEditorTab(location.Path, position: position) is { } opened)
            FocusPanel(null, opened.Host);
    }

    private static string RelativeDirectory(string path, string project)
    {
        var directory = Path.GetDirectoryName(Path.GetRelativePath(project, path));
        return string.IsNullOrEmpty(directory) ? "." : directory.Replace('\\', '/');
    }

    // ---- the file in the editor, elsewhere ------------------------------------------

    private void UpdateEditorFileActions()
    {
        var hasFile = _activeEditorTab?.Path is not null;
        EditorRevealButton.IsEnabled = hasFile;
        EditorHistoryButton.IsEnabled = hasFile;
        EditorMentionButton.IsEnabled = hasFile && _activeAgentSession is not null;
        EditorMentionButton.Visibility = _state.AiEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditorRevealButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEditorTab?.Path is { } path)
            RevealInProjectBrowser(path);
    }

    private void EditorHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeEditorTab?.Path is { } path)
            OpenFileHistory(path);
    }

    private void EditorMentionButton_Click(object sender, RoutedEventArgs e) => MentionFileInAgent();

    /// <summary>Shows the project browser and selects the file in it.</summary>
    /// <remarks>
    /// Yazi is asked through its own channel, <c>ya emit-to</c>, with the id Helide
    /// started it under. A browser that is only just starting does not answer yet, so
    /// the request is repeated for a moment.
    /// </remarks>
    private async void RevealInProjectBrowser(string path)
    {
        if (IsPanelCollapsed(ToolPanel.Left) || _activeLeftTool != LeftTool.Project)
            ToggleLeftTool(LeftTool.Project);

        FocusWhenReady(ActiveLeftHost());

        var id = YaziClientId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (await RunYa("emit-to", id, "reveal", path))
                return;

            await Task.Delay(200);
        }
    }

    private static Task<bool> RunYa(params string[] arguments) => Task.Run(() =>
    {
        try
        {
            var start = new ProcessStartInfo("ya.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null)
                return false;

            if (!process.WaitForExit(3000))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>
    /// Opens the file's history in lazygit, in a terminal tab of its own.
    /// </summary>
    /// <remarks>
    /// Lazygit only narrows to one file when it is started that way (<c>--filter</c>), so
    /// this is a second lazygit beside the one in the left panel, not a change to it.
    /// Closing the tab is closing the history. Full-screen mode gives the commit list the
    /// whole pane: the terminal panel is short, and lazygit's normal layout will not draw
    /// in it at all.
    /// </remarks>
    private void OpenFileHistory(string path)
    {
        if (_currentProject is not { } project)
            return;

        var lazygit = ToolLocator.Tools.First(candidate => candidate.Label == "lazygit");
        if (!ToolLocator.IsInstalled(lazygit))
        {
            ShowNotice("File History", "lazygit is not installed.");
            return;
        }

        var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
        var before = _runnerSessions.Count;
        CreateRunnerSession(
            "lazygit",
            Path.GetFileName(path),
            ToolCommand("lazygit.exe", [.. ToolDefaults.LazygitArguments(), "--filter", relative, "--screen-mode", "full"]),
            project);

        if (_runnerSessions.Count <= before)
            return;

        FocusPanel(ToolPanel.Runner, _activeRunnerSession?.Host);

        // Lazygit will not draw at all below a dozen or so rows, which is shorter than
        // the terminal panel usually sits. It is given room the way a drag would, and
        // keeps it until dragged back.
        var wanted = Math.Max(320, ActualHeight * 0.4);
        if (RunnerPane.ActualHeight < wanted)
        {
            RunnerPaneRow.Height = new GridLength(wanted);
            _runnerPanePixels = wanted;
            SaveWorkspaceLayout();
        }
    }

    // ---- palette -----------------------------------------------------------------------

    private void AddNavigationCommands(List<PaletteCommand> commands, Func<bool> workspace)
    {
        commands.Add(new PaletteCommand(
            "Open Error Location",
            OpenErrorLocation,
            "Go",
            Shortcut(Helide.Keys.HelideKeymap.ErrorLocation),
            keywords: "jump line file compiler stack trace",
            canInvoke: workspace));

        commands.Add(new PaletteCommand(
            "Show File in Project Browser",
            () => { if (_activeEditorTab?.Path is { } path) RevealInProjectBrowser(path); },
            "Go",
            keywords: "yazi reveal locate",
            canInvoke: () => workspace() && _activeEditorTab?.Path is not null));

        commands.Add(new PaletteCommand(
            "File History in Lazygit",
            () => { if (_activeEditorTab?.Path is { } path) OpenFileHistory(path); },
            "Go",
            keywords: "git log blame commits filter",
            canInvoke: () => workspace() && _activeEditorTab?.Path is not null));
    }
}
