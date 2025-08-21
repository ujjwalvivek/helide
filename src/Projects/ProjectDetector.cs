using System.IO;

namespace Helide.Projects;

internal static class ProjectDetector
{
    public static string DetectRunCommand(string projectPath)
    {
        if (File.Exists(Path.Combine(projectPath, "package.json")))
            return "npm run dev";
        if (File.Exists(Path.Combine(projectPath, "Cargo.toml")))
            return "cargo run";
        if (File.Exists(Path.Combine(projectPath, "index.html")))
            return "python -m http.server 8000";
        return "pwsh";
    }

    public static string? DetectGitBranch(string projectPath)
    {
        try
        {
            var gitPath = Path.Combine(projectPath, ".git");
            var headPath = Path.Combine(gitPath, "HEAD");

            if (File.Exists(gitPath))
            {
                var pointer = File.ReadAllText(gitPath).Trim();
                const string prefix = "gitdir:";
                if (pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var target = pointer[prefix.Length..].Trim();
                    var gitDirectory = Path.GetFullPath(Path.Combine(projectPath, target));
                    headPath = Path.Combine(gitDirectory, "HEAD");
                }
            }

            if (!File.Exists(headPath))
                return null;

            var head = File.ReadAllText(headPath).Trim();
            const string branchPrefix = "ref: refs/heads/";
            return head.StartsWith(branchPrefix, StringComparison.Ordinal)
                ? head[branchPrefix.Length..]
                : head.Length >= 8 ? head[..8] : head;
        }
        catch
        {
            return null;
        }
    }
}
