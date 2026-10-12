namespace Helide.Persistence;

/// <summary>
/// State that belongs to the application rather than to any one project: the theme and
/// the recent-project list.
/// </summary>
/// <remarks>
/// Deliberately small. Everything describing a workspace -- layout, window geometry,
/// editor tabs, agent sessions -- lives in <see cref="WorkspaceState"/> and is stored in
/// a file of its own keyed by project path, so two projects open at the same time cannot
/// overwrite each other's sessions. This file is the only one they still share, and the
/// only thing that can be lost by two windows writing it at once is one recent-project
/// entry.
/// </remarks>
internal sealed class AppState
{
    public int Version { get; set; } = 1;
    public string? LastProjectPath { get; set; }
    public List<RecentProjectState> RecentProjects { get; set; } = [];
    public string Theme { get; set; } = "mocha";  // "mocha" or "oled"

    // Whether the yazi config that routes file opens into Helide's editor is installed.
    // Only a mirror of what is on disk -- yazi reads its config once at startup, so the
    // script's presence is the fact that decides it -- and is kept only so the palette
    // can label the command without touching the file system to find out.
    public bool YaziOpenInHelide { get; set; }

    // Where the last window was and whether it was maximised. Each workspace keeps its
    // own, and this is what a window uses when it has none: a project opened for the
    // first time, or a window that starts on the welcome screen.
    public WindowGeometryState Window { get; set; } = new();

    // Set just before the restart that follows installing the yazi config, and cleared
    // by the window that reads it: that window opens with the project browser focused.
    public bool FocusProjectBrowserOnStart { get; set; }

    // Whether the agent pane exists at all. Off until the user turns it on from the
    // welcome screen: Helide is an editor shell first, and no agent is required to use
    // it. While it is off no agent is started, and the saved sessions of each workspace
    // are left exactly as they were so turning it back on brings them back.
    public bool AiEnabled { get; set; }
}

/// <summary>Everything describing one project's workspace, persisted per project path.</summary>
internal sealed class WorkspaceState
{
    public WindowGeometryState Window { get; set; } = new();
    public WorkspaceLayoutState Layout { get; set; } = new();
    public List<AgentSessionState> AgentSessions { get; set; } = [];

    // Which agent tab was selected when the window closed. Every new session is made
    // active on creation, so without this it is always the last one.
    public int ActiveAgentIndex { get; set; } = -1;

    public List<EditorTabState> EditorTabs { get; set; } = [];
}

internal sealed class EditorTabState
{
    // null is Helix's file picker, which is what the pane opens on with no file.
    public string? Path { get; set; }
}

internal sealed class RecentProjectState
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime LastOpenedUtc { get; set; }
}

internal sealed class WindowGeometryState
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

internal sealed class AgentSessionState
{
    public string Type { get; set; } = "opencode";  // "opencode" or "codex"
    public string Name { get; set; } = string.Empty;
    public string CommandLine { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }

    // The conversation to reopen. Without this the pane comes back empty, which is
    // the whole thing this field exists to prevent.
    public string? SessionId { get; set; }

    // Only set for a pane opened somewhere other than the project root. An OpenCode
    // conversation is filed under the directory it was started in, so the pane has to
    // be reopened there to find it.
    public string? WorkingDirectory { get; set; }
}

internal sealed class WorkspaceLayoutState
{
    public double LeftRatio { get; set; } = 0.21;
    public double CenterRatio { get; set; } = 0.58;
    public double RightRatio { get; set; } = 0.21;
    public double EditorRatio { get; set; } = 0.80;
    public double RunnerRatio { get; set; } = 0.20;
    public double LeftPixels { get; set; }
    public double CenterPixels { get; set; }
    public double RightPixels { get; set; }
    public double RunnerPixels { get; set; }
    public bool LeftCollapsed { get; set; }
    public bool RunnerCollapsed { get; set; } = true;
    public bool AgentCollapsed { get; set; } = true;

    // Set when the agent pane was open and got closed only because AI was switched off.
    // AgentCollapsed has to read true while there is no pane, and without this the pane
    // would stay closed after AI is switched back on, as if the user had closed it.
    public bool AgentHiddenByAi { get; set; }
    public string LeftTool { get; set; } = "git";

    // Zen mode: every panel closed, with the title bar and the editor's tab strip. The
    // three flags are which panels were open when it was switched on, so switching it
    // off puts back the arrangement it replaced -- after a restart as much as before.
    public bool Zen { get; set; }
    public bool ZenLeftWasOpen { get; set; }
    public bool ZenRunnerWasOpen { get; set; }
    public bool ZenAgentWasOpen { get; set; }

    // Zen mode fills the screen. This is whether the window already did, so leaving
    // zen mode gives back the size it took rather than staying maximised.
    public bool ZenWasMaximized { get; set; }
}
