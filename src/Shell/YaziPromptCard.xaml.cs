using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Helide.Shell;

/// <summary>
/// The offer to let the project browser open files in Helide's editor.
/// </summary>
/// <remarks>
/// Lives in the project browser's own pane, and the browser is not started behind it
/// until the question is answered -- so the pane is never a working file manager that
/// quietly opens files the old way. Deliberately never expires: the offer is driven by
/// the config file's absence rather than by anything remembered here, so being asked
/// once and losing the chance to answer is worse than being asked again on the next
/// launch.
/// </remarks>
public partial class YaziPromptCard : UserControl
{
    public YaziPromptCard()
    {
        InitializeComponent();
        // Shown rather than written into the XAML so the path always matches the folder
        // the installer actually writes to.
        PathText.Text = $"config goes to {Helide.YaziConfig.ConfigDirectory}";
    }

    /// <summary>Raised when the user takes the offer.</summary>
    public event Action? Accepted;

    /// <summary>
    /// Raised when the user turns it down or dismisses it. Both are the same answer:
    /// nothing is installed and the browser simply starts.
    /// </summary>
    public event Action? Declined;

    private void AcceptButton_Click(object sender, RoutedEventArgs e) => Accepted?.Invoke();

    private void DeclineButton_Click(object sender, RoutedEventArgs e) => Declined?.Invoke();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Declined?.Invoke();
}
