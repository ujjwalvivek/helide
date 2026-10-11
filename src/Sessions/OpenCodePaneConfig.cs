using System.IO;
using System.Security.Cryptography;
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

    private const string ScriptPrefix = "launch-";

    /// <summary>
    /// Builds the launch command for a pane.
    /// </summary>
    /// <remarks>
    /// The overlay is delivered through a small generated script rather than inline on the
    /// command line. An inline <c>cmd /c "set ..."</c> cannot carry it: the outer quotes
    /// make cmd re-parse the value, so the JSON's own quotes end the assignment, and the
    /// pane starts while silently ignoring what it was told. Inside a script file the same
    /// assignment is unambiguous, because cmd is parsing a file rather than a quoted string.
    /// <para>
    /// The script is named after the command it runs, so panes given different arguments
    /// get different files. Putting every pane on one shared script looked equivalent and
    /// was not: restoring several panes at once meant each one overwrote the same file, and
    /// a pane could end up running the last one written -- which is how panes launched with
    /// no session argument and opened blank while their status went quiet.
    /// </para>
    /// </remarks>
    public static string BuildLaunchCommand(
        string executable,
        IEnumerable<string> arguments,
        Func<string, string> quoteArgument)
    {
        var inner = new List<string> { executable };
        inner.AddRange(arguments);

        var command = string.Join(' ', inner.Select(quoteArgument));

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine($"set \"{ConfigContentVariable}={CliOverlay}\"");
        script.AppendLine("setlocal");
        script.AppendLine();

        // One line: a batch file treats each line as a separate command, so an argument
        // wrapped onto its own line would be run instead of passed.
        script.Append(command);

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Helide");

        Directory.CreateDirectory(directory);

        // Keyed on the command so the same launch reuses its script and a different one
        // gets its own: two panes restored together can no longer share a file.
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(command)))[..16];

        var path = Path.Combine(directory, $"{ScriptPrefix}{hash}.cmd");
        File.WriteAllText(path, script.ToString(), new UTF8Encoding(false));

        return quoteArgument(path);
    }
}