using Helide.Sessions;
using Helide.Shell;

namespace Helide;

// Reaching a Claude conversation that is running in the background.
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
}
