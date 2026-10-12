using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Helide.Shell;

/// <summary>
/// What a pane shows in place of a tool that is not installed.
/// </summary>
/// <remarks>
/// A pane whose executable cannot be found does not fail: the terminal sits on its
/// "Starting…" placeholder for good, which reads as the app being slow. This takes that
/// pane's place instead, says what is missing and why it matters, and offers the install.
/// It follows the shared install state, so a card opened while an install is already
/// running shows that, and resolves itself when the tool turns up.
/// </remarks>
public partial class MissingToolCard : UserControl
{
    private readonly ManagedTool _tool;
    private bool _resolved;

    internal MissingToolCard(ManagedTool tool, string purpose)
    {
        InitializeComponent();
        _tool = tool;

        TitleText.Text = $"{tool.Label} is not installed";
        BodyText.Text = purpose;
        CommandText.Text = $"winget install -e --id {tool.WingetId}";

        Loaded += (_, _) =>
        {
            ToolLocator.InstallStateChanged += OnInstallStateChanged;
            Refresh();
        };
        Unloaded += (_, _) => ToolLocator.InstallStateChanged -= OnInstallStateChanged;

        Refresh();
    }

    /// <summary>
    /// Raised once, when the tool has been found. The owner puts the real pane in this
    /// one's place.
    /// </summary>
    internal event Action? Resolved;

    private void OnInstallStateChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        if (_resolved)
            return;

        if (ToolLocator.IsInstalled(_tool))
        {
            Resolve();
            return;
        }

        var installing = ToolLocator.IsInstalling;
        InstallButton.IsEnabled = !installing;
        RecheckButton.IsEnabled = !installing;
        InstallButton.Content = installing
            ? $"Installing {(ToolLocator.Current ?? _tool).Label}…"
            : $"Install {_tool.Label}";
    }

    private void Resolve()
    {
        if (_resolved)
            return;

        // Found by a route this process's PATH does not know about, so it is added
        // before the pane is built: the pane launches the tool by bare name.
        ToolLocator.SyncProcessPath();

        _resolved = true;
        Resolved?.Invoke();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Visibility = Visibility.Collapsed;

        await ToolLocator.InstallAsync([_tool]);

        if (ToolLocator.IsInstalled(_tool))
        {
            Resolve();
            return;
        }

        StatusText.Text = ToolLocator.WingetMissing
            ? "winget is not available on this machine. Run the command above from a shell where it is."
            : $"{_tool.Label} could not be installed. Run the command above yourself, then check again.";
        StatusText.Visibility = Visibility.Visible;
        InstallButton.Content = "Try again";
        Refresh();
    }

    // For a tool installed by hand, or by something other than this card.
    private void RecheckButton_Click(object sender, RoutedEventArgs e)
    {
        ToolLocator.SyncProcessPath();

        if (ToolLocator.IsInstalled(_tool))
        {
            Resolve();
            return;
        }

        StatusText.Text = $"{_tool.Label} is still not found. A new shell may be needed for PATH to update.";
        StatusText.Visibility = Visibility.Visible;
    }
}
