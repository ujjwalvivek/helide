using System.IO;
using System.Windows.Input;
using Helide.Keys;
using Helide.Shell;
using Helide.Terminal;
using KeyBinding = Helide.Keys.KeyBinding;

namespace Helide;

// Every control in Helide and its tools, as bound on this machine, in the palette.
// Helide's own shortcuts are changed here; a tool's opens that tool's config for you to
// change, since Helide never writes a tool's config.
public partial class MainWindow
{
    private static readonly PaletteMode KeybindingsMode =
        new("Search keys, or tool: to narrow (helix:, yazi:, lazygit:, helide:)", "binding", "bindings", "change");

    private async void ShowKeybindings()
    {
        if (_paletteOpen)
            return;

        var first = FocusedTool();
        var sets = await Task.Run(ReadToolKeys);

        var commands = new List<PaletteCommand>();
        var tools = new List<(string Tool, List<PaletteCommand> Rows)> { ("Helide", HelideRows()) };
        foreach (var (tool, bindings, note) in sets)
        {
            if (bindings.Count == 0)
                continue;

            var rows = new List<PaletteCommand>();
            if (note is not null)
                rows.Add(new PaletteCommand(note, () => { }, tool, scope: tool.ToLowerInvariant()));

            rows.AddRange(bindings.Select(ToolRow));
            tools.Add((tool, rows));
        }

        // The tool you were in comes first.
        foreach (var (_, rows) in tools.OrderBy(entry => entry.Tool == first ? 0 : 1))
            commands.AddRange(rows);

        ShowPalette(commands, KeybindingsMode);
    }

    private static List<(string Tool, IReadOnlyList<KeyBinding> Bindings, string? Note)> ReadToolKeys()
    {
        var helix = HelixKeymap.Read();
        return
        [
            ("Helix", helix.Bindings, helix.Note),
            ("yazi", YaziKeymap.Read(), null),
            ("lazygit", LazygitKeymap.Read(), null),
        ];
    }

    // Reads every tool's keys once in the background after the window opens, so the
    // first opening of the list does not wait on starting lazygit or reading yazi's
    // program. Each reader keeps what it can, and rereads the user's own config on every
    // opening, so a change there still shows.
    private static void WarmKeybindings() => _ = Task.Run(() =>
    {
        try
        {
            ReadToolKeys();
        }
        catch (Exception)
        {
            // Read again, and reported, when the list is opened.
        }
    });

    // The shortcut a palette row shows: one that works from where the palette was
    // opened, so a key an agent pane keeps for itself is not offered from there.
    private string? Shortcut(string id) => HelideKeymap.GestureFor(id, AgentHasKeyboard());

    private string FocusedTool()
    {
        if (_editorTabs.Any(tab => tab.Host.HasKeyboardFocus()))
            return "Helix";

        if (ActiveLeftHost()?.HasKeyboardFocus() == true)
            return _activeLeftTool == LeftTool.Project ? "yazi" : "lazygit";

        return "Helide";
    }

    private List<PaletteCommand> HelideRows()
    {
        var rows = new List<PaletteCommand>();

        foreach (var action in HelideKeymap.Actions)
        {
            var keys = HelideKeymap.KeysFor(action.Id);
            var target = action;
            rows.Add(new PaletteCommand(
                action.Title,
                () => CaptureShortcut(target),
                "Helide · " + action.Context,
                HelideKeymap.IsChanged(action.Id) ? "changed" : null,
                keywords: action.Context + " " + string.Join(' ', keys),
                keys: Caps(keys.Select(key => (IReadOnlyList<string>)[key]).ToList()),
                scope: "helide"));
        }

        foreach (var (title, context, keys) in HelideKeymap.Fixed)
        {
            rows.Add(new PaletteCommand(
                title,
                () => { },
                "Helide · " + context,
                "fixed",
                keywords: context + " " + string.Join(' ', keys),
                keys: Caps(keys.Select(key => (IReadOnlyList<string>)[key]).ToList()),
                scope: "helide"));
        }

        return rows;
    }

    private PaletteCommand ToolRow(KeyBinding binding)
    {
        var note = binding.Keys.Count == 0 ? "unbound" : binding.Changed ? "changed" : null;
        return new PaletteCommand(
            binding.Description,
            () => OpenKeyConfig(binding),
            $"{binding.Tool} · {binding.Context}",
            note,
            keywords: $"{binding.Context} {binding.Action} " + string.Join(' ', binding.Keys.SelectMany(sequence => sequence)),
            keys: Caps(binding.Keys),
            scope: binding.Tool.ToLowerInvariant());
    }

    // Each way to press it, one cap per key, with "/" between the ways. A control with
    // many keys shows the first few and how many more.
    private static IReadOnlyList<string> Caps(IReadOnlyList<IReadOnlyList<string>> keys)
    {
        const int Shown = 3;
        var caps = new List<string>();
        foreach (var sequence in keys.Take(Shown))
        {
            if (caps.Count > 0)
                caps.Add("/");
            caps.AddRange(sequence);
        }

        if (keys.Count > Shown)
            caps.Add($"+{keys.Count - Shown}");

        return caps;
    }

    // ---- a tool's binding: its config, for you to change ------------------------------

    private void OpenKeyConfig(KeyBinding binding)
    {
        if (binding.ConfigFile is not { } file)
            return;

        if (WorkspaceRoot.Visibility != System.Windows.Visibility.Visible)
        {
            ShowNotice("Keybindings", $"{binding.Tool}'s keys are set in {file}. Open a project to edit it here.");
            return;
        }

        // Opened at the binding itself when the file already sets it, else where that
        // tool's keys are set, else at the top.
        var line = (binding.Changed ? LineWith(file, binding.Action) : null) ?? KeysSectionLine(binding.Tool, file);
        if (line is { } at)
            OpenEditorAt(new SourceLocation(file, at, null, string.Empty));
        else
            OpenEditorTab(file);
    }

    private static int? LineWith(string file, string text)
    {
        try
        {
            if (!File.Exists(file))
                return null;

            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                if (!line.TrimStart().StartsWith('#') && line.Contains(text, StringComparison.Ordinal))
                    return number;
            }
        }
        catch (Exception)
        {
            // Unreadable: the section will do.
        }

        return null;
    }

    private static int? KeysSectionLine(string tool, string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;

            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var text = line.Trim();
                var found = tool switch
                {
                    "Helix" => text.StartsWith("[keys", StringComparison.Ordinal),
                    "yazi" => text.StartsWith('[') && !text.StartsWith("#", StringComparison.Ordinal),
                    "lazygit" => text.StartsWith("keybinding:", StringComparison.Ordinal),
                    _ => false,
                };

                if (found)
                    return number;
            }
        }
        catch (Exception)
        {
            // Unreadable: it opens at the top.
        }

        return null;
    }

    // ---- a Helide shortcut: changed right here -----------------------------------------

    private HelideAction? _capturingShortcut;

    private async void CaptureShortcut(HelideAction action)
    {
        _capturingShortcut = action;
        var defaults = string.Join(" or ", action.Defaults);
        await ShowDialogAsync(new DialogRequest(
            "Change Shortcut",
            $"Press the new shortcut for {action.Title}.\n\nEsc cancels. Backspace puts back the default, {defaults}.",
            "Cancel"));

        // However the card went -- a key, or its button -- nothing is being asked for now.
        _capturingShortcut = null;
    }

    // Runs ahead of everything else in the key handler while a shortcut is being asked
    // for, so the keys pressed are recorded rather than acted on.
    private bool HandleShortcutCapture(Key key, ModifierKeys modifiers)
    {
        if (_capturingShortcut is not { } action)
            return false;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            EndCapture();
            return true;
        }

        if (key == Key.Back && modifiers == ModifierKeys.None)
        {
            _state.KeyBindings = HelideKeymap.Reset(action.Id);
            _stateStore.Save(_state);
            EndCapture(reopen: true);
            return true;
        }

        if (HelideKeymap.Gesture(key, modifiers) is not { } gesture)
            return true; // A modifier on its own: still waiting for the key.

        // A key without Ctrl or Alt would be taken from typing in every pane.
        var functionKey = key is >= Key.F1 and <= Key.F24;
        if (!functionKey && !modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt))
            return true;

        EndCapture();

        if (HelideKeymap.Owner(gesture, action.Id) is { } owner)
        {
            ShowNotice("Change Shortcut", $"{gesture} is already {owner.Title}. Change that one first, or pick another.");
            return true;
        }

        _state.KeyBindings = HelideKeymap.Set(action.Id, gesture);
        _stateStore.Save(_state);
        Dispatcher.BeginInvoke(ShowKeybindings);
        return true;
    }

    private void EndCapture(bool reopen = false)
    {
        _capturingShortcut = null;
        CompleteDialog(false, null, false);

        if (reopen)
            Dispatcher.BeginInvoke(ShowKeybindings);
    }
}
