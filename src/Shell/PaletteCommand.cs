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
        Func<bool>? canInvoke = null)
    {
        Title = title;
        Invoke = invoke;
        Group = group;
        Detail = detail;
        Keywords = keywords;
        CanInvoke = canInvoke;
    }

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
    public static string? Gesture(Key key, ModifierKeys modifiers = ModifierKeys.Control) =>
        modifiers == ModifierKeys.None ? key.ToString() : $"{modifiers}+{key}";
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
    private PaletteRow(string title, PaletteCommand? command)
    {
        Title = title;
        Command = command;
    }

    public string Title { get; }

    /// <summary>Right-aligned hint for a command row; empty on a heading.</summary>
    public string Detail => Command?.Detail ?? string.Empty;

    /// <summary>Null for a heading row, which cannot be run.</summary>
    public PaletteCommand? Command { get; }

    public bool IsHeading => Command is null;

    public static PaletteRow Heading(string group) => new(group.ToUpperInvariant(), null);

    public static PaletteRow For(PaletteCommand command) => new(command.Title, command);
}
