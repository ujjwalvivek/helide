using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Helide.Sessions;
using Helide.Shell;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Helide;

// What an agent's last turn changed, as a line under it, and the files themselves a
// click away.
public partial class MainWindow
{
    private void LoadTurnEdits(AgentSessionView session)
    {
        if (session.SessionId is not { Length: > 0 } id || _currentProject is not { } project)
            return;

        var directory = session.WorkingDirectory ?? project;
        var type = session.Type;

        Task.Run(() => AgentTranscripts.LastTurnFiles(type, id, directory))
            .ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    // Only what is still there: a file the turn deleted has nothing to open.
                    session.TurnEdits = task.Result.Where(File.Exists).ToList();
                    UpdateAgentEditsStrip();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ClearTurnEdits(AgentSessionView session)
    {
        if (session.TurnEdits.Count == 0)
            return;

        session.TurnEdits = [];
        UpdateAgentEditsStrip();
    }

    private void UpdateAgentEditsStrip()
    {
        var files = _activeAgentSession?.TurnEdits ?? [];
        if (files.Count == 0)
        {
            AgentEditsStrip.Visibility = Visibility.Collapsed;
            AgentEditsFlyout.IsOpen = false;
            return;
        }

        var names = files.Take(2).Select(Path.GetFileName);
        var more = files.Count > 2 ? $" +{files.Count - 2}" : string.Empty;
        AgentEditsText.Text =
            $"{files.Count} {(files.Count == 1 ? "file" : "files")} changed · {string.Join(", ", names)}{more}";
        AgentEditsStrip.ToolTip = string.Join("\n", files.Select(file => RelativePath(file)));
        AgentEditsStrip.Visibility = Visibility.Visible;

        if (AgentEditsFlyout.IsOpen)
            RenderAgentEdits();
    }

    private void AgentEditsStrip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AgentEditsFlyout.IsOpen)
        {
            AgentEditsFlyout.IsOpen = false;
            return;
        }

        RenderAgentEdits();
        AgentEditsFlyout.IsOpen = true;
    }

    private void RenderAgentEdits()
    {
        // Above the line, flush with the pane's right edge.
        AgentEditsFlyout.CustomPopupPlacementCallback = (popup, target, _) =>
        [
            new CustomPopupPlacement(new System.Windows.Point(target.Width - popup.Width, -popup.Height), PopupPrimaryAxis.Horizontal),
        ];

        AgentEditsRows.Children.Clear();
        foreach (var file in _activeAgentSession?.TurnEdits ?? [])
            AgentEditsRows.Children.Add(EditRow(file));
    }

    // A file: its name and folder, opening it in an editor tab, with its history beside.
    private FrameworkElement EditRow(string file)
    {
        var open = new Button
        {
            Style = (Style)FindResource("MenuRowStyle"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            ToolTip = "Open in the editor",
        };

        var text = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock { Text = Path.GetFileName(file), VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var folder = new TextBlock
        {
            Text = "  " + (Path.GetDirectoryName(RelativePath(file))?.Replace('\\', '/') ?? string.Empty),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        folder.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        text.Children.Add(name);
        text.Children.Add(folder);
        open.Content = text;
        open.Click += (_, _) =>
        {
            AgentEditsFlyout.IsOpen = false;
            OpenEditorTab(file);
        };

        var history = new Button
        {
            Style = (Style)FindResource("PaneIconButtonStyle"),
            Height = 24,
            ToolTip = "File history in lazygit",
            Content = new GlyphIcon { Glyph = "" },
        };
        history.Click += (_, _) =>
        {
            AgentEditsFlyout.IsOpen = false;
            OpenFileHistory(file);
        };

        var row = new DockPanel();
        DockPanel.SetDock(history, Dock.Right);
        row.Children.Add(history);
        row.Children.Add(open);
        return row;
    }

    private string RelativePath(string file) =>
        _currentProject is { } project ? Path.GetRelativePath(project, file) : file;
}
