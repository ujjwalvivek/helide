namespace Helide.Persistence;

internal sealed class AppState
{
    public int Version { get; set; } = 1;
    public string? LastProjectPath { get; set; }
    public List<RecentProjectState> RecentProjects { get; set; } = [];
    public WindowGeometryState Window { get; set; } = new();
    public WorkspaceLayoutState Layout { get; set; } = new();
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

internal sealed class WorkspaceLayoutState
{
    public double LeftRatio { get; set; } = 0.21;
    public double CenterRatio { get; set; } = 0.58;
    public double RightRatio { get; set; } = 0.21;
    public double EditorRatio { get; set; } = 0.80;
    public double RunnerRatio { get; set; } = 0.20;
}
