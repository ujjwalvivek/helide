using System.Diagnostics;
using System.IO;
using System.Text;

namespace Helide.Keys;

/// <summary>
/// lazygit's keys as they are bound here: what <c>lazygit --config</c> prints as its
/// defaults, with the <c>keybinding:</c> section of the user's config laid over them.
/// </summary>
/// <remarks>
/// lazygit names its controls but does not describe them in its config, so a control
/// is shown under its own name, spelled out: <c>pushFiles</c> as "Push files".
/// </remarks>
internal static class LazygitKeymap
{
    private const string Tool = "lazygit";

    public static string? ConfigFile => ConfigDirectory() is { } directory ? Path.Combine(directory, "config.yml") : null;

    public static IReadOnlyList<KeyBinding> Read()
    {
        if (Defaults() is not { } printed)
            return [];

        var defaults = Section(printed);
        var user = ConfigFile is { } file && File.Exists(file) ? Section(Read(file)) : [];

        var result = new List<KeyBinding>();
        foreach (var ((context, action), keys) in defaults)
        {
            var changed = user.TryGetValue((context, action), out var mine);
            var bound = changed ? mine! : keys;

            // "<disabled>" turns a control off.
            var list = bound
                .Where(key => key.Length > 0 && !key.Equals("<disabled>", StringComparison.OrdinalIgnoreCase))
                .Select(key => (IReadOnlyList<string>)[KeyNames.Pretty(key)])
                .ToList();

            result.Add(new KeyBinding(Tool, context, action, Spell(action), list, changed, ConfigFile));
        }

        return result;
    }

    // "pushFiles" -> "Push files"; "quit-alt1" -> "Quit (alt 1)".
    private static string Spell(string action)
    {
        var alt = action.IndexOf("-alt", StringComparison.Ordinal);
        var name = alt > 0 ? action[..alt] : action;

        var spelled = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && spelled.Length > 0)
                spelled.Append(' ');
            spelled.Append(spelled.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }

        if (alt > 0)
            spelled.Append(" (alt").Append(action[(alt + 4)..] is { Length: > 0 } n ? " " + n : string.Empty).Append(')');

        return spelled.ToString();
    }

    // The keybinding section, as (panel, control) -> keys. A control can take one key or
    // a list of them.
    private static Dictionary<(string Context, string Action), List<string>> Section(string yaml)
    {
        var result = new Dictionary<(string, string), List<string>>();

        var inSection = false;
        var sectionIndent = -1;
        var context = string.Empty;
        var contextIndent = -1;
        (string Context, string Action)? openList = null;

        foreach (var raw in yaml.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = StripComment(raw);
            if (line.Trim().Length == 0)
                continue;

            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();

            if (!inSection)
            {
                if (indent == 0 && text == "keybinding:")
                {
                    inSection = true;
                    sectionIndent = 0;
                }
                continue;
            }

            if (indent <= sectionIndent)
                break;

            // "- key" under a control whose value is a block list.
            if (text.StartsWith("- ", StringComparison.Ordinal) && openList is { } list)
            {
                result[list].Add(Scalar(text[2..]));
                continue;
            }

            var colon = KeyColon(text);
            if (colon < 0)
                continue;

            var name = Scalar(text[..colon]);
            var value = text[(colon + 1)..].Trim();

            if (value.Length == 0)
            {
                // A panel heading, or a control whose keys follow as a list.
                if (contextIndent < 0 || indent <= contextIndent)
                {
                    context = name;
                    contextIndent = indent;
                    openList = null;
                }
                else
                {
                    openList = (context, name);
                    result[(context, name)] = [];
                }
                continue;
            }

            openList = null;
            result[(context, name)] = value.StartsWith('[')
                ? value.Trim('[', ']').Split(',').Select(Scalar).Where(key => key.Length > 0).ToList()
                : [Scalar(value)];
        }

        return result;
    }

    // Where the key ends: the first colon outside quotes that is followed by a space or
    // the end of the line. A key can itself be ':' when quoted.
    private static int KeyColon(string text)
    {
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c is '\'' or '"')
                quote = c;
            else if (c == ':' && (i == text.Length - 1 || text[i + 1] == ' '))
                return i;
        }

        return -1;
    }

    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || line[i - 1] == ' '))
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Scalar(string text)
    {
        var value = text.Trim();
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
        {
            var quote = value[0];
            value = value[1..^1];
            value = quote == '\'' ? value.Replace("''", "'") : value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        return value;
    }

    private static string Read(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? _configDirectory;

    private static string? ConfigDirectory()
    {
        if (_configDirectory is null)
            _configDirectory = Run("--print-config-dir")?.Trim() ?? string.Empty;

        return _configDirectory.Length == 0 ? null : _configDirectory;
    }

    // What `lazygit --config` printed, kept until lazygit itself changes: running it is
    // most of what reading lazygit's keys costs.
    private static (string Stamp, string Printed)? _defaults;

    private static string? Defaults()
    {
        var stamp = ToolExecutable.Stamp("lazygit.exe") ?? string.Empty;
        if (_defaults is { } cached && cached.Stamp == stamp)
            return cached.Printed;

        if (Run("--config") is not { } printed)
            return null;

        _defaults = (stamp, printed);
        return printed;
    }

    private static string? Run(string argument)
    {
        try
        {
            var start = new ProcessStartInfo("lazygit.exe", argument)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(start);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
