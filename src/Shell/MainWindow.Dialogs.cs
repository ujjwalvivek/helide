using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Helide.Shell;

namespace Helide;

/// <summary>How an in-app dialog was answered.</summary>
internal readonly record struct DialogOutcome(bool Accepted, string? Text, bool Suppress = false);

// Everything that stands in for a system dialog. Helide shows none of the system's own:
// a message, a question, a prompt for a name and the folder browser are all drawn inside
// the window, so nothing takes focus from a terminal or arrives in another
// application's chrome.
public partial class MainWindow
{
    private TaskCompletionSource<DialogOutcome>? _dialogCompletion;
    private bool _dialogOpen;

    // ---- Dialog card -------------------------------------------------------------

    // Asks, and waits for the answer. One card at a time: a second request queues behind
    // the first rather than replacing it, so a notice raised while a confirmation is up
    // is shown after it instead of silently answering it.
    private async Task<DialogOutcome> ShowDialogAsync(DialogRequest request)
    {
        // Ticked before: a confirmation is answered yes without being shown, which is
        // what "don't ask again" promised, and a notice is simply not raised.
        if (request.SuppressKey is { } key && _state.HiddenDialogs.Contains(key))
            return new DialogOutcome(true, null);

        while (_dialogCompletion is { } pending)
            await pending.Task;

        var completion = new TaskCompletionSource<DialogOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogCompletion = completion;
        _dialogOpen = true;

        DialogControl.Show(request);
        DialogPopup.PlacementTarget = RootGrid;
        DialogPopup.IsOpen = true;

        var outcome = await completion.Task;

        // Only an "OK" is remembered. Ticking the box and then cancelling is not a
        // standing answer of "no" -- the card would never appear again to take it back.
        if (outcome.Accepted && outcome.Suppress && request.SuppressKey is { } suppress
            && !_state.HiddenDialogs.Contains(suppress))
        {
            _state.HiddenDialogs.Add(suppress);
            _stateStore.Save(_state);
        }

        return outcome;
    }

    // Raised by the card's buttons and by the popup closing underneath it -- an outside
    // click, or another application coming forward. Both mean an answer, and the second
    // one is "no".
    private void CompleteDialog(bool accepted, string? text, bool suppress)
    {
        if (_dialogCompletion is not { } completion)
            return;

        _dialogCompletion = null;
        _dialogOpen = false;
        DialogPopup.IsOpen = false;

        completion.TrySetResult(new DialogOutcome(accepted, text, suppress));
    }

    private void DialogPopup_Opened(object sender, EventArgs e)
    {
        DialogControl.FocusDefault();

        // WPF focus alone is not enough when a pane held the keyboard: see the same
        // handoff in the palette's Opened handler.
        if (PresentationSource.FromVisual(DialogControl) is HwndSource source)
            NativeMethods.SetFocus(source.Handle);
    }

    private bool DialogHasOsFocus()
    {
        if (PresentationSource.FromVisual(DialogControl) is not HwndSource source)
            return false;

        return NativeMethods.GetFocus() == source.Handle;
    }

    // Not awaited by callers: a notice has nothing to wait for.
    private void ShowNotice(string title, string message, bool monospace = false, string? suppressKey = null) =>
        _ = ShowDialogAsync(new DialogRequest(title, message, "OK", Monospace: monospace, SuppressKey: suppressKey));

    private async Task<bool> ConfirmAsync(
        string title, string message, string confirmText, bool danger = false, string? suppressKey = null)
    {
        var outcome = await ShowDialogAsync(
            new DialogRequest(title, message, confirmText, "Cancel", danger, SuppressKey: suppressKey));

        return outcome.Accepted;
    }

    // Null when cancelled, or when nothing was typed.
    private async Task<string?> PromptAsync(string title, string message, string placeholder, string confirmText)
    {
        var outcome = await ShowDialogAsync(
            new DialogRequest(title, message, confirmText, "Cancel", InputPlaceholder: placeholder));

        return outcome.Accepted && !string.IsNullOrWhiteSpace(outcome.Text) ? outcome.Text.Trim() : null;
    }

    // ---- Folder browser ------------------------------------------------------------

    // Walks the file system one folder at a time in the palette's own layout, in place of
    // the system's folder dialog. Choosing a folder row goes into it, which re-opens the
    // list on that folder; the first row picks the one being looked at, and ".." goes up,
    // as far as the list of drives.
    //
    // It re-opens rather than updating in place because the palette's contract is that a
    // choice runs and the overlay goes away -- see CommandPalette_Chosen. The list is
    // rebuilt in the same dispatcher turn, so nothing is seen to close.
    private void BrowseFolders(string? start, Action<string> pick)
    {
        // A start that has since gone falls back to the nearest parent that has not.
        while (start is not null && !Directory.Exists(start))
            start = Path.GetDirectoryName(start);

        var commands = new List<PaletteCommand>();
        string placeholder;

        if (start is null)
        {
            placeholder = "This PC";

            foreach (var drive in DriveInfo.GetDrives())
            {
                string root;
                string label;
                try
                {
                    if (!drive.IsReady)
                        continue;

                    root = drive.RootDirectory.FullName;
                    label = drive.VolumeLabel;
                }
                catch (IOException)
                {
                    continue;
                }

                commands.Add(new PaletteCommand(
                    root,
                    () => BrowseFolders(root, pick),
                    "Folder",
                    string.IsNullOrWhiteSpace(label) ? null : label));
            }
        }
        else
        {
            var folder = start;
            var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            placeholder = folder;

            commands.Add(new PaletteCommand(
                $"Select {(name.Length > 0 ? name : folder)}",
                () => pick(folder),
                "Select",
                "this folder",
                keywords: "choose use open here"));
            commands.Add(new PaletteCommand(
                "New folder here…",
                () => _ = CreateFolderAndBrowseAsync(folder, pick),
                "Select",
                "creates a folder",
                keywords: "make add"));

            var parent = Path.GetDirectoryName(trimmed);
            commands.Add(new PaletteCommand(
                "..",
                () => BrowseFolders(parent, pick),
                "Up",
                parent ?? "This PC",
                keywords: "parent up back"));

            foreach (var child in Subfolders(folder))
            {
                var path = child;
                commands.Add(new PaletteCommand(
                    Path.GetFileName(path),
                    () => BrowseFolders(path, pick),
                    "Folder"));
            }
        }

        // "items": the list carries the select and go-up rows as well as the folders.
        ShowPalette(commands, new PaletteMode(placeholder, "item", "items", "open"));
    }

    // One unreadable folder costs its listing, not the browser. System and hidden
    // folders ($Recycle.Bin, System Volume Information) are left out: nothing is ever
    // meant to be picked in them.
    private static IEnumerable<string> Subfolders(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateDirectories()
                .Where(directory => (directory.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                .Select(directory => directory.FullName)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task CreateFolderAndBrowseAsync(string parent, Action<string> pick)
    {
        var name = await PromptAsync("New folder", $"Created in {parent}", "folder name", "Create");

        if (name is null)
        {
            BrowseFolders(parent, pick);
            return;
        }

        try
        {
            var created = Path.GetFullPath(Path.Combine(parent, name));

            // Anything with a separator in it would be made somewhere other than where
            // the list is looking, which is not what "here" says.
            if (!string.Equals(Path.GetDirectoryName(created)?.TrimEnd('\\'), Path.GetFullPath(parent).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                ShowNotice("New folder", "A folder name cannot contain a path.");
                BrowseFolders(parent, pick);
                return;
            }

            Directory.CreateDirectory(created);
            BrowseFolders(created, pick);
        }
        catch (Exception exception)
        {
            ShowNotice("Could not create the folder", exception.Message);
            BrowseFolders(parent, pick);
        }
    }
}
