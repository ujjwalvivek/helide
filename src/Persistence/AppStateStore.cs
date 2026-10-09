using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Helide.Persistence;

internal sealed class AppStateStore
{
    private const int MaximumRecentProjects = 8;
    private readonly string _stateDirectory;
    private readonly string _statePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    public AppStateStore()
    {
        _stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Helide");
        _statePath = Path.Combine(_stateDirectory, "state.json");
    }

    public AppState Load()
    {
        try
        {
            if (!File.Exists(_statePath))
                return new AppState();

            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(_statePath), _jsonOptions)
                   ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    // The per-workspace file is named from a hash of the project's full path rather than
    // from the folder name. Names collide -- two projects called "api" in different
    // parents would share one file and silently clobber each other's sessions -- and
    // Windows treats file names case-insensitively, so "Helide" and "helide" would too.
    // A path mismatch merely orphans a file; a name collision loses data.
    public WorkspaceState LoadWorkspace(string projectPath)
    {
        try
        {
            var path = WorkspacePath(projectPath);
            if (!File.Exists(path))
                return new WorkspaceState();

            return JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(path), _jsonOptions)
                   ?? new WorkspaceState();
        }
        catch
        {
            return new WorkspaceState();
        }
    }

    public void SaveWorkspace(WorkspaceState workspace, string projectPath)
    {
        try
        {
            var path = WorkspacePath(projectPath);
            Directory.CreateDirectory(_stateDirectory);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(workspace, _jsonOptions));
            File.Move(temporaryPath, path, true);
        }
        catch
        {
            // Persistence failure must never make the workspace unusable.
        }
    }

    // Identifies a project across the app: the workspace file name and the single-instance
// guard both key off this, so they must agree. Names collide -- two projects called "api"
// in different parents -- and Windows compares file names case-insensitively, so a folder
// name is not usable as identity. A path mismatch merely orphans a file; a name
// collision loses a workspace. Null is the welcome screen, which has neither.
public static string ProjectKey(string? projectPath)
{
    if (string.IsNullOrWhiteSpace(projectPath))
        return "welcome";

    var normalized = Path.GetFullPath(projectPath).ToLowerInvariant();
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    return Convert.ToHexString(hash)[..8].ToLowerInvariant();
}

private string WorkspacePath(string projectPath) =>
        Path.Combine(_stateDirectory, $"state-ws-{ProjectKey(projectPath)}.json");

    public void RecordProject(AppState state, string projectPath)
    {
        var normalizedPath = Path.GetFullPath(projectPath);
        state.RecentProjects.RemoveAll(project =>
            string.Equals(project.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));
        state.RecentProjects.Insert(0, new RecentProjectState
        {
            Path = normalizedPath,
            Name = new DirectoryInfo(normalizedPath).Name,
            LastOpenedUtc = DateTime.UtcNow,
        });

        if (state.RecentProjects.Count > MaximumRecentProjects)
            state.RecentProjects.RemoveRange(
                MaximumRecentProjects,
                state.RecentProjects.Count - MaximumRecentProjects);

        state.LastProjectPath = normalizedPath;
        Save(state);
    }

    public void RemoveRecentProject(AppState state, string projectPath)
    {
        state.RecentProjects.RemoveAll(project =>
            string.Equals(project.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(state.LastProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
            state.LastProjectPath = state.RecentProjects.FirstOrDefault()?.Path;
        Save(state);
    }

    public void Save(AppState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _statePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, _jsonOptions));
            File.Move(temporaryPath, _statePath, true);
        }
        catch
        {
            // Persistence failure must never make the workspace unusable.
        }
    }
}
