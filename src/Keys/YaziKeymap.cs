using System.IO;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Helide.Keys;

/// <summary>
/// yazi's keys as they are bound here: the default keymap yazi ships inside its own
/// program, with the user's <c>keymap.toml</c> laid over it the way yazi does.
/// </summary>
/// <remarks>
/// In each layer yazi tries the user's <c>prepend_keymap</c> first, then their
/// <c>keymap</c> if they wrote one (it replaces the default) or the default, then their
/// <c>append_keymap</c>, and the first entry for a key sequence wins. A key sequence an
/// earlier entry already claims does nothing for a later one, so it is not listed for it.
/// </remarks>
internal static class YaziKeymap
{
    private const string Tool = "yazi";
    private const string Marker = "#:schema https://yazi-rs.github.io/schemas/keymap.json";

    public static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("YAZI_CONFIG_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yazi", "config");

    public static string ConfigFile => Path.Combine(ConfigDirectory, "keymap.toml");

    public static IReadOnlyList<KeyBinding> Read()
    {
        if (DefaultKeymap() is not { } defaults)
            return [];

        var user = UserKeymap();
        var result = new List<KeyBinding>();

        foreach (var (layer, value) in defaults)
        {
            if (value is not TomlTable table)
                continue;

            var mine = user?.TryGetValue(layer, out var own) == true ? own as TomlTable : null;

            var ordered = new List<(TomlTable Entry, bool Changed)>();
            ordered.AddRange(Entries(mine, "prepend_keymap").Select(entry => (entry, true)));
            ordered.AddRange(mine?.ContainsKey("keymap") == true
                ? Entries(mine, "keymap").Select(entry => (entry, true))
                : Entries(table, "keymap").Select(entry => (entry, false)));
            ordered.AddRange(Entries(mine, "append_keymap").Select(entry => (entry, true)));

            result.AddRange(Layer(layer, ordered));
        }

        return result;
    }

    private static IEnumerable<KeyBinding> Layer(string layer, List<(TomlTable Entry, bool Changed)> ordered)
    {
        var claimed = new HashSet<string>();
        var rows = new List<(string Run, string Description, List<IReadOnlyList<string>> Keys, bool Changed)>();

        foreach (var (entry, changed) in ordered)
        {
            var run = Run(entry);
            if (run.Length == 0)
                continue;

            var sequence = On(entry);
            var key = string.Join('\u0001', sequence);

            // One row per action: the same run line under another key is the same control.
            var row = rows.FindIndex(existing => existing.Run == run);
            if (row < 0)
            {
                var description = entry.TryGetValue("desc", out var desc) && desc is string text && text.Length > 0 ? text : run;
                rows.Add((run, description, [], changed));
                row = rows.Count - 1;
            }
            else if (changed)
            {
                rows[row] = rows[row] with { Changed = true };
            }

            if (sequence.Count > 0 && claimed.Add(key))
                rows[row].Keys.Add(sequence.Select(KeyNames.Pretty).ToList());
        }

        return rows.Select(row => new KeyBinding(Tool, layer, row.Run, row.Description, row.Keys, row.Changed, ConfigFile));
    }

    private static IEnumerable<TomlTable> Entries(TomlTable? table, string name) =>
        table is not null && table.TryGetValue(name, out var list) && list is TomlTableArray entries
            ? entries
            : table is not null && table.TryGetValue(name, out var array) && array is TomlArray items
                ? items.OfType<TomlTable>()
                : [];

    // "on" is one key, or a list of keys pressed one after another.
    private static List<string> On(TomlTable entry) =>
        entry.TryGetValue("on", out var on) switch
        {
            true when on is string single => [single],
            true when on is TomlArray keys => keys.Select(key => key?.ToString() ?? string.Empty).Where(key => key.Length > 0).ToList(),
            _ => [],
        };

    // "run" is one command, or several run in turn.
    private static string Run(TomlTable entry) =>
        entry.TryGetValue("run", out var run) switch
        {
            true when run is string single => single,
            true when run is TomlArray many => string.Join("; ", many.Select(item => item?.ToString())),
            _ => string.Empty,
        };

    private static TomlTable? UserKeymap()
    {
        try
        {
            return File.Exists(ConfigFile) ? Toml.ToModel(File.ReadAllText(ConfigFile)) : null;
        }
        catch (Exception)
        {
            // yazi would refuse it too, and run on its defaults.
            return null;
        }
    }

    private static (string Path, DateTime Written, TomlTable Table)? _defaults;

    // The keymap yazi was built with, read out of yazi.exe itself, so it is always the
    // installed version's.
    private static TomlTable? DefaultKeymap()
    {
        if (ToolExecutable.Find("yazi.exe") is not { } path)
            return null;

        var written = File.GetLastWriteTimeUtc(path);
        if (_defaults is { } cached && cached.Path == path && cached.Written == written)
            return cached.Table;

        try
        {
            var bytes = File.ReadAllBytes(path);
            var marker = Encoding.UTF8.GetBytes(Marker);
            var start = bytes.AsSpan().IndexOf(marker);
            if (start < 0)
                return null;

            var length = bytes.AsSpan(start).IndexOf((byte)0);
            var text = Encoding.UTF8.GetString(bytes, start, length < 0 ? bytes.Length - start : length);

            // The keymap ends with the closing bracket of its last list; whatever the
            // compiler put after it is not part of it.
            var end = text.LastIndexOf("\n]", StringComparison.Ordinal);
            if (end < 0)
                return null;

            var table = Toml.ToModel(text[..(end + 2)]);
            _defaults = (path, written, table);
            return table;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
