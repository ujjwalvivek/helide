using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Helide.Theme;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
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
    private readonly ObservableCollection<PaletteCommand> _visible = [];
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

        _visible.Clear();
        foreach (var command in _all)
        {
            if (command.CanInvoke?.Invoke() == false)
                continue;

            if (FuzzyMatch.ScoreCommand(command.Title, command.Keywords, query) <= 0)
                continue;

            _visible.Add(command);
        }

        // Order only once something has been typed. With an empty query the declared
        // order is the useful one -- grouping still reads, and no reordering is needed.
        if (!string.IsNullOrWhiteSpace(query))
        {
            var ordered = _visible
                .Select(c => (Command: c, Score: FuzzyMatch.ScoreCommand(c.Title, c.Keywords, query)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Command.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Command)
                .ToList();

            _visible.Clear();
            foreach (var command in ordered)
                _visible.Add(command);
        }

        ResultsList.Visibility = _visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _visible.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        // Land on the first row so Enter always does the obvious thing.
        if (_visible.Count > 0)
            ResultsList.SelectedIndex = 0;
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => Refilter();

    /// <summary>
    /// Double-click runs a command. Single click only selects, which is what the
    /// ListBox already does -- binding invocation to a single click would fire commands
    /// while the pointer was still travelling toward the row.
    /// </summary>
    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        InvokeSelected();
        e.Handled = true;
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

        var next = ResultsList.SelectedIndex + delta;
        if (next < 0)
            next = _visible.Count - 1;
        else if (next >= _visible.Count)
            next = 0;

        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(_visible[next]);
    }

    private void InvokeSelected()
    {
        if (ResultsList.SelectedItem is not PaletteCommand command)
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
