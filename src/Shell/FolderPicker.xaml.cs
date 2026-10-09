using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Helide.Shell;

/// <summary>
/// The in-project counterpart of the folder dialog: lists the project's own
/// directories, project-relative and fuzzy-filtered, and picks one.
/// </summary>
/// <remarks>
/// The tree is scanned on a background thread and appended in batches, which is why
/// filtering re-runs as rows arrive rather than once at the end: the first rows are
/// useful within milliseconds and a deep project keeps filling in behind the query.
/// A generation counter guards that against a walk started by the previous open
/// still draining into a list that has since been cleared.
/// </remarks>
public partial class FolderPicker : UserControl
{
    private readonly ObservableCollection<FolderRow> _visible = [];
    private readonly List<FolderEntry> _all = [];
    private CancellationTokenSource? _scan;
    private int _generation;

    public FolderPicker()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _visible;
    }

    /// <summary>Raised when the picker closes without picking anything.</summary>
    public event Action? Dismissed;

    /// <summary>Raised with the picked folder's full path, before the picker closes.</summary>
    public event Action<string>? Picked;

    /// <summary>Raised when the pinned "Open another folder" row is chosen. The window owns the
    /// native dialog from there -- including closing this picker before showing it,
    /// which is the whole reason this is an event rather than the picker calling the
    /// dialog itself.
    /// </summary>
    public event Action? BrowseRequested;

    /// <summary>
    /// Raised when the pinned "Back to command palette" row is chosen. The window owns
    /// the palette this picker was opened from.
    /// </summary>
    public event Action? BackRequested;

    /// <summary>
    /// Scans the project and shows every folder under it, root first. Called on every
    /// open, so the tree reflects the project as it is right now -- a directory created
    /// since the last open shows up.
    /// </summary>
    public void Load(string projectRoot)
    {
        _scan?.Cancel();
        _scan?.Dispose();

        var generation = ++_generation;
        var scan = new CancellationTokenSource();
        _scan = scan;
        var token = scan.Token;

        _all.Clear();
        _visible.Clear();
        QueryBox.Text = string.Empty;

        // The root leads the list as ".". It is the session root every pane has used
        // until now, so it stays the default Enter -- filtering it out for not
        // matching "." is why it carries the project name as its match text.
        var rootName = Path.GetFileName(
            projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _all.Add(new FolderEntry(projectRoot, ".", 0, rootName));

        ScanText.Visibility = Visibility.Visible;
        Refilter();

        _ = Task.Run(() =>
        {
            try
            {
                FolderScan.Walk(projectRoot, batch => Marshal(generation, batch), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                // Only the walk that is still current clears its own note: a
                // cancelled walk finishing late must not hide the new one's.
                if (!token.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
                    Dispatcher.Invoke(() => ScanText.Visibility = Visibility.Collapsed);
            }
        }, token);
    }

    private void Marshal(int generation, IReadOnlyList<FolderEntry> batch)
    {
        // The walk outlives the popup in one case: closing the window mid-scan. Its
        // dispatcher is on its way down then, and invoking into it is not a recoverable
        // failure for a picker nobody is looking at.
        if (Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.Invoke(() =>
        {
            if (generation != _generation)
                return;

            _all.AddRange(batch);
            Refilter();
        });
    }

    private void Refilter()
    {
        // Forward slashes are what makes a path normal to type, and they are the one
        // character the matcher cannot flatten on its own.
        var query = QueryBox.Text.Trim().Replace('/', '\\');

        var matched = _all
            .Select(entry => (Entry: entry, Score: FuzzyMatch.Score(entry.MatchText, query)))
            .Where(pair => query.Length == 0 || pair.Score > 0);

        if (query.Length > 0)
            matched = matched
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Entry.Depth);

        _visible.Clear();

        // Pinned first, whatever the query: it reads as where you came from rather than
        // as part of the tree, and keeping it pinned means Esc is never the only way
        // out of a picker that fills the screen.
        _visible.Add(FolderRow.Back());

        foreach (var (entry, _) in matched)
            _visible.Add(new FolderRow(entry));

        var folders = _visible.Count - 1;

        // Pinned last whatever the query: the one folder the tree cannot show is one
        // outside the project, and that row is also the escape hatch when the query
        // matches nothing. With both rows the list is never empty, never collapsed.
        _visible.Add(FolderRow.Browse());

        EmptyText.Visibility = folders == 0 ? Visibility.Visible : Visibility.Collapsed;

        SelectFirst();
    }

    // The first folder, not the first row: the pinned rows are destinations, not defaults,
    // and Enter on a freshly opened picker should still pick the project root.
    private void SelectFirst()
    {
        for (var i = 0; i < _visible.Count; i++)
        {
            if (_visible[i].Kind != FolderRowKind.Folder)
                continue;

            ResultsList.SelectedIndex = i;
            ResultsList.ScrollIntoView(_visible[i]);
            return;
        }

        ResultsList.SelectedIndex = -1;
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => Refilter();

    /// <summary>
    /// Moves the selection by whole rows. Public because the window's key handler owns
    /// picker navigation, for the same reason it owns the palette's.
    /// </summary>
    public void MoveSelection(int delta)
    {
        if (_visible.Count <= 1)
            return;

        var index = ResultsList.SelectedIndex;
        index = (index + delta + _visible.Count) % _visible.Count;

        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(_visible[index]);
    }

    /// <summary>Picks the selected folder. Public for the same reason.</summary>
    public void InvokeCurrent() => InvokeSelected();

    /// <summary>Appends a typed character, handed over when a pane still owns the keyboard.</summary>
    public void AppendText(string text) => QueryBox.AppendText(text);

    /// <summary>
    /// A click picks the folder, matching the palette's single-gesture rule: selecting
    /// is not a useful outcome here on its own.
    /// </summary>
    private void Results_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Clicks that miss a row -- the scrollbar, the list padding -- must not pick
        // whatever happens to be selected.
        if (Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null)
            return;

        InvokeSelected();
        e.Handled = true;
    }

    private static T? Ancestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var current = start; current is not null;)
        {
            if (current is T match)
                return match;

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Dismiss();
                break;

            case Key.Enter:
                e.Handled = true;
                InvokeSelected();
                break;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;

            case Key.Tab:
                e.Handled = true;
                MoveSelection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
        }
    }

    private void InvokeSelected()
    {
        if (ResultsList.SelectedItem is not FolderRow row)
            return;

        // The pinned row hands off to the window for the native browser. It stays open
        // until the window decides what to do: it is the one that has to close first,
        // since showing a parentless dialog under an open popup lets the dialog adopt
        // the popup's HWND as its owner, and the deactivation that closes the popup
        // then destroys its owner with it.
        if (row.IsBrowse)
        {
            BrowseRequested?.Invoke();
            return;
        }

        // The other pinned row goes back where the picker came from.
        if (row.IsBack)
        {
            BackRequested?.Invoke();
            return;
        }

        if (row.Entry is not { } entry)
            return;

        Picked?.Invoke(entry.FullPath);
        Dismiss();
    }

    private void Dismiss()
    {
        CancelWalk();
        Dismissed?.Invoke();
    }

    // A walk nobody is waiting for is exactly the one that holds a background thread
    // for seconds. Dismiss and an outside click both get here: the latter lands on the
    // popup rather than on this control, so its Closed handler cannot do this for it.
    private void CancelWalk()
    {
        _scan?.Cancel();
        _scan?.Dispose();
        _scan = null;
    }

    /// <summary>
    /// Focuses the query box. Separate from <see cref="Load"/> because the caret has to
    /// land here too, and Load is called before the popup is shown.
    /// </summary>
    public void FocusQuery() => QueryBox.Focus();

    // The popup unloads its content when it closes -- Esc, an outside click, a window
    // closing -- and none of those reach Dismiss. This is the one cancel point all of
    // them pass through.
    private void FolderPicker_Unloaded(object sender, RoutedEventArgs e) => CancelWalk();
}

/// <summary>What a rendered line is: part of the tree, or one of the two pinned rows.</summary>
internal enum FolderRowKind
{
    Folder,
    Back,
    Browse,
}

/// <summary>One rendered line: a folder, with its display text and indent.</summary>
internal sealed class FolderRow
{
    private FolderRow(FolderRowKind kind)
    {
        Kind = kind;
    }

    public FolderRow(FolderEntry entry)
    {
        Entry = entry;
        Kind = FolderRowKind.Folder;
    }

    public FolderRowKind Kind { get; }

    public FolderEntry? Entry { get; }

    /// <summary>True for the "Back to command palette" row, pinned above the tree.</summary>
    public bool IsBack => Kind == FolderRowKind.Back;

    /// <summary>True for the "Open another folder" row, pinned below the tree.</summary>
    public bool IsBrowse => Kind == FolderRowKind.Browse;

    /// <summary>The folder's own name -- the indent shows where it sits.</summary>
    public string Title => Kind switch
    {
        FolderRowKind.Back => "Back to command palette",
        FolderRowKind.Browse => "Open another folder",
        _ => Entry?.Name ?? string.Empty,
    };

    /// <summary>
    /// Project-relative path, shown as a tooltip: two folders in a large tree share a
    /// name more often than a glance at the indent can tell apart.
    /// </summary>
    public string Path => Entry?.RelativePath ?? Kind switch
    {
        FolderRowKind.Back => "Return to the command palette",
        FolderRowKind.Browse => "Browse for a folder outside this project",
        _ => string.Empty,
    };

    // The pinned rows get air instead of a divider, so they read as outside the tree
    // without a second kind of row in the list.
    public Thickness Indent => Kind switch
    {
        FolderRowKind.Back => new Thickness(0, 0, 0, 5),
        FolderRowKind.Browse => new Thickness(0, 7, 0, 3),
        _ => new Thickness(Entry!.Depth * 12, 0, 0, 0),
    };

    public static FolderRow Back() => new(FolderRowKind.Back);

    public static FolderRow Browse() => new(FolderRowKind.Browse);
}
