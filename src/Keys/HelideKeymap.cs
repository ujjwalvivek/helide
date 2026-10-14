using System.Windows.Input;

namespace Helide.Keys;

/// <summary>A Helide action a shortcut can be bound to.</summary>
/// <param name="Id">Saved with the user's choice, so it must not change.</param>
/// <param name="Defaults">What it is bound to until the user says otherwise.</param>
internal sealed record HelideAction(string Id, string Title, string Context, string[] Defaults);

/// <summary>
/// Helide's own shortcuts: the one table both the key handler and the keybindings view
/// read, so what is shown is what runs.
/// </summary>
/// <remarks>
/// A shortcut is <c>Ctrl+Shift+P</c>-style text. The user's choices are kept by action
/// id in Helide's state and replace that action's defaults. Keys that only work inside
/// one overlay -- the palette's arrows, a dialog's Enter -- are listed by
/// <see cref="Fixed"/> and cannot be rebound.
/// </remarks>
internal static class HelideKeymap
{
    public const string Palette = "palette";
    public const string OpenFolder = "open-folder";
    public const string ErrorLocation = "error-location";
    public const string Mention = "mention";
    public const string Keybindings = "keybindings";
    public const string Zen = "zen";
    public const string Lazygit = "pane-lazygit";
    public const string Editor = "pane-editor";
    public const string Terminal = "pane-terminal";
    public const string Agent = "pane-agent";
    public const string Files = "pane-files";
    public const string ProjectCommands = "project-commands";

    public static readonly IReadOnlyList<HelideAction> Actions =
    [
        // The Shift form first: it is the one every pane leaves to Helide.
        new(Palette, "Show Command Palette", "window", ["Ctrl+Shift+P", "Ctrl+P"]),
        new(Keybindings, "Show Keybindings", "window", ["Ctrl+Shift+K"]),
        new(OpenFolder, "Open Folder", "window", ["Ctrl+Shift+O", "Ctrl+O"]),
        new(Zen, "Toggle Zen Mode", "workspace", ["F11"]),
        new(Lazygit, "Toggle Lazygit Panel", "workspace", ["Ctrl+1"]),
        new(Editor, "Focus Editor", "workspace", ["Ctrl+2"]),
        new(Terminal, "Toggle Terminal Panel", "workspace", ["Ctrl+3"]),
        new(Agent, "Toggle Agent Panel", "workspace", ["Ctrl+4"]),
        new(Files, "Toggle Project Browser", "workspace", ["Ctrl+5"]),
        new(ErrorLocation, "Open Error Location", "workspace", ["Ctrl+Shift+E"]),
        new(Mention, "Mention Current File in Agent", "workspace", ["Ctrl+Shift+A"]),
        new(ProjectCommands, "Run Project Command", "workspace", ["Ctrl+Shift+R"]),
    ];

    /// <summary>Keys Helide handles that belong to one overlay and stay as they are.</summary>
    public static readonly IReadOnlyList<(string Title, string Context, string[] Keys)> Fixed =
    [
        ("Open recent project 1 to 8", "welcome", ["Ctrl+1", "Ctrl+8"]),
        ("Continue with the last project", "welcome", ["Enter"]),
        ("Move through the list", "palette", ["Up", "Down", "Tab", "Shift+Tab"]),
        ("Run the selected row", "palette", ["Enter"]),
        ("Close the palette", "palette", ["Esc"]),
        ("Confirm", "dialog", ["Enter"]),
        ("Cancel", "dialog", ["Esc"]),
    ];

    private static Dictionary<string, List<string>> _overrides = new();

    /// <summary>The user's choices, as saved: action id to its shortcuts.</summary>
    public static void Load(Dictionary<string, List<string>>? overrides) =>
        _overrides = overrides is null ? new() : new(overrides);

    public static IReadOnlyList<string> KeysFor(string id)
    {
        if (_overrides.TryGetValue(id, out var mine))
            return mine;

        return Actions.FirstOrDefault(action => action.Id == id)?.Defaults ?? [];
    }

    public static bool IsChanged(string id) => _overrides.ContainsKey(id);

    /// <summary>The action another shortcut already belongs to, if any.</summary>
    public static HelideAction? Owner(string gesture, string exceptId) =>
        Actions.FirstOrDefault(action => action.Id != exceptId
            && KeysFor(action.Id).Any(key => string.Equals(key, gesture, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Binds the action to this one shortcut. Returns the overrides to save.</summary>
    public static Dictionary<string, List<string>> Set(string id, string gesture)
    {
        var action = Actions.First(candidate => candidate.Id == id);
        if (action.Defaults.Length == 1 && string.Equals(action.Defaults[0], gesture, StringComparison.OrdinalIgnoreCase))
            _overrides.Remove(id);
        else
            _overrides[id] = [gesture];

        return new(_overrides);
    }

    public static Dictionary<string, List<string>> Reset(string id)
    {
        _overrides.Remove(id);
        return new(_overrides);
    }

    /// <summary>The action this key press is bound to, or null.</summary>
    public static string? Match(Key key, ModifierKeys modifiers)
    {
        var pressed = Gesture(key, modifiers);
        if (pressed is null)
            return null;

        foreach (var action in Actions)
        {
            if (KeysFor(action.Id).Any(bound => string.Equals(bound, pressed, StringComparison.OrdinalIgnoreCase)))
                return action.Id;
        }

        return null;
    }

    /// <summary>
    /// Whether a shortcut gives way to an agent pane that has the keyboard. The agents
    /// use Ctrl with a letter for their own commands, so a Helide shortcut of that shape
    /// only works elsewhere; the same letter with Shift as well is always Helide's.
    /// </summary>
    public static bool YieldsToAgent(Key key, ModifierKeys modifiers) =>
        modifiers == ModifierKeys.Control && key is >= Key.A and <= Key.Z;

    /// <summary>The same question for a shortcut written as text, "Ctrl+P".</summary>
    public static bool YieldsToAgent(string gesture)
    {
        var parts = gesture.Split('+');
        return parts.Length == 2
            && string.Equals(parts[0], "Ctrl", StringComparison.OrdinalIgnoreCase)
            && parts[1].Length == 1
            && char.IsLetter(parts[1][0]);
    }

    /// <summary>
    /// The shortcut to show for an action where it is used: one that works there, and of
    /// those the one with fewest keys. Where an agent pane has the keyboard, one it would
    /// take for itself is left out.
    /// </summary>
    public static string? GestureFor(string id, bool agentHasKeyboard) =>
        KeysFor(id)
            .Where(key => !agentHasKeyboard || !YieldsToAgent(key))
            .OrderBy(key => key.Count(c => c == '+'))
            .FirstOrDefault();

    /// <summary>A key press as shortcut text, or null for a lone modifier.</summary>
    public static string? Gesture(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.None)
            return null;

        var name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)key - (int)Key.NumPad0).ToString(),
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Back => "Backspace",
            Key.Delete => "Del",
            Key.Prior => "PgUp",
            Key.Next => "PgDn",
            _ => key.ToString(),
        };

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(name);

        return string.Join('+', parts);
    }
}
