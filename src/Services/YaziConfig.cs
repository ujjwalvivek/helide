using System.IO;
using System.Reflection;
using System.Text;

namespace Helide;

/// <summary>
/// The yazi half of "the file tree opens files in the editor".
/// </summary>
/// <remarks>
/// Yazi opens files through its own <c>[opener]</c> rules, so the only way a click in
/// the project browser can land in Helide's editor is a config inside the user's yazi
/// config directory that points at the hand-off script. That directory is the user's,
/// so nothing is written there until they ask for it -- this type is what they are
/// agreeing to, and what the palette command runs later.
///
/// The bundled files are embedded rather than shipped beside the exe because Helide
/// publishes as a single file, and the files are needed in a specific place on the
/// user's machine, not beside the running image.
/// </remarks>
internal static class YaziConfig
{
    // The marker for "Helide's config is installed". It is checked rather than a stored
    // flag because yazi only reads its config at startup: the script's presence on disk
    // is the one fact that says whether opening a file actually reaches Helide, and it
    // survives the user deleting it, restoring a backup, or editing the config by hand.
    //
    // Chosen because nothing else would have a file of this name in a yazi config
    // directory, so it is never mistaken for a config the user already had.
    private const string ScriptName = "open-with-helix.ps1";

    // Installed as one set, not as the script alone. yazi.toml is what points the
    // openers at the script, and keymap.toml and theme.toml are what make a 220px
    // sidebar usable at all -- stock yazi assumes a full-width terminal and spends most
    // of the pane on a parent column and a preview column. Installing half of that
    // leaves a hand-off that works next to a tree that does not.
    private static readonly string[] BundledFiles =
        ["yazi.toml", "keymap.toml", "theme.toml", ScriptName];

    private const string ResourcePrefix = "Helide.YaziConfig.";

    /// <summary>Where yazi on Windows looks for its configuration.</summary>
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "yazi",
        "config");

    /// <summary>The hand-off script yazi's openers invoke.</summary>
    public static string ScriptPath => Path.Combine(ConfigDirectory, ScriptName);

    public static bool IsInstalled() => File.Exists(ScriptPath);

    /// <summary>
    /// Writes the bundled config into the user's yazi config directory. Anything found
    /// there first is moved aside into a backup folder, so accepting the offer costs
    /// nothing that cannot be undone.
    /// </summary>
    public static YaziInstall Install()
    {
        Directory.CreateDirectory(ConfigDirectory);

        var replaced = new List<string>();
        foreach (var name in BundledFiles)
        {
            if (File.Exists(Path.Combine(ConfigDirectory, name)))
                replaced.Add(name);
        }

        string? backup = null;
        if (replaced.Count > 0)
        {
            backup = NewBackupDirectory();
            foreach (var name in replaced)
            {
                // Move rather than copy: a leftover original beside the installed copy
                // would be picked up as the config on the next read.
                var target = Path.Combine(backup, name);
                File.Move(Path.Combine(ConfigDirectory, name), target);
            }
        }

        foreach (var name in BundledFiles)
            File.WriteAllText(Path.Combine(ConfigDirectory, name), Read(name));

        return new YaziInstall(replaced, backup);
    }

    // Named by the minute rather than by a counter, so a second install lands in its own
    // folder instead of merging into the first one's and losing what it replaced.
    private static string NewBackupDirectory()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = Path.Combine(ConfigDirectory, $"helide-backup-{stamp}");
        for (var suffix = 2; Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(ConfigDirectory, $"helide-backup-{stamp}-{suffix}");

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string Read(string name)
    {
        var stream = typeof(YaziConfig).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new FileNotFoundException($"Embedded yazi config '{name}' is missing.");

        using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }
}

/// <summary>What installing the yazi config did, so the caller can say so.</summary>
/// <param name="ReplacedFiles">Files that already existed and were moved aside.</param>
/// <param name="BackupDirectory">Where they went, or null when there was nothing to back up.</param>
internal sealed record YaziInstall(IReadOnlyList<string> ReplacedFiles, string? BackupDirectory);
