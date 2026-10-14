using System.Windows;
using System.Windows.Controls;
using Helide.Sessions;
using Helide.Shell;
using Button = System.Windows.Controls.Button;

namespace Helide;

// Handing one agent's answer to another: the selected tab's last reply, word for word,
// pasted into the input of the tab chosen. Never sent: what to ask about it is yours.
public partial class MainWindow
{
    private void AgentReplyButton_Click(object sender, RoutedEventArgs e)
    {
        AgentReplyRows.Children.Clear();

        var others = _agentSessions.Where(session => !ReferenceEquals(session, _activeAgentSession)).ToList();
        foreach (var target in others)
        {
            // Two tabs of one agent are told apart by their place in the strip.
            var label = target.Name;
            if (others.Count(session => session.Name == target.Name) > 1)
                label += " " + (_agentSessions.Where(session => session.Name == target.Name).ToList().IndexOf(target) + 1);

            var row = new Button { Style = (Style)FindResource("MenuRowStyle"), Content = label };
            row.Click += (_, _) => SendLastReply(target);
            AgentReplyRows.Children.Add(row);
        }

        if (others.Count == 0)
            AgentReplyRows.Children.Add(MenuNote("No other agent tab open."));

        AgentReplyFlyout.IsOpen = true;
    }

    private TextBlock MenuNote(string text)
    {
        var note = new TextBlock { Text = text, Margin = new Thickness(10, 4, 10, 4) };
        note.SetResourceReference(TextBlock.FontFamilyProperty, "UiMonoFontFamily");
        note.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeSmall");
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        return note;
    }

    private async void SendLastReply(AgentSessionView target)
    {
        AgentReplyFlyout.IsOpen = false;

        if (_activeAgentSession is not { } source)
            return;

        var reply = source.SessionId is { Length: > 0 } id
            ? await Task.Run(() => AgentTranscripts.LastReply(source.Type, id))
            : null;

        if (reply is null)
        {
            ShowNotice("Send Last Reply", $"{source.TypeLabel} has no reply on record in this tab yet.");
            return;
        }

        ActivateAgentSession(target);
        if (target.Host?.PasteText(reply) == true)
            FocusPanel(ToolPanel.Agent, target.Host);
        else
            ShowNotice("Send Last Reply", $"{target.Name} is not ready for input yet. Try again once it has started.");
    }
}
