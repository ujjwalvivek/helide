using System.Windows.Input;

namespace Helide.Shell;

/// <summary>
/// One entry in the command palette: what it does, what to search it by, and how to
/// show that it has a shortcut.
/// </summary>
/// <remarks>
/// Commands are built fresh each time the palette opens rather than kept alive,
/// because most of them depend on what is currently open -- which editor tab is
/// active, whether a workspace is loaded, which project was opened last. Caching them
/// would mean every one of those facts going stale between openings.
/// </remarks>
public sealed class PaletteCommand
{
    public PaletteCommand(
        string title,
        Action invoke,
        string? group = null,
        string? detail = null,
        string? keywords = null,
        Func<bool>? canInvoke = null,
        IReadOnlyList<string>? keys = null,
        string? scope = null)
    {
        Title = title;
        Invoke = invoke;
        Group = group;
        Detail = detail;
        Keywords = keywords;
        CanInvoke = canInvoke;
        Keys = keys;
        Scope = scope;
    }

    /// <summary>
    /// Key caps to draw, when they are not a Ctrl/Alt/Shift shortcut that
    /// <see cref="Detail"/> can carry: a tool's <c>g g</c>, or several keys at once.
    /// "/" between them is drawn as a plain separator, not a cap.
    /// </summary>
    public IReadOnlyList<string>? Keys { get; }

    /// <summary>
    /// What "scope:" at the start of the query narrows to, e.g. a tool in the keybindings
    /// list, so "helix:save" searches Helix alone.
    /// </summary>
    public string? Scope { get; }

    public string Title { get; }

    /// <summary>Group heading shown under the title, e.g. "File" or "Theme".</summary>
    public string? Group { get; }

    /// <summary>Right-aligned hint. A shortcut where one exists, else a short note.</summary>
    public string? Detail { get; }

    /// <summary>
    /// Extra words that should match this command but do not appear in the title, so
    /// "git" finds "Show Lazygit Panel" without spelling the product name in the title.
    /// </summary>
    public string? Keywords { get; }

    public Action Invoke { get; }

    /// <summary>
    /// False hides the entry entirely. Used for commands that need an open workspace,
    /// which would otherwise offer to act on a project that is not loaded.
    /// </summary>
    public Func<bool>? CanInvoke { get; }

    /// <summary>Shortcut text, or null when the command has no key binding.</summary>
    /// <remarks>
    /// Written the way it is printed on a keyboard: <c>Ctrl+1</c>, not the enum names
    /// <c>Control+D1</c> that <see cref="ModifierKeys"/> and <see cref="Key"/> stringify to.
    /// </remarks>
    public static string? Gesture(Key key, ModifierKeys modifiers = ModifierKeys.Control)
    {
        var name = key is >= Key.D0 and <= Key.D9
            ? ((int)key - (int)Key.D0).ToString()
            : key.ToString();

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(name);

        return string.Join('+', parts);
    }
}

/// <summary>
/// One rendered line in the palette: either a category heading or a command under one.
/// </summary>
/// <remarks>
/// Headings and commands are interleaved in a single list rather than the commands
/// being grouped by a ListBox <c>GroupStyle</c>. Grouping re-groups whenever the query
/// re-sorts the list, which makes headings jump around mid-typing; interleaving keeps
/// each heading attached to the commands it introduces.
/// </remarks>
internal sealed class PaletteRow
{
    private static readonly string[] Modifiers = ["Ctrl", "Alt", "Shift"];

    private PaletteRow(string title, PaletteCommand? command)
    {
        Title = title;
        Command = command;
        Keys = command?.Keys ?? ShortcutKeys(command?.Detail);
        Glyph = GlyphFor(command?.Group);
    }

    // A shortcut is recognised by its shape -- a modifier, then keys, joined by plus
    // signs -- so commands keep passing one plain string and need no second field.
    private static IReadOnlyList<string> ShortcutKeys(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail.Contains(' '))
            return [];

        var parts = detail.Split('+', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && Modifiers.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
            ? parts
            : [];
    }

    private static string GlyphFor(string? group) => group switch
    {
        "File" => "",      // open file
        "Recent" => "",    // history
        "Window" => "",    // new window
        "New" => "",       // add
        "Close" => "",     // cancel
        "Focus" => "",     // open in new window / go to
        "View" => "",      // view
        "Navigate" => "",  // switch
        "Run" => "",       // play
        "Branch" => "\uE8AB",  // switch
        "Remote" => "\uE774",  // globe
        "Select" => "\uE73E",  // check
        "Up" => "\uE74A",      // up
        "Folder" => "\uE8B7",  // folder
        "Theme" => "",     // colour
        _ => "",           // chevron
    };

    public string Title { get; }

    /// <summary>Right-aligned hint for a command row; empty on a heading.</summary>
    public string Detail => Command?.Detail ?? string.Empty;

    /// <summary>
    /// The keys of the command's shortcut, one per cap, or empty when its hint is not a
    /// shortcut. A hint is either a shortcut or a note, never both, and the two are
    /// drawn differently: caps for keys, quiet text for everything else.
    /// </summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>The hint when it is not a shortcut: a path, or a word of explanation.</summary>
    public string Note => Keys.Count == 0 || Command?.Keys is not null ? Detail : string.Empty;

    /// <summary>The icon for the command's group, from Segoe Fluent Icons.</summary>
    public string Glyph { get; }

    public bool IsCommand => Command is not null;

    /// <summary>Null for a heading row, which cannot be run.</summary>
    public PaletteCommand? Command { get; }

    public bool IsHeading => Command is null;

    public static PaletteRow Heading(string group) => new(group.ToUpperInvariant(), null);

    public static PaletteRow For(PaletteCommand command) => new(command.Title, command);
}
