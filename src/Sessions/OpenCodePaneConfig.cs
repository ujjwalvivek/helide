using System.IO;
using System.Text;

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
    /// The overlay merged over the user's <c>cli.json</c>, for this process only.
    /// </summary>
    /// <remarks>
    /// OpenCode 2.x draws a tab strip of its own, which puts a second row of tabs inside a
    /// pane that already sits under Helide's tabs. It is switched off here rather than in
    /// the user's config, so every pane looks the same on every machine and nobody has to
    /// configure anything.
    /// </remarks>
    private const string CliOverlay = "{\"tabs\":{\"mode\":\"off\"}}";

    /// <summary>
    /// The environment variable name OpenCode reads pane-local CLI config from.
    /// </summary>
    public const string ConfigContentVariable = "OPENCODE_CLI_CONFIG_CONTENT";

    private const string ScriptName = "launch.cmd";

    /// <summary>
    /// The script, which is the same for every pane. Only the argument varies, which is
    /// what lets a single file serve every pane without them interfering.
    /// </summary>
    private static string Script => $"""
        @echo off
        set "{ConfigContentVariable}={CliOverlay}"
        if not "%~1"=="" opencode.cmd -s %~1
        if "%~1"=="" opencode.cmd

        """;

    /// <summary>
    /// Where the script lives. One file, under Helide's own local data.
    /// </summary>
    public static string ScriptPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Helide",
        ScriptName);

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
    /// shared file. Passing the id as an argument keeps the file identical for every pane:
    /// there is nothing to overwrite, and nothing to accumulate.
    /// </para>
    /// </remarks>
    public static string BuildLaunchCommand(
        string? sessionId,
        Func<string, string> quoteArgument)
    {
        // Rewritten on every launch so a stale or partial script cannot strand a pane, and
        // so a changed overlay reaches panes opened after an upgrade.
        File.WriteAllText(ScriptPath, Script, new UTF8Encoding(false));

        var command = quoteArgument(ScriptPath);

        if (sessionId is { Length: > 0 } id)
            command += " " + quoteArgument(id);

        return command;
    }
}