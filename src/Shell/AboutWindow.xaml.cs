using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Reflection;

namespace Helide;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {DescribeVersion()}";
        Loaded += (_, _) => SyncWithMainWindow();
    }

    // The pill and this button must always read the same. Without this, opening About
    // after a silent download shows "Check for Updates" and clicking it re-downloads the
    // very exe that was just installed, which wastes the download and confuses the user.
    private void SyncWithMainWindow()
    {
        var owner = Window.GetWindow(this) as MainWindow;
        if (owner is null) return;

        if (owner.IsUpdateReadyToRestart)
        {
            UpdateButton.Content = "restart to update";
            UpdateButton.IsEnabled = true;
        }
        else if (UpdateButton.Content.ToString() == "restart to update")
        {
            UpdateButton.Content = "Check for Updates";
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();

    private void GitHubLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/ujjwalvivek/helide")
        { UseShellExecute = true });

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateButton.Content.ToString() == "Check for Updates")
        {
            UpdateButton.IsEnabled = false;
            UpdateButton.Content = "downloading";
            try
            {
                var updater = new HelideUpdater();
                bool hasUpdate = await updater.CheckAsync();
                if (hasUpdate)
                {
                    UpdateButton.Content = "installing";
                    await updater.DownloadAndApplyAsync(pct =>
                        Dispatcher.Invoke(() => UpdateButton.Content = $"installing ... {pct}%"));

                    // This download is the other entry point into the state the pill uses.
                    // Without this the button would offer a restart, but HelideUpdateState
                    // would still be empty and restarting would drop the workspace.
                    var owner = Window.GetWindow(this) as MainWindow;
                    var projectPathArg = owner?.CurrentProject;
                    if (string.IsNullOrEmpty(projectPathArg))
                        projectPathArg = owner?.LastProjectPathFromState;
                    HelideUpdateState.MarkUpdateApplied(projectPathArg);

                    SyncWithMainWindow();
                    if (UpdateButton.Content.ToString() != "restart to update")
                        UpdateButton.Content = "restart to update";
                    UpdateButton.IsEnabled = true;
                    return;
                }
                SyncWithMainWindow();
            }
            catch (Exception ex)
            {
                UpdateButton.Content = $"failed: {ex.Message}";
                await System.Threading.Tasks.Task.Delay(3000);
                SyncWithMainWindow();
            }
            if (UpdateButton.Content.ToString() != "restart to update")
            {
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = "Check for Updates";
            }
        }
        else if (UpdateButton.Content.ToString() == "restart to update")
        {
            try { new HelideUpdater().RestartApp(HelideUpdateState.PendingProjectPath); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to restart Helide: {ex.Message}", "Restart Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }

    private static string DescribeVersion()
    {
        var informational = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return "unknown";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
