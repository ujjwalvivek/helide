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
    public string LeftTool { get; set; } = "git";
}
