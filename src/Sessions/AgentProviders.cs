using System.IO;

namespace Helide.Sessions;

/// <summary>One agent Helide can run in the agent pane.</summary>
/// <param name="Type">The id saved in workspace state: <c>opencode</c>, <c>codex</c> or <c>claude</c>.</param>
/// <param name="Label">The name shown on tabs and in menus.</param>
/// <param name="Command">The command name looked for on PATH, without an extension.</param>
internal sealed record AgentProvider(string Type, string Label, string Command);

/// <summary>
/// The agents Helide knows how to run, and which of them are installed.
/// </summary>
/// <remarks>
/// None of them is required. Helide offers whichever it finds on PATH and leaves the rest
/// out of every menu, so a machine with one agent, or none, is a normal machine rather
/// than a broken install.
/// <para>
/// Each one arrives in more than one form -- a native <c>.exe</c> from an installer, or a
/// <c>.cmd</c> shim from npm -- so the lookup returns the file it actually found, and that
/// is what gets launched. A pane is started without a shell in front of it, and
/// <c>CreateProcess</c> does not go looking for extensions the way a shell does.
/// </para>
/// </remarks>
internal static class AgentProviders
{
    public const string OpenCode = "opencode";
    public const string Codex = "codex";
    public const string Claude = "claude";

    /// <summary>Every known agent, in the order they are offered.</summary>
    public static readonly IReadOnlyList<AgentProvider> All =
    [
        new(OpenCode, "OpenCode", "opencode"),
        new(Codex, "Codex", "codex"),
        new(Claude, "Claude", "claude"),
    ];

    // Only forms CreateProcess can start, in the order a shell would prefer them.
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat"];

    /// <summary>The label for a saved type, including ones this build does not know.</summary>
    public static string LabelFor(string type) =>
        All.FirstOrDefault(provider => provider.Type == type)?.Label ?? type;

    /// <summary>The agents found on PATH right now, in offer order.</summary>
    public static IReadOnlyList<AgentProvider> Installed() =>
        All.Where(provider => Resolve(provider.Type) is not null).ToArray();

    public static bool IsInstalled(string type) => Resolve(type) is not null;

    /// <summary>
    /// The file name to launch for an agent -- <c>codex.cmd</c>, <c>claude.exe</c> -- or
    /// null when it is not on PATH.
    /// </summary>
    /// <remarks>
    /// Looked up every time rather than cached: it is a handful of file checks, and an
    /// agent installed while Helide is open should appear without a restart.
    /// </remarks>
    public static string? Resolve(string type)
    {
        var provider = All.FirstOrDefault(candidate => candidate.Type == type);
        if (provider is null)
            return null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0)
                continue;

            foreach (var extension in Extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, provider.Command + extension)))
                        return provider.Command + extension;
                }
                catch (Exception)
                {
                    // A malformed PATH entry is skipped, not fatal.
                }
            }
        }

        return null;
    }
}
