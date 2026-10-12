using System.IO;
using System.Text;

namespace Helide;

/// <summary>
/// What Helide adds to the tools it hosts so they look like part of it on a machine
/// where nobody has configured them, and which shell the runner gets.
/// </summary>
/// <remarks>
/// Nothing here writes into a tool's own config directory. The files live under
/// Helide's local data and are handed over on the command line or through the
/// environment, so they apply to the panes Helide starts and to nothing else, and a
/// user's own settings always win over them.
/// </remarks>
internal static class ToolDefaults
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Helide");

    // ------------------------------------------------------------------ helix

    // Helix's built-in theme paints its own purple background over the pane. This one
    // keeps a stock palette and gives the background back to the terminal, which is
    // already drawing Helide's.
    private const string HelixTheme = """
        inherits = "catppuccin_mocha"

        "ui.background" = {}

        """;

    private const string HelixConfig = """
        theme = "helide"

        """;

    /// <summary>
    /// Arguments to put in front of a Helix launch, or none.
    /// </summary>
    /// <remarks>
    /// Only for a Helix that has no config of its own. <c>-c</c> replaces the user's
    /// config file rather than adding to it, so a user who has one keeps it untouched
    /// and gets whatever theme they chose.
    /// <para>
    /// A theme has to be found by name in a runtime directory, which is what
    /// <c>HELIX_RUNTIME</c> adds: Helix searches it alongside its own, so grammars and
    /// the stock themes still come from the install.
    /// </para>
    /// </remarks>
    public static string[] HelixArguments()
    {
        try
        {
            var userConfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "helix", "config.toml");

            if (File.Exists(userConfig))
                return [];

            // Somebody who points Helix at a runtime of their own has set it up, and
            // the variable holds one directory, not a list.
            var runtime = Path.Combine(Folder, "helix", "runtime");
            var existing = Environment.GetEnvironmentVariable("HELIX_RUNTIME");
            if (!string.IsNullOrWhiteSpace(existing)
                && !string.Equals(existing, runtime, StringComparison.OrdinalIgnoreCase))
                return [];

            var config = Path.Combine(Folder, "helix", "config.toml");
            WriteIfChanged(Path.Combine(runtime, "themes", "helide.toml"), HelixTheme);
            WriteIfChanged(config, HelixConfig);

            // Inherited by every pane started from here on.
            Environment.SetEnvironmentVariable("HELIX_RUNTIME", runtime);

            return ["-c", config];
        }
        catch (Exception)
        {
            // Helix with its own colours is still Helix.
            return [];
        }
    }

    // ---------------------------------------------------------------- lazygit

    // Square panels, like everything around them. lazygit rounds its borders by default.
    private const string LazygitOverlay = """
        gui:
          border: single

        """;

    /// <summary>
    /// Arguments to put after <c>lazygit.exe</c>, or none.
    /// </summary>
    /// <remarks>
    /// lazygit takes a list of config files and lets later ones override earlier ones,
    /// so Helide's goes first and the user's own after it: anything they set, borders
    /// included, stays theirs. Naming the list replaces the default lookup, which is
    /// why their file has to be named in it.
    /// </remarks>
    public static string[] LazygitArguments()
    {
        try
        {
            var overlay = Path.Combine(Folder, "lazygit.yml");
            WriteIfChanged(overlay, LazygitOverlay);

            var files = new List<string> { overlay };

            var chosen = Environment.GetEnvironmentVariable("LG_CONFIG_FILE");
            if (!string.IsNullOrWhiteSpace(chosen))
                files.Add(chosen);
            else if (LazygitUserConfig() is { } user)
                files.Add(user);

            return ["--use-config-file", string.Join(',', files)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? LazygitUserConfig()
    {
        string?[] directories =
        [
            Environment.GetEnvironmentVariable("CONFIG_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lazygit"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lazygit"),
        ];

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var file = Path.Combine(directory, "config.yml");
            if (File.Exists(file))
                return file;
        }

        return null;
    }

    // ------------------------------------------------------------------ shell

    /// <summary>The shell the runner pane starts, and the name its tab carries.</summary>
    /// <param name="Label">Shown on the tab.</param>
    /// <param name="Executable">What is launched.</param>
    /// <param name="IsPowerShell">False for cmd, which takes different arguments.</param>
    internal sealed record Shell(string Label, string Executable, bool IsPowerShell);

    /// <summary>
    /// PowerShell 7 when it is installed, then the Windows PowerShell every machine
    /// has, then cmd.
    /// </summary>
    /// <remarks>
    /// The runner used to name <c>pwsh.exe</c> outright, and on a machine without it the
    /// pane sat on "Starting runner…" for good. Looked up on every call rather than
    /// cached: it is a handful of file checks, and a shell installed while Helide is
    /// open should be picked up by the next tab.
    /// </remarks>
    public static Shell RunnerShell()
    {
        if (OnPath("pwsh.exe"))
            return new Shell("pwsh", "pwsh.exe", true);

        if (OnPath("powershell.exe"))
            return new Shell("powershell", "powershell.exe", true);

        return new Shell("cmd", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", false);
    }

    internal static bool OnPath(string file)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(entry.Trim().Trim('"'), file)))
                    return true;
            }
            catch (Exception)
            {
                // A malformed PATH entry is skipped, not fatal.
            }
        }

        return false;
    }

    // Only written when the content differs: several panes pass through here as a
    // workspace opens, and rewriting a file one of them is reading helps nobody.
    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
