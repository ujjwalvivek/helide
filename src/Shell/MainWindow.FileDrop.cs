using System.IO;
using System.Windows;
using System.Windows.Media;
using Helide.Shell;
using Helide.Terminal;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace Helide;

// Files dragged in from outside: dropped on an agent, they go into its input as
// mentions, the same as the mention shortcut, and nothing is sent.
public partial class MainWindow
{
    private void AgentSlot_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPaths(e).Length > 0 && AgentUnder(e) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void AgentSlot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var paths = DroppedPaths(e);
        if (paths.Length == 0 || AgentUnder(e) is not { Host: { } host } session)
            return;

        // Relative to where the agent runs, as a mention is; anything outside that folder
        // keeps its full path. A path with a space in it is quoted, or the agent would
        // read it as two words.
        var root = session.WorkingDirectory ?? _currentProject!;
        var mentions = paths.Select(path =>
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                relative = path;

            relative = relative.Replace('\\', '/');
            return "@" + (relative.Contains(' ') ? $"\"{relative}\"" : relative);
        });

        ActivateAgentSession(session);
        if (host.PasteText(string.Join(" ", mentions) + " "))
            FocusPanel(ToolPanel.Agent, host);
    }

    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths
            : [];

    // The agent tab whose pane the pointer is over, split panes included.
    private AgentSessionView? AgentUnder(DragEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is NativeTerminalHost host)
                return _agentSessions.FirstOrDefault(session => ReferenceEquals(session.Host, host));
        }

        return _activeAgentSession;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
