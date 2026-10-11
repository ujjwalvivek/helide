using System.IO;

namespace Helide.Sessions;

/// <summary>
/// Owns the on-disk state of an OpenCode pane: the private data directory that gives it
/// its own session list, and the credentials copied into it so the agent can still
/// authenticate.
/// </summary>
/// <remarks>
/// OpenCode 2.x keeps every session in one database under the data directory and has the
/// TUI list all of them for the current project. With panes sharing that directory, each
/// pane opened with every other pane's conversations already in its tab strip, and a pane
/// had no way to tell which of them was its own. Giving each pane its own data directory
/// gives it its own database, so its sessions are its own and nothing else's.
/// <para>
/// The config directory is deliberately <i>not</i> redirected: themes, keybinds, agents,
/// MCP servers and the CLI settings stay shared, so panes look and behave the same as the
/// user's own opencode.
/// </para>
/// </remarks>
internal static class OpenCodePaneStore
{
    /// <summary>
    /// Environment variables that move the data and state directories. Both are needed:
    /// the database lives in the data directory and the service record in the state one,
    /// and a pane must own both or it will still attach to the shared service.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(string paneDirectory)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["XDG_DATA_HOME"] = paneDirectory,
            ["XDG_STATE_HOME"] = paneDirectory,
        };
    }

    /// <summary>
    /// Creates the pane's data directory and seeds it with the user's credentials.
    /// </summary>
    /// <remarks>
    /// Credentials are resolved from the same XDG variable as the database, so an isolated
    /// directory starts with an empty auth.json and the pane cannot sign in. They are
    /// copied rather than linked because the store writes credentials by replacing the
    /// file, which silently detaches a hard link and would leave panes quietly
    /// unauthenticated after a `/connect`.
    /// <para>
    /// The copy is refreshed on every launch rather than once, so a pane started after new
    /// credentials are added elsewhere still works. It cannot be shared afterwards, so a
    /// `/connect` inside one pane is not seen by the others until they are reopened.
    /// </para>
    /// </remarks>
    public static void Prepare(string paneDirectory, string sourceDataDirectory)
    {
        Directory.CreateDirectory(Path.Combine(paneDirectory, "opencode"));

        var source = Path.Combine(sourceDataDirectory, "opencode", "auth.json");
        var target = Path.Combine(paneDirectory, "opencode", "auth.json");

        try
        {
            if (!File.Exists(source))
                return;

            File.Copy(source, target, overwrite: true);
        }
        catch (Exception)
        {
            // A pane that cannot be given credentials still opens; it simply reports no
            // usable provider, which is a better failure than refusing to start.
        }
    }

    /// <summary>
    /// The pane's database path.
    /// </summary>
    public static string DatabasePath(string paneDirectory)
        => Path.Combine(paneDirectory, "opencode", "opencode.db");

    /// <summary>
    /// Where pane directories live. Kept under Helide's own local data so they are
    /// removed with the app rather than left inside the user's opencode tree.
    /// </summary>
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Helide", "opencode-panes");

    /// <summary>
    /// A stable directory name for a pane, so the same pane finds its own database again
    /// after Helide restarts.
    /// </summary>
    public static string DirectoryFor(string paneId)
        => Path.Combine(RootDirectory, paneId);

    /// <summary>
    /// A fresh identifier for a pane's directory.
    /// </summary>
    /// <remarks>
    /// Restored panes reuse the id they were saved with rather than being given a new one,
    /// which is what lets a reopened pane find the database holding its conversation.
    /// </remarks>
    public static string NewPaneId() => Guid.NewGuid().ToString("n");

    /// <summary>
    /// The user's own OpenCode data directory, the source of the credentials a pane needs.
    /// </summary>
    public static string UserDataDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share")
                : xdg;
        }
    }

    /// <summary>
    /// Writes the small batch file a pane is started through, and returns its path.
    /// </summary>
    /// <remarks>
    /// The script exists because cmd.exe cannot be given these variables inline. Wrapping
    /// the assignments in the quotes that <c>cmd /c "..."</c> requires makes cmd re-parse
    /// the value, so the quotes inside the overlay terminate the assignment early; the pane
    /// then starts normally, attached to the user's shared sessions, with nothing to say
    /// so. A script file is parsed as a file, where the same assignment is unambiguous.
    /// </remarks>
    public static string WriteLauncher(string paneDirectory, string script)
    {
        Directory.CreateDirectory(paneDirectory);

        var path = Path.Combine(paneDirectory, "launch.cmd");
        File.WriteAllText(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }

    /// <summary>
    /// Removes a pane's directory. Called when a pane is closed for good rather than at
    /// application exit, so its conversations are kept exactly as long as the tab is.
    /// </summary>
    public static void Discard(string paneId)
    {
        try
        {
            var directory = DirectoryFor(paneId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // Leftover state is harmless; it is reclaimed the next time the pane is opened.
        }
    }
}