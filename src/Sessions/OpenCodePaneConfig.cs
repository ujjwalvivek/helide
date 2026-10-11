namespace Helide.Sessions;

/// <summary>
/// Builds the command line an OpenCode pane is launched with: the per-pane data
/// directories that give it its own sessions, plus Helide's own view settings.
/// </summary>
/// <remarks>
/// OpenCode 2.x keeps every session in one database and has the TUI list all of them for
/// the current project, so panes sharing a data directory also share a tab strip, and a
/// pane cannot tell which conversations are its own. Redirecting the data directory per
/// pane fixes that at the source rather than filtering the symptoms afterwards.
/// <para>
/// Two things are set here, both only for the pane's own process. The data and state
/// directories move, which is what isolates it; the CLI overlay switches off the tab
/// strip, which would otherwise sit inside a pane that already has Helide's own tabs above
/// it. Neither touches a file the user owns: the config directory stays shared, and the
/// user's <c>cli.json</c> is read as usual with the overlay merged over it.
/// </para>
/// </remarks>
internal static class OpenCodePaneConfig
{
    /// <summary>
    /// The overlay applied to every OpenCode pane. Only settings Helide actually needs
    /// belong here -- this is merged into the user's config, so a value listed twice is a
    /// value the user can no longer change for themselves.
    /// </summary>
    private const string Overlay = "{\"tabs\":{\"mode\":\"off\"}}";

    /// <summary>
    /// The environment variable name OpenCode reads pane-local CLI config from.
    /// </summary>
    public const string ConfigContentVariable = "OPENCODE_CLI_CONFIG_CONTENT";

    /// <summary>
    /// The environment variable name OpenCode reads pane-local server config from.
    /// </summary>
    /// <remarks>
    /// Separate from the CLI overlay above because the two are read by different halves of
    /// the agent. View settings such as the tab strip belong to the TUI; the default model
    /// belongs to the server, and is only accepted here.
    /// </remarks>
    public const string ServerConfigContentVariable = "OPENCODE_CONFIG_CONTENT";

    /// <summary>
    /// The model a pane starts on when the user has not expressed a preference.
    /// </summary>
    /// <remarks>
    /// A pane with its own data directory has none of the user's stored preferences, so
    /// OpenCode falls back to a paid default it will then fail to authenticate against.
    /// Naming a free model here means a new pane is usable immediately, while a pane
    /// resumed onto an existing conversation keeps that conversation's own model.
    /// </remarks>
    private const string DefaultModel = "opencode/space-bunny-free";

    /// <summary>
    /// Builds the launch command for a pane rooted at <paramref name="paneDirectory"/>.
    /// </summary>
    /// <remarks>
    /// The terminal control starts a process from a single command-line string and offers
    /// no way to hand a child its own environment, so the variables are set through the
    /// shell instead. <c>cmd.exe /c</c> is used rather than the bare program because the
    /// agent is a <c>.cmd</c> shim, which only a command interpreter can execute directly.
    /// Assignments are chained with <c>&amp;&amp;</c> so a failed set cannot leave the pane
    /// running while silently sharing the user's sessions.
    /// <para>
    /// The variables are set from a small generated script rather than inline on the
    /// command line. An inline <c>cmd /c "set ..."</c> cannot carry the overlay: the outer
    /// quotes make cmd re-parse the value, so the JSON's own quotes end the assignment and
    /// the pane silently runs against the user's shared sessions with no error anywhere.
    /// Inside a script file the same assignment is unambiguous, because cmd is parsing a
    /// file rather than a quoted string.
    /// </para>
    /// </remarks>
    public static string BuildLaunchCommand(
        string executable,
        IEnumerable<string> arguments,
        string paneDirectory,
        Func<string, string> quoteArgument)
    {
        var environment = OpenCodePaneStore.BuildEnvironment(paneDirectory).ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        environment[ConfigContentVariable] = Overlay;
        environment[ServerConfigContentVariable] = $"{{\"model\":\"{DefaultModel}\"}}";

        var script = new System.Text.StringBuilder();
        script.AppendLine("@echo off");

        foreach (var pair in environment)
            script.AppendLine($"set \"{pair.Key}={pair.Value}\"");

        script.AppendLine("setlocal");
        script.AppendLine();

        // One line: a batch file treats each line as a separate command, so an argument
        // wrapped onto its own line would be run instead of passed.
        script.Append(executable);
        foreach (var argument in arguments)
            script.Append(' ').Append(argument);

        var scriptPath = OpenCodePaneStore.WriteLauncher(paneDirectory, script.ToString());

        // The pane runs the script rather than inlining it, so nothing here has to be
        // escaped for a quoted command line.
        return quoteArgument(scriptPath);
    }
}