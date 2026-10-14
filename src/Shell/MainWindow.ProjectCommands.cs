using System.IO;
using System.Windows;
using System.Windows.Controls;
using Helide.Persistence;
using Helide.Shell;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace Helide;

// Commands added for a project -- its tests, its dev server -- run by name, each in a
// terminal tab of its own. They are kept in the project's workspace state, not in the
// repo, and only typed into a shell, as you would type them.
public partial class MainWindow
{
    private static readonly PaletteMode ProjectCommandsMode =
        new("Run a project command", "command", "commands", "run", KeepOrder: true);

    private IReadOnlyList<ProjectCommandState> ProjectCommands => _workspace.Commands;

    // ---- the lists -------------------------------------------------------------------

    private void AddProjectCommands(List<PaletteCommand> commands, Func<bool> workspace)
    {
        foreach (var command in ProjectCommands.ToList())
            commands.Add(RunRow(command, "Run ", workspace));

        commands.Add(new PaletteCommand("Add Project Command…", () => AddProjectCommand(), "Run",
            keywords: "new script task", canInvoke: workspace));
        commands.Add(new PaletteCommand("Add Project Command From Terminal History…", ShowRecentTerminalCommands, "Run",
            keywords: "recent last history save", canInvoke: workspace));
        commands.Add(new PaletteCommand("Edit Project Commands…", ShowProjectCommandEditor, "Run",
            keywords: "rename remove delete folder change", canInvoke: () => workspace() && ProjectCommands.Count > 0));
    }

    private PaletteCommand RunRow(ProjectCommandState command, string prefix, Func<bool>? workspace = null) =>
        new(prefix + command.Name,
            () => RunProjectCommand(command),
            "Run",
            Where(command) is { Length: > 0 } folder ? $"{command.CommandLine}  ·  {folder}" : command.CommandLine,
            keywords: command.CommandLine,
            canInvoke: workspace);

    // The list on its own, from its shortcut: the commands, then the ways to add some.
    private void ShowProjectCommands()
    {
        if (_currentProject is null)
            return;

        var rows = ProjectCommands.Select(command => RunRow(command, string.Empty)).ToList();
        rows.Add(new PaletteCommand("Add Project Command…", () => AddProjectCommand(), "Add"));
        rows.Add(new PaletteCommand("Add From Terminal History…", ShowRecentTerminalCommands, "Add"));
        if (ProjectCommands.Count > 0)
            rows.Add(new PaletteCommand("Edit Project Commands…", ShowProjectCommandEditor, "Add"));

        ShowPalette(rows, ProjectCommandsMode);
    }

    // ---- running ---------------------------------------------------------------------

    // In the command's own tab. A tab still running it is only brought forward -- a dev
    // server keeps serving -- and one that has finished runs it again, typed at its
    // prompt.
    private void RunProjectCommand(ProjectCommandState command)
    {
        if (_currentProject is not { } project)
            return;

        var tab = _runnerSessions.FirstOrDefault(session => session.ProjectCommand == command.Name && session.Host is not null);
        if (tab is { Host: { } host })
        {
            ActivateRunnerSession(tab);
            FocusPanel(ToolPanel.Runner, host);
            if (!tab.IsBusy)
                host.WriteLine(command.CommandLine);
            return;
        }

        var folder = Path.GetFullPath(Path.Combine(project, command.Folder));
        if (!Directory.Exists(folder))
        {
            ShowNotice("Run Project Command", $"{command.Name} runs in {command.Folder}, which is not there any more. Edit the command to pick another folder.");
            return;
        }

        // A shell that runs the command and stays, so its prompt is there for the next
        // run, or for anything typed after it.
        var shell = ToolDefaults.RunnerShell();
        var commandLine = shell.IsPowerShell
            ? PowerShellCommand(shell.Executable, command.CommandLine)
            : $"{QuoteArgument(shell.Executable)} /K {command.CommandLine}";

        var before = _runnerSessions.Count;
        CreateRunnerSession("runner", command.Name, commandLine, folder);
        if (_runnerSessions.Count > before && _runnerSessions[^1] is { } created)
        {
            created.ProjectCommand = command.Name;
            FocusPanel(ToolPanel.Runner, created.Host);
        }
    }

    // ---- adding ----------------------------------------------------------------------

    private async void AddProjectCommand(string? commandLine = null)
    {
        // The folder is chosen from the card itself: its link goes to the picker and
        // comes back with what was typed kept.
        var folder = string.Empty;
        var typed = commandLine ?? string.Empty;
        while (true)
        {
            var asked = await ShowDialogAsync(new DialogRequest(
                "Add Project Command",
                "The command, as you would type it in the terminal.",
                "Next",
                "Cancel",
                InputPlaceholder: "dotnet test",
                InputText: typed,
                LinkLabel: "Runs in",
                LinkText: folder.Length == 0 ? "the project folder" : folder.Replace('\\', '/')));

            if (asked.Link)
            {
                typed = asked.Text ?? string.Empty;
                if (await PickFolderAsync() is { } picked)
                {
                    var relative = Path.GetRelativePath(_currentProject!, picked);
                    folder = relative == "." ? string.Empty : relative;
                }
                continue;
            }

            if (!asked.Accepted)
                return;

            commandLine = asked.Text?.Trim();
            break;
        }

        if (string.IsNullOrWhiteSpace(commandLine))
            return;

        var named = await ShowDialogAsync(new DialogRequest(
            "Add Project Command",
            $"A name to find it by.\n\n{commandLine}",
            "Add",
            "Cancel",
            InputPlaceholder: "name",
            InputText: commandLine));
        if (!named.Accepted)
            return;

        var name = UniqueName(string.IsNullOrWhiteSpace(named.Text) ? commandLine : named.Text.Trim());
        _workspace.Commands.Add(new ProjectCommandState { Name = name, CommandLine = commandLine.Trim(), Folder = folder });
        WriteWorkspace();
    }

    // What was typed into PowerShell lately, newest first, from the history PowerShell
    // keeps itself. Read only.
    private void ShowRecentTerminalCommands()
    {
        var recent = RecentShellCommands();
        if (recent.Count == 0)
        {
            ShowNotice(
                "Add From Terminal History",
                "No terminal history to add from. It comes from PowerShell's own history file, which cmd does not keep.");
            return;
        }

        var rows = recent
            .Select(line => new PaletteCommand(line, () => AddProjectCommand(line)))
            .ToList();
        ShowPalette(rows, new PaletteMode("Pick a command you ran", "command", "commands", "add", KeepOrder: true));
    }

    private static List<string> RecentShellCommands()
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt");

        try
        {
            if (!File.Exists(file))
                return [];

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n');

            // A line ending in a backtick goes on to the next; those are left out rather
            // than half-shown.
            return lines
                .Reverse()
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.EndsWith('`'))
                .Distinct(StringComparer.Ordinal)
                .Take(30)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    // ---- editing ---------------------------------------------------------------------

    private void ShowProjectCommandEditor()
    {
        var rows = ProjectCommands
            .Select(command => new PaletteCommand(command.Name, () => ShowCommandActions(command), detail: command.CommandLine))
            .ToList();
        ShowPalette(rows, new PaletteMode("Pick a command to change", "command", "commands", "edit", KeepOrder: true));
    }

    private void ShowCommandActions(ProjectCommandState command)
    {
        var folder = Where(command) is { Length: > 0 } where ? where : "the project folder";
        ShowPalette(
        [
            new PaletteCommand("Rename…", () => RenameCommand(command), command.Name, command.Name),
            new PaletteCommand("Change Command…", () => ChangeCommandLine(command), command.Name, command.CommandLine),
            new PaletteCommand("Change Folder…", () => ChangeCommandFolder(command), command.Name, folder),
            new PaletteCommand("Remove", () => RemoveCommand(command), command.Name),
        ], new PaletteMode("Change " + command.Name, "action", "actions", "do", KeepOrder: true));
    }

    private async void RenameCommand(ProjectCommandState command)
    {
        var outcome = await ShowDialogAsync(new DialogRequest(
            "Rename Project Command", command.CommandLine, "Rename", "Cancel", InputPlaceholder: "name", InputText: command.Name));
        if (!outcome.Accepted || string.IsNullOrWhiteSpace(outcome.Text) || outcome.Text.Trim() == command.Name)
            return;

        var name = UniqueName(outcome.Text.Trim());

        // Its open tab keeps belonging to it.
        foreach (var session in _runnerSessions.Where(session => session.ProjectCommand == command.Name))
            session.ProjectCommand = name;

        command.Name = name;
        WriteWorkspace();
    }

    private async void ChangeCommandLine(ProjectCommandState command)
    {
        var outcome = await ShowDialogAsync(new DialogRequest(
            "Change Project Command", command.Name, "Save", "Cancel", InputPlaceholder: "command", InputText: command.CommandLine));
        if (!outcome.Accepted || string.IsNullOrWhiteSpace(outcome.Text))
            return;

        command.CommandLine = outcome.Text.Trim();
        WriteWorkspace();
    }

    private void ChangeCommandFolder(ProjectCommandState command) =>
        OpenFolderPicker(folder =>
        {
            var relative = Path.GetRelativePath(_currentProject!, folder);
            command.Folder = relative == "." ? string.Empty : relative;
            WriteWorkspace();
        });

    private async void RemoveCommand(ProjectCommandState command)
    {
        if (!await ConfirmAsync("Remove Project Command", $"{command.Name}\n{command.CommandLine}", "Remove", danger: true))
            return;

        _workspace.Commands.Remove(command);
        WriteWorkspace();
    }

    private string UniqueName(string name)
    {
        var unique = name;
        for (var number = 2; ProjectCommands.Any(command => string.Equals(command.Name, unique, StringComparison.OrdinalIgnoreCase)); number++)
            unique = $"{name} {number}";

        return unique;
    }

    // The folder it runs in, for showing; empty for the project folder.
    private static string Where(ProjectCommandState command) => command.Folder.Replace('\\', '/');

    // ---- the status bar menu -----------------------------------------------------------

    private void ProjectCommandsButton_Click(object sender, RoutedEventArgs e)
    {
        ProjectCommandsButton.IsChecked = true;
        ProjectCommandsRows.Children.Clear();

        foreach (var command in ProjectCommands.ToList())
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = command.Name, VerticalAlignment = VerticalAlignment.Center });
            var line = new TextBlock
            {
                Text = "  " + command.CommandLine,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
            label.Children.Add(line);

            var target = command;
            var row = new Button { Style = (Style)FindResource("MenuRowStyle"), Content = label, ToolTip = command.CommandLine };
            row.Click += (_, _) =>
            {
                ProjectCommandsFlyout.IsOpen = false;
                RunProjectCommand(target);
            };
            ProjectCommandsRows.Children.Add(row);
        }

        if (ProjectCommands.Count == 0)
            ProjectCommandsRows.Children.Add(MenuNote("No commands for this project yet."));

        var rule = new Border { Height = 1, Margin = new Thickness(0, 3, 0, 3) };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        ProjectCommandsRows.Children.Add(rule);

        ProjectCommandsRows.Children.Add(MenuAction("Add command…", () => AddProjectCommand()));
        ProjectCommandsRows.Children.Add(MenuAction("Add from terminal history…", ShowRecentTerminalCommands));
        if (ProjectCommands.Count > 0)
            ProjectCommandsRows.Children.Add(MenuAction("Edit commands…", ShowProjectCommandEditor));

        ProjectCommandsFlyout.IsOpen = true;
    }

    private Button MenuAction(string text, Action act)
    {
        var row = new Button { Style = (Style)FindResource("MenuRowStyle"), Content = text };
        row.Click += (_, _) =>
        {
            ProjectCommandsFlyout.IsOpen = false;
            Dispatcher.BeginInvoke(act);
        };
        return row;
    }

    // The toggle is lit while its menu is open, as the panel toggles are while their
    // panel is.
    private void ProjectCommandsFlyout_Closed(object sender, EventArgs e) => ProjectCommandsButton.IsChecked = false;
}
