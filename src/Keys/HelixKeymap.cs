using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace Helide.Keys;

/// <summary>
/// Helix's keys as they are bound here: its defaults, with the user's <c>[keys]</c> laid
/// over them.
/// </summary>
/// <remarks>
/// Helix builds its default keymap into the program and has no way to print it, so
/// Helide carries one for each Helix release, generated from Helix's own source
/// (<c>tools/keymaps/helix_defaults.py</c>), and uses the one for the version
/// installed. The user's part is read from their config, which is the same
/// <c>config.toml</c> Helix reads; nothing is written to it.
/// </remarks>
internal static class HelixKeymap
{
    private const string Tool = "Helix";

    public static string ConfigFile => Path.Combine(
        Environment.GetEnvironmentVariable("HELIX_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "helix"),
        "config.toml");

    /// <summary>The bindings, plus a note when the defaults are from another release.</summary>
    public static (IReadOnlyList<KeyBinding> Bindings, string? Note) Read()
    {
        if (LoadDefaults() is not { } defaults)
            return ([], null);

        // mode -> key path -> (command, changed)
        var modes = new Dictionary<string, Dictionary<string, (string Command, bool Changed)>>();
        foreach (var binding in defaults.Bindings)
        {
            if (!modes.TryGetValue(binding.Mode, out var table))
                modes[binding.Mode] = table = new();

            table[string.Join('\u0001', binding.Keys)] = (binding.Command, false);
        }

        foreach (var (mode, path, command) in UserKeys())
        {
            if (!modes.TryGetValue(mode, out var table))
                modes[mode] = table = new();

            table[path] = (command, true);
        }

        var result = new List<KeyBinding>();
        var bound = new HashSet<string>();
        foreach (var (mode, table) in modes)
        {
            // One row per command, carrying every key path that runs it.
            foreach (var group in table
                         .Where(entry => entry.Value.Command != "no_op")
                         .GroupBy(entry => entry.Value.Command))
            {
                bound.Add(group.Key);
                var keys = group
                    .Select(entry => (IReadOnlyList<string>)entry.Key.Split('\u0001').Select(KeyNames.Pretty).ToList())
                    .ToList();

                result.Add(new KeyBinding(
                    Tool,
                    mode,
                    group.Key,
                    Describe(group.Key, defaults.Commands),
                    keys,
                    group.Any(entry => entry.Value.Changed),
                    ConfigFile));
            }
        }

        // Every command Helix has, even with no key on it: it can still be bound, and
        // run from Helix's own command palette.
        foreach (var (command, doc) in defaults.Commands)
        {
            if (!bound.Contains(command) && command != "no_op")
                result.Add(new KeyBinding(Tool, "unbound", command, doc, [], false, ConfigFile));
        }

        var note = InstalledVersion() is { } installed && installed != defaults.Version
            ? $"Defaults are Helix {defaults.Version}'s; {installed} is installed"
            : null;

        return (result, note);
    }

    // A command's own description; a typed command (":w") or a sequence of them is
    // shown as written.
    private static string Describe(string command, IReadOnlyDictionary<string, string> docs) =>
        docs.TryGetValue(command, out var doc) ? doc : command;

    private sealed record Defaults(string Version, IReadOnlyDictionary<string, string> Commands, IReadOnlyList<DefaultBinding> Bindings);

    private sealed record DefaultBinding(string Mode, IReadOnlyList<string> Keys, string Command);

    private const string ResourcePrefix = "Helide.Keys.helix-";

    private static Defaults? _defaults;

    private static Defaults? LoadDefaults()
    {
        if (_defaults is not null)
            return _defaults;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            if (Pick(assembly.GetManifestResourceNames(), InstalledVersion()) is not { } name)
                return null;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
                return null;

            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            var commands = root.GetProperty("commands").EnumerateObject()
                .ToDictionary(command => command.Name, command => command.Value.GetString() ?? command.Name);
            var bindings = root.GetProperty("bindings").EnumerateArray()
                .Select(binding => new DefaultBinding(
                    binding.GetProperty("mode").GetString()!,
                    binding.GetProperty("keys").EnumerateArray().Select(key => key.GetString()!).ToList(),
                    binding.GetProperty("command").GetString()!))
                .ToList();

            return _defaults = new Defaults(root.GetProperty("version").GetString()!, commands, bindings);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // [keys.normal] and friends, flattened: a nested table is a menu, a list is a
    // sequence of commands run one after another.
    private static IEnumerable<(string Mode, string Path, string Command)> UserKeys()
    {
        TomlTable model;
        try
        {
            if (!File.Exists(ConfigFile))
                yield break;

            model = Toml.ToModel(File.ReadAllText(ConfigFile));
        }
        catch (Exception)
        {
            // A config Helix itself would refuse: its defaults are what is running.
            yield break;
        }

        if (!model.TryGetValue("keys", out var keys) || keys is not TomlTable modes)
            yield break;

        foreach (var (mode, value) in modes)
        {
            if (value is not TomlTable table)
                continue;

            foreach (var entry in Flatten(table, []))
                yield return (mode, entry.Path, entry.Command);
        }
    }

    private static IEnumerable<(string Path, string Command)> Flatten(TomlTable table, List<string> prefix)
    {
        foreach (var (key, value) in table)
        {
            var path = new List<string>(prefix) { key };
            switch (value)
            {
                case TomlTable menu:
                    foreach (var inner in Flatten(menu, path))
                        yield return inner;
                    break;
                case TomlArray sequence:
                    yield return (string.Join('\u0001', path), string.Join(", ", sequence.Select(item => item?.ToString())));
                    break;
                case string command:
                    yield return (string.Join('\u0001', path), command);
                    break;
            }
        }
    }

    // The installed version's own keymap; else the nearest release to it: the newest one
    // older than it, or for a Helix older than all of them the oldest. A Helix whose
    // version cannot be read gets the newest.
    private static string? Pick(IEnumerable<string> resources, string? installed)
    {
        var known = resources
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => (Name: name, Version: Version(name[ResourcePrefix.Length..^".json".Length])))
            .Where(entry => entry.Version is not null)
            .OrderBy(entry => entry.Version)
            .ToList();

        if (known.Count == 0)
            return null;

        if (installed is not null && Version(installed) is { } wanted)
        {
            var older = known.LastOrDefault(entry => entry.Version! <= wanted);
            return older.Name ?? known[0].Name;
        }

        return known[^1].Name;
    }

    // Helix numbers releases YY.MM, with a patch number when there is one.
    private static Version? Version(string text) =>
        System.Version.TryParse(text.Count(c => c == '.') == 1 ? text + ".0" : text, out var version) ? version : null;

    private static string? _installed;

    private static string? InstalledVersion()
    {
        if (_installed is not null)
            return _installed.Length == 0 ? null : _installed;

        try
        {
            var start = new ProcessStartInfo("hx.exe", "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(start);
            var text = process?.StandardOutput.ReadToEnd() ?? string.Empty;
            process?.WaitForExit(3000);

            // "helix 25.07.1 (a05c151b)"
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _installed = parts.Length >= 2 ? parts[1] : string.Empty;
        }
        catch (Exception)
        {
            _installed = string.Empty;
        }

        return _installed.Length == 0 ? null : _installed;
    }
}
