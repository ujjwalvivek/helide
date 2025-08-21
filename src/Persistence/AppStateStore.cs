using System.IO;
using System.Text.Json;

namespace Helide.Persistence;

internal sealed class AppStateStore
{
    private const int MaximumRecentProjects = 8;
    private readonly string _statePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    public AppStateStore()
    {
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Helide");
        _statePath = Path.Combine(stateDirectory, "state.json");
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
