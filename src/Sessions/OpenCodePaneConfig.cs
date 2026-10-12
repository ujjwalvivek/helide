using System.IO;
using System.Text;
using Helide.Theme;

namespace Helide.Sessions;

/// <summary>
/// Builds the command line an OpenCode pane is launched with.
/// </summary>
/// <remarks>
/// The pane runs the user's own opencode against their own data directory, so its
/// conversations are the same ones a terminal or another client would see. What Helide
/// contributes is confined to view settings, and those are applied through the environment
/// so that no file the user owns is edited.
/// </remarks>
internal static class OpenCodePaneConfig
{
    /// <summary>
    /// The environment variable name OpenCode reads pane-local CLI config from.
    /// </summary>
    public const string ConfigContentVariable = "OPENCODE_CLI_CONFIG_CONTENT";

    // OpenCode's own rose pine, which ships with it and needs no file.
    private const string MochaTheme = "rosepine";

    // The same palette with no background of its own, so the pane shows Helide's. It is
    // not built in, so it is the one file Helide adds to the user's opencode folder.
    private const string OledTheme = "helide-oled";

    private const string OledThemeResource = "Helide.OpenCodeConfig.helide-oled.json";

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Helide");

    private static string UserDirectory(string variable, params string[] fallback)
    {
        var chosen = Environment.GetEnvironmentVariable(variable);
        var root = string.IsNullOrWhiteSpace(chosen)
            ? Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. fallback])
            : chosen;

        return Path.Combine(root, "opencode");
    }

    private static string UserConfigDirectory => UserDirectory("XDG_CONFIG_HOME", ".config");

    private static string UserStateDirectory => UserDirectory("XDG_STATE_HOME", ".local", "state");

    /// <summary>
    /// The overlay merged over the user's <c>cli.json</c>, for this process only.
    /// </summary>
    /// <remarks>
    /// OpenCode 2.x draws a tab strip of its own, which puts a second row of tabs inside a
    /// pane that already sits under Helide's tabs. It is switched off here rather than in
    /// the user's config, so every pane looks the same on every machine and nobody has to
    /// configure anything.
    /// <para>
    /// A theme is added only when the user has not picked one. The overlay wins over
    /// their config, so naming a theme here for someone who chose their own would take
    /// it away from them.
    /// </para>
    /// </remarks>
    private static string CliOverlay(string? theme) => theme is null
        ? "{\"tabs\":{\"mode\":\"off\"}}"
        : "{\"tabs\":{\"mode\":\"off\"},\"theme\":{\"name\":\"" + theme + "\",\"mode\":\"dark\"}}";

    /// <summary>
    /// The theme a pane opened now should get, or null to leave OpenCode to itself.
    /// </summary>
    /// <remarks>
    /// OpenCode's default theme paints a near-black background of its own, which sits
    /// in the pane as a box of the wrong colour. Rose pine goes with the default
    /// palette; the OLED palette gets the same colours with the background left out.
    /// <para>
    /// Decided when the pane is opened. A pane already running keeps what it started
    /// with, so switching Helide's theme reaches OpenCode panes opened after it.
    /// </para>
    /// </remarks>
    private static string? PaneTheme()
    {
        if (UserHasChosenTheme())
            return null;

        if (ThemePalette.ActiveTheme != ThemePalette.Oled)
            return MochaTheme;

        return EnsureOledTheme() ? OledTheme : null;
    }

    // Any mention of a theme in the user's opencode config or state counts, whichever
    // version wrote it and whatever shape it has. Erring this way costs a pane the
    // default look; erring the other way overrides something the user set.
    private static bool UserHasChosenTheme()
    {
        string[] files =
        [
            Path.Combine(UserConfigDirectory, "cli.json"),
            Path.Combine(UserConfigDirectory, "cli.jsonc"),
            Path.Combine(UserConfigDirectory, "tui.json"),
            Path.Combine(UserConfigDirectory, "tui.jsonc"),
            Path.Combine(UserConfigDirectory, "opencode.json"),
            Path.Combine(UserConfigDirectory, "opencode.jsonc"),
            Path.Combine(UserConfigDirectory, "config.json"),
            Path.Combine(UserStateDirectory, "kv.json"),
        ];

        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file) && File.ReadAllText(file).Contains("\"theme\"", StringComparison.Ordinal))
                    return true;
            }
            catch (Exception)
            {
                // A file that cannot be read cannot be ruled out.
                return true;
            }
        }

        return false;
    }

    // Added beside the user's own themes, because that folder is the only place OpenCode
    // looks for one by name: pointing it at a folder of Helide's replaces the user's
    // whole config directory rather than adding to it. An existing file of this name is
    // left exactly as it is.
    private static bool EnsureOledTheme()
    {
        try
        {
            var path = Path.Combine(UserConfigDirectory, "themes", OledTheme + ".json");
            if (File.Exists(path))
                return true;

            using var stream = typeof(OpenCodePaneConfig).Assembly.GetManifestResourceStream(OledThemeResource);
            if (stream is null)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            File.WriteAllText(path, reader.ReadToEnd(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The script for one overlay. Only the argument varies between panes, which is what
    /// lets a single file serve every pane without them interfering.
    /// </summary>
    /// <remarks>
    /// The first argument is the opencode to run and the second the session id. The
    /// opencode is one <see cref="OpenCodeVersion"/> picked, by full path, because the
    /// bare name finds whichever is first on PATH and that can be an old one. It goes
    /// through <c>call</c> since it may be a <c>.cmd</c> shim, which a script otherwise
    /// hands itself over to: npm installs a shim and other installers an <c>.exe</c>.
    /// <para>
    /// This is only ever run against OpenCode 2. Version 1 rejects an id it did not make
    /// itself, and the agent pane does not start it: see <see cref="OpenCodeVersion"/>.
    /// </para>
    /// </remarks>
    private static string Script(string? theme) => $"""
        @echo off
        set "{ConfigContentVariable}={CliOverlay(theme)}"
        if "%~2"=="" (call "%~1") else (call "%~1" -s %~2)

        """;

    /// <summary>
    /// Where a script lives: under Helide's own local data, one file per overlay.
    /// </summary>
    private static string ScriptPath(string? theme) => Path.Combine(
        Folder,
        theme is null ? "launch.cmd" : $"launch-{theme}.cmd");

    /// <summary>
    /// Builds the launch command for a pane, passing <paramref name="sessionId"/> as an
    /// argument rather than baking it into the script.
    /// </summary>
    /// <remarks>
    /// The overlay is delivered through a script rather than inline on the command line.
    /// An inline <c>cmd /c "set ..."</c> cannot carry it: the outer quotes make cmd
    /// re-parse the value, so the JSON's own quotes end the assignment, and the pane starts
    /// while silently ignoring what it was told. Inside a script file the same assignment is
    /// unambiguous, because cmd is parsing a file rather than a quoted string.
    /// <para>
    /// The session id deliberately does not go in the file. An earlier version named a
    /// script per command, which meant every conversation a pane was reopened onto left a
    /// new file behind forever, and panes restored together still raced to overwrite one
    /// shared file. Passing the id as an argument keeps the file identical for every pane
    /// on the same overlay: there is nothing to overwrite, and nothing to accumulate.
    /// There is one file per overlay, and there are three overlays.
    /// </para>
    /// <para>
    /// The id is one Helide chose, for a fresh pane as much as a restored one.
    /// <c>opencode -s</c> continues that session or, when there is none, creates it under
    /// that id in the directory the pane was started in.
    /// </para>
    /// </remarks>
    public static string BuildLaunchCommand(
        string? sessionId,
        Func<string, string> quoteArgument)
    {
        var theme = PaneTheme();
        EnsureScript(theme);

        var command = quoteArgument(ScriptPath(theme))
            + " " + quoteArgument(OpenCodeVersion.Executable());

        if (sessionId is { Length: > 0 } id)
            command += " " + quoteArgument(id);

        return command;
    }

    /// <summary>
    /// Puts the script on disk if it is missing or out of date.
    /// </summary>
    /// <remarks>
    /// Checked on every launch so a changed overlay reaches panes opened after an upgrade,
    /// but only written when the content differs. Panes restored together all pass through
    /// here while the first of them is already reading the file, and rewriting it each time
    /// would let one of them catch it half-written. When it does have to be written it is
    /// moved into place whole, for the same reason.
    /// </remarks>
    private static void EnsureScript(string? theme)
    {
        var path = ScriptPath(theme);
        var script = Script(theme);

        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == script)
                return;
        }
        catch (IOException)
        {
            // Unreadable is treated as out of date.
        }

        // Helide's local data folder does not exist until something is saved to it, and
        // on a first run this is reached before anything has been.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, script, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
