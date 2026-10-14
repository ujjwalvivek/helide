namespace Helide.Keys;

/// <summary>One control in Helide or a tool, and every key that triggers it.</summary>
/// <param name="Tool">"Helide", "Helix", "yazi" or "lazygit".</param>
/// <param name="Context">Where it applies: a Helix mode, a yazi layer, a lazygit panel.</param>
/// <param name="Action">The tool's own name for it: a Helix command, a yazi run line.</param>
/// <param name="Description">What it does, in the tool's words where the tool has any.</param>
/// <param name="Keys">Each way to trigger it, as the sequence of keys to press. Empty when nothing is bound.</param>
/// <param name="Changed">Bound differently from the tool's default, by the user's own config.</param>
/// <param name="ConfigFile">The file a change to it would go in, for the user to edit.</param>
internal sealed record KeyBinding(
    string Tool,
    string Context,
    string Action,
    string Description,
    IReadOnlyList<IReadOnlyList<string>> Keys,
    bool Changed,
    string? ConfigFile);

/// <summary>Where a tool's program is, through any link a package manager put on PATH.</summary>
internal static class ToolExecutable
{
    public static string? Find(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator))
        {
            try
            {
                var candidate = System.IO.Path.Combine(directory.Trim(), name);
                if (!System.IO.File.Exists(candidate))
                    continue;

                var info = new System.IO.FileInfo(candidate);
                return info.LinkTarget is not null
                    ? info.ResolveLinkTarget(true)?.FullName ?? candidate
                    : candidate;
            }
            catch (Exception)
            {
                // A PATH entry that is not a folder.
            }
        }

        return null;
    }

    /// <summary>The program's path and when it was written: changes when the tool is updated.</summary>
    public static string? Stamp(string name) =>
        Find(name) is { } path ? path + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks : null;
}

/// <summary>
/// Key names the way a keyboard prints them, whichever notation a tool writes them in:
/// Helix's <c>C-w</c>, yazi's <c>&lt;C-w&gt;</c> and lazygit's <c>&lt;c-w&gt;</c> all become <c>Ctrl+w</c>.
/// </summary>
/// <remarks>
/// A letter keeps its case: in a modal tool <c>g</c> and <c>G</c> are different keys.
/// </remarks>
internal static class KeyNames
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "Esc", ["escape"] = "Esc",
        ["ret"] = "Enter", ["enter"] = "Enter", ["cr"] = "Enter", ["return"] = "Enter",
        ["tab"] = "Tab", ["backtab"] = "Shift+Tab",
        ["space"] = "Space",
        ["bs"] = "Backspace", ["backspace"] = "Backspace",
        ["del"] = "Del", ["delete"] = "Del",
        ["ins"] = "Ins", ["insert"] = "Ins",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        ["pgup"] = "PgUp", ["pageup"] = "PgUp", ["pgdown"] = "PgDn", ["pagedown"] = "PgDn", ["pgdn"] = "PgDn",
        ["home"] = "Home", ["end"] = "End",
        ["minus"] = "-", ["lt"] = "<", ["gt"] = ">",
        ["null"] = "Null",
    };

    private static readonly Dictionary<string, string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = "Ctrl", ["ctrl"] = "Ctrl", ["control"] = "Ctrl",
        ["a"] = "Alt", ["alt"] = "Alt", ["m"] = "Alt", ["meta"] = "Alt",
        ["s"] = "Shift", ["shift"] = "Shift",
        ["d"] = "Super", ["super"] = "Super",
    };

    /// <summary>One key, from any of the three notations.</summary>
    public static string Pretty(string key)
    {
        if (key.Length == 0)
            return key;

        var inner = key.Length > 2 && key[0] == '<' && key[^1] == '>' ? key[1..^1] : key;
        if (inner.Length == 1)
            return inner;

        // Modifiers come first, split from the key by '-' (Helix, yazi) or '+' (lazygit).
        // The key itself can be '-' or '+', so only what comes before the last separator
        // is read as modifiers.
        var parts = new List<string>();
        var rest = inner;
        while (true)
        {
            var cut = rest.IndexOfAny(['-', '+']);
            if (cut <= 0 || cut == rest.Length - 1 || !Modifiers.TryGetValue(rest[..cut], out var modifier))
                break;

            parts.Add(modifier);
            rest = rest[(cut + 1)..];
        }

        if (Named.TryGetValue(rest, out var named))
            parts.Add(named);
        else if (rest.Length > 1 && (rest[0] == 'f' || rest[0] == 'F') && int.TryParse(rest[1..], out var number))
            parts.Add("F" + number);
        else
            parts.Add(rest);

        return string.Join('+', parts);
    }
}
