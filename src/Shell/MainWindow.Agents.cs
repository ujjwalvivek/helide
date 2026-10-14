using System.IO;
using Helide.Sessions;
using Helide.Shell;

namespace Helide;

// Reaching a conversation: one running in the background, or any past one.
public partial class MainWindow
{
    private static readonly PaletteMode BackgroundSessionsMode =
        new("Search background sessions", "session", "sessions", "attach");

    // One row per conversation running in this project. Choosing one opens a Claude tab
    // on it, and the id the tab is saved with is the conversation's own, so the next
    // launch attaches to it again rather than resuming a stub.
    private void AttachBackgroundSession()
    {
        if (_currentProject is not { } project)
            return;

        var commands = new List<PaletteCommand>();

        foreach (var job in ClaudeSessions.ListBackgroundJobs(project))
        {
            var target = job;
            commands.Add(new PaletteCommand(
                target.Name,
                () => StartAgentSession("claude", "Claude", target.SessionId, resuming: true),
                "Session",
                target.Status.Length > 0 ? target.Status : target.JobId));
        }

        if (commands.Count == 0)
        {
            ShowNotice("Attach Claude Background Session", "No Claude session is running in the background for this project.");
            return;
        }

        ShowPalette(commands, BackgroundSessionsMode);
    }

    private static readonly PaletteMode PastConversationsMode =
        new("Search conversations", "conversation", "conversations", "open");

    // Every conversation any agent has had in this project, newest first, so the one from
    // last week is found without remembering which agent it was in. Each reopens in its
    // own agent, in the folder it was started in; one already in a tab is switched to.
    private async void ShowPastConversations()
    {
        if (_currentProject is not { } project)
            return;

        var conversations = await Task.Run(() => PastConversations.For(project));
        if (conversations.Count == 0)
        {
            ShowNotice("Open Past Conversation", "No agent has a conversation on record for this project.");
            return;
        }

        var now = DateTimeOffset.Now;
        var commands = conversations
            .Select(conversation => new PaletteCommand(
                conversation.Title.Length > 0 ? conversation.Title : "(untitled)",
                () => OpenPastConversation(conversation),
                detail: AgentProviders.LabelFor(conversation.Agent) + " · " + (OpenAgentTab(conversation) is not null
                    ? "open"
                    : Helide.Usage.UsageFormat.Duration(now - conversation.LastUsed) + " ago"),
                keywords: conversation.Agent))
            .ToList();

        ShowPalette(commands, PastConversationsMode);
    }

    // ---- tab names -------------------------------------------------------------------

    // A tab is named after its conversation once the agent has written a title for it.
    // That happens after the first prompt -- or later, when the agent names it itself --
    // so a finished turn looks again, and so does the usage tick for any tab still
    // without one.
    private async void RefreshAgentTitle(AgentSessionView session)
    {
        if (session.SessionId is not { Length: > 0 } id)
            return;

        var title = await Task.Run(() => PastConversations.TitleOf(session.Type, id));
        if (title is not null && _agentSessions.Contains(session))
            session.Title = title;
    }

    private void RefreshUntitledAgents()
    {
        foreach (var session in _agentSessions.Where(session => session.Title is null).ToList())
            RefreshAgentTitle(session);
    }

    private AgentSessionView? OpenAgentTab(PastConversation conversation) =>
        _agentSessions.FirstOrDefault(session =>
            session.Type == conversation.Agent
            && string.Equals(session.SessionId, conversation.SessionId, StringComparison.OrdinalIgnoreCase));

    private void OpenPastConversation(PastConversation conversation)
    {
        if (OpenAgentTab(conversation) is { } open)
        {
            ActivateAgentSession(open);
            FocusPanel(ToolPanel.Agent, open.Host);
            return;
        }

        var directory = string.Equals(
            Path.GetFullPath(conversation.Directory).TrimEnd('\\'),
            Path.GetFullPath(_currentProject!).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : conversation.Directory;

        StartAgentSession(
            conversation.Agent,
            AgentProviders.LabelFor(conversation.Agent),
            conversation.SessionId,
            resuming: true,
            workingDirectory: directory);
    }
}
