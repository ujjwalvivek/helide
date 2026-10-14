using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Helide.Shell;

/// <summary>What an in-app dialog says, asks, and offers.</summary>
/// <param name="Title">The header line.</param>
/// <param name="Message">The body. Long text scrolls.</param>
/// <param name="PrimaryText">The answer being asked for, which Enter gives.</param>
/// <param name="SecondaryText">The way out, which Esc gives. Null for a plain notice.</param>
/// <param name="Danger">Draws the primary answer in the danger colour.</param>
/// <param name="Monospace">For output that has to keep its columns, such as git's.</param>
/// <param name="InputPlaceholder">Shows a text box with this hint when not null.</param>
/// <param name="InputText">What the text box starts with.</param>
/// <param name="SuppressKey">
/// Offers "don't ask again" under this key when not null. Only for dialogs that are safe
/// to stop showing: a failure that has to be read should not carry one.
/// </param>
/// <param name="LinkLabel">A quiet label under the text box, such as "Runs in".</param>
/// <param name="LinkText">
/// A link after the label. Choosing it ends the card with <c>Link</c> set and the text
/// typed so far, so the caller can ask something else and show the card again.
/// </param>
public sealed record DialogRequest(
    string Title,
    string Message,
    string PrimaryText = "OK",
    string? SecondaryText = null,
    bool Danger = false,
    bool Monospace = false,
    string? InputPlaceholder = null,
    string? InputText = null,
    string? SuppressKey = null,
    string? LinkLabel = null,
    string? LinkText = null);

/// <summary>
/// A message, a question or a one-line prompt, drawn inside the window.
/// </summary>
/// <remarks>
/// Helide does not use the system's message boxes or dialogs: they are a different
/// application's chrome in the middle of this one, they take focus away from the
/// terminals, and they cannot be themed. This is the one surface that replaces all of
/// them, so a notice, a confirmation and a prompt for a name are the same card.
///
/// It owns no keyboard handling of its own beyond the buttons: the window's key handler
/// calls <see cref="Accept"/> and <see cref="Cancel"/>, for the reason it owns the
/// palette's keys -- a terminal pane can still hold the OS-level keyboard.
/// </remarks>
public partial class AppDialog : UserControl
{
    public AppDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised once, with whether the primary answer was given, any text typed, and whether
    /// "don't ask again" was ticked.
    /// </summary>
    public event Action<bool, string?, bool>? Finished;

    /// <summary>Raised when the link is chosen, with the text typed so far.</summary>
    public event Action<string?>? LinkChosen;

    /// <summary>Whether the card has a text box.</summary>
    public bool HasInput => InputFrame.Visibility == Visibility.Visible;

    public void Show(DialogRequest request)
    {
        TitleText.Text = request.Title;

        MessageText.Text = request.Message;
        MessageText.Visibility = request.Message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        MessageText.SetResourceReference(
            TextBlock.FontFamilyProperty,
            request.Monospace ? "UiMonoFontFamily" : "UiFontFamily");
        MessageText.SetResourceReference(
            TextBlock.FontSizeProperty,
            request.Monospace ? "FontSizeSmall" : "FontSizeBody");

        PrimaryButton.Content = request.PrimaryText;
        PrimaryButton.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            request.Danger ? "DangerBrush" : "AccentBrush");

        SecondaryButton.Content = request.SecondaryText;
        SecondaryButton.Visibility = request.SecondaryText is null ? Visibility.Collapsed : Visibility.Visible;

        EnterHint.Text = request.PrimaryText.ToLowerInvariant();
        EscHint.Text = request.SecondaryText?.ToLowerInvariant() ?? "close";

        InputFrame.Visibility = request.InputPlaceholder is null ? Visibility.Collapsed : Visibility.Visible;
        InputPlaceholder.Text = request.InputPlaceholder ?? string.Empty;
        InputBox.Text = request.InputText ?? string.Empty;

        LinkRow.Visibility = request.LinkText is null ? Visibility.Collapsed : Visibility.Visible;
        LinkLabelText.Text = request.LinkLabel ?? string.Empty;
        LinkButton.Content = request.LinkText ?? string.Empty;

        SuppressCheck.IsChecked = false;
        SuppressCheck.Visibility = request.SuppressKey is null ? Visibility.Collapsed : Visibility.Visible;
        SuppressCheck.Content = request.SecondaryText is null ? "Don't show this again" : "Don't ask again";
    }

    /// <summary>Ticks or clears "don't ask again", for the keyboard.</summary>
    public void ToggleSuppress()
    {
        if (SuppressCheck.Visibility == Visibility.Visible)
            SuppressCheck.IsChecked = SuppressCheck.IsChecked != true;
    }

    /// <summary>Gives the primary answer, with whatever the text box holds.</summary>
    public void Accept() => Finished?.Invoke(
        true,
        HasInput ? InputBox.Text : null,
        SuppressCheck.Visibility == Visibility.Visible && SuppressCheck.IsChecked == true);

    /// <summary>Gives the way out.</summary>
    public void Cancel() => Finished?.Invoke(false, null, false);

    /// <summary>
    /// Appends a typed character. Handed over by the window when a pane still owns the
    /// keyboard, as the palette's is.
    /// </summary>
    public void AppendText(string text)
    {
        if (HasInput)
            InputBox.AppendText(text);
    }

    /// <summary>Removes the last typed character, for the same case.</summary>
    public void Backspace()
    {
        if (HasInput && InputBox.Text.Length > 0)
            InputBox.Text = InputBox.Text[..^1];
    }

    /// <summary>The text box when there is one, otherwise the answer.</summary>
    public void FocusDefault()
    {
        if (HasInput)
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        }
        else
        {
            PrimaryButton.Focus();
        }
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e) => Accept();

    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => Cancel();

    private void LinkButton_Click(object sender, RoutedEventArgs e) => LinkChosen?.Invoke(HasInput ? InputBox.Text : null);
}
