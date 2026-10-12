using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Helide.Shell;

/// <summary>
/// What an OpenCode tab shows instead of OpenCode while the installed one is version 1.
/// </summary>
/// <remarks>
/// Sits in the agent pane the way the yazi offer sits in the browser's: the terminal
/// behind it is not started until the question is settled, so the pane is never an
/// agent that half-works. One card serves every OpenCode tab, since they are all
/// waiting on the same thing.
/// </remarks>
public partial class OpenCodeUpdateCard : UserControl
{
    public OpenCodeUpdateCard()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user types <c>/update</c>.</summary>
    public event Action? UpdateRequested;

    /// <summary>Raised when the user asks for the installed version to be looked at again.</summary>
    public event Action? CheckRequested;

    /// <summary>Says what is happening under the command line, or clears it.</summary>
    public void SetStatus(string? text)
    {
        StatusText.Text = text ?? string.Empty;
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The command <c>/update</c> will run, shown so it is no surprise.</summary>
    public void SetCommand(string command) => CommandText.Text = command;

    public void FocusInput() => InputBox.Focus();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;

        var typed = InputBox.Text.Trim();
        InputBox.Clear();

        // Enter on an empty line is "look again", which is what it does in a shell
        // that has nothing to run.
        if (typed.Length == 0)
            CheckRequested?.Invoke();
        else if (string.Equals(typed, "/update", StringComparison.OrdinalIgnoreCase))
            UpdateRequested?.Invoke();
        else
            SetStatus($"\"{typed}\" is not something this pane can run. Type /update to update OpenCode.");
    }

    private void CheckButton_Click(object sender, RoutedEventArgs e) => CheckRequested?.Invoke();
}
