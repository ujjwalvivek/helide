using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Helide.Theme;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Helide.Shell;

/// <summary>
/// The overlay behind the command palette: a filterable list driven entirely from the
/// keyboard.
/// </summary>
/// <remarks>
/// Keyboard only, deliberately. The point of a palette is that you type what you want
/// and hit Enter, so the mouse path is not part of the contract. Selection follows the
/// query rather than being set once at open, because the list re-filters on every
/// keystroke and a selection index left over from the previous query points at an
/// unrelated row.
/// </remarks>
public partial class CommandPalette : UserControl
{
    private readonly ObservableCollection<PaletteRow> _visible = [];
    private IReadOnlyList<PaletteCommand> _all = [];

    public CommandPalette()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _visible;
    }

    /// <summary>Raised when the palette wants to close, whatever the reason.</summary>
    public event Action? Dismissed;

    /// <summary>Raised with the chosen command, before the palette closes.</summary>
    public event Action<PaletteCommand>? Chosen;

    /// <summary>
    /// Replaces the command set and re-runs the current query. Called on every open so
    /// the list reflects what exists right now rather than what existed last time.
    /// </summary>
    public void Load(IReadOnlyList<PaletteCommand> commands)
    {
        _all = commands;
        QueryBox.Text = string.Empty;
        Refilter();
    }

    private void Refilter()
    {
        var query = QueryBox.Text;

        var matched = _all
            .Where(command => command.CanInvoke?.Invoke() != false)
            .Where(command => FuzzyMatch.ScoreCommand(command.Title, command.Keywords, query) > 0)
            .ToList();

        // Order only once something has been typed. With an empty query the declared
        // order is the useful one -- grouping still reads, and no reordering is needed.
        if (!string.IsNullOrWhiteSpace(query))
        {
            matched = matched
                .OrderByDescending(c => FuzzyMatch.ScoreCommand(c.Title, c.Keywords, query))
                .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        _visible.Clear();

        // A heading is emitted only when it introduces something, so a filtered list
        // never carries a row of empty categories above the results.
        var currentGroup = string.Empty;
        foreach (var command in matched)
        {
            var group = command.Group ?? string.Empty;
            if (!string.Equals(group, currentGroup, StringComparison.Ordinal))
            {
                if (group.Length > 0)
                {
                    _visible.Add(PaletteRow.Heading(group));
                    currentGroup = group;
                }
            }

            _visible.Add(PaletteRow.For(command));
        }

        ResultsList.Visibility = _visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _visible.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        // Land on the first command rather than the first row, so Enter runs something
        // instead of trying to run a heading.
        SelectFirstCommand();
    }

    private void SelectFirstCommand()
    {
        for (var i = 0; i < _visible.Count; i++)
        {
            if (_visible[i].IsHeading)
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
    /// palette navigation: when the overlay was opened from a pane, the query box never
    /// received these keys at all, so relying on its own handler left the list stuck.
    /// </summary>
    public void MoveSelection(int delta) => Move(delta);

    /// <summary>Runs the selected command. Public for the same reason.</summary>
    public void InvokeCurrent() => InvokeSelected();

    /// <summary>
    /// Appends a typed character. Used when a pane underneath still owns real keyboard
    /// focus: the query box looks focused as far as WPF is concerned, yet no keystroke
    /// ever reaches it, so characters have to be handed over explicitly.
    /// </summary>
    public void AppendText(string text) => QueryBox.AppendText(text);

    /// <summary>
    /// A click runs the command. Single click rather than double, because selecting is
    /// not a useful outcome on its own here: a palette exists to be used with one
    /// gesture, and with the selection indicator being the only feedback, a click that
    /// only selected read as a click that did nothing.
    /// </summary>
    private void Results_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Clicks that miss a row -- the scrollbar, or the list padding -- must not run
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
                Move(1);
                break;

            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;

            case Key.Tab:
                // Tab would leave the query box for the list, stranding the caret. Both
                // directions walk the list instead, which is what a palette should do.
                e.Handled = true;
                Move(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
        }
    }

    private void Move(int delta)
    {
        if (_visible.Count == 0)
            return;

        // Skips headings in both directions, so arrowing never stops on a row that
        // cannot be run.
        var index = ResultsList.SelectedIndex;
        for (var step = 0; step < _visible.Count; step++)
        {
            index += delta;
            if (index < 0)
                index = _visible.Count - 1;
            else if (index >= _visible.Count)
                index = 0;

            if (_visible[index].IsHeading)
                continue;

            ResultsList.SelectedIndex = index;
            ResultsList.ScrollIntoView(_visible[index]);
            return;
        }
    }

    private void InvokeSelected()
    {
        if (ResultsList.SelectedItem is not PaletteRow { Command: { } command })
            return;

        Chosen?.Invoke(command);
        Dismiss();
    }

    private void Dismiss() => Dismissed?.Invoke();

    /// <summary>
    /// Focuses the query box and clears it. Separate from <see cref="Load"/> because the
    /// caret has to land here too, and Load is called before the popup is shown.
    /// </summary>
    public void FocusQuery() => QueryBox.Focus();
}
