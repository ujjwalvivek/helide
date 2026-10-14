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
        new("Search titles, prompts and replies", "conversation", "conversations", "open", KeepOrder: true);

    // Every conversation any agent has had in this project, newest first, so the one from
    // last week is found without remembering which agent it was in. Each reopens in its
    // own agent, in the folder it was started in; one already in a tab is switched to.
    private async void ShowPastConversations()
    {
        if (_currentProject is not { } project)
            return;

        // What was said in each is searched too. It is read ahead (WarmConversationText),
        // so this is usually only checking nothing changed.
        var conversations = await Task.Run(() => PastConversations.For(project)
            .Select(conversation => (Conversation: conversation, Said: ConversationText.Of(conversation)))
            .ToList());
        if (conversations.Count == 0)
        {
            ShowNotice("Open Past Conversation", "No agent has a conversation on record for this project.");
            return;
        }

        var now = DateTimeOffset.Now;
        var commands = conversations
            .Select(entry => new PaletteCommand(
                Named(entry.Conversation, entry.Said),
                () => OpenPastConversation(entry.Conversation),
                detail: AgentProviders.LabelFor(entry.Conversation.Agent) + " · " + (OpenAgentTab(entry.Conversation) is not null
                    ? "open"
                    : Helide.Usage.UsageFormat.Duration(now - entry.Conversation.LastUsed) + " ago"),
                keywords: entry.Conversation.Agent + "\n" + entry.Said,
                detailFor: query => Snippet(entry.Conversation, Named(entry.Conversation, entry.Said), entry.Said, query)))
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

    // Its title, or for a conversation still under the agent's placeholder ("New session
    // - 2026-10-06T22:51…"), the first thing asked in it.
    private static string Named(PastConversation conversation, string said)
    {
        var title = conversation.Title;
        if (title.Length > 0 && !title.StartsWith("New session", StringComparison.OrdinalIgnoreCase))
            return title;

        var first = said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is { Length: > 0 }
            ? first.Length > 80 ? first[..79] + "…" : first
            : title.Length > 0 ? title : "(untitled)";
    }

    // Where in what was said the search was found, when the title does not show it:
    // the words around the first hit, on one line.
    private static string? Snippet(PastConversation conversation, string title, string said, string query)
    {
        if (Helide.Shell.FuzzyMatch.Matches(title, query))
            return null;

        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(term => term.Length))
        {
            var at = said.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;

            const int Around = 24;
            var start = Math.Max(0, at - Around);
            var end = Math.Min(said.Length, at + term.Length + Around);
            var line = string.Join(' ', said[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            return AgentProviders.LabelFor(conversation.Agent) + " · "
                + (start > 0 ? "…" : string.Empty) + line + (end < said.Length ? "…" : string.Empty);
        }

        return null;
    }

    // Reads what was said in this project's conversations in the background, so the
    // search has it when the list is opened. Only conversations that changed since are
    // read again: after the first time, that is the one a turn just finished in.
    private string? _conversationsRead;

    private void WarmConversationText(bool force = false)
    {
        if (_currentProject is not { } project || (!force && _conversationsRead == project))
            return;

        _conversationsRead = project;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var conversation in PastConversations.For(project))
                    ConversationText.Of(conversation);
            }
            catch (Exception)
            {
                // Read when the list is opened instead.
            }
        });
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
