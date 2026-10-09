using System.Diagnostics;
using System.Windows;
using System.Reflection;

namespace Helide;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {DescribeVersion()}";
        Loaded += (_, _) =>
        {
            // Past the constructor on purpose: the Owner is assigned after it, and the
            // subscription has to outlive this window being reopened for a second look.
            SyncWithUpdateState();
            HelideUpdateState.Changed += OnUpdateStateChanged;
        };
        Closed += (_, _) => HelideUpdateState.Changed -= OnUpdateStateChanged;
    }

    private void OnUpdateStateChanged() => SyncWithUpdateState();

    // The pill and this button read the same place, so opening About mid-download shows
    // the same thing rather than falling back to a stale "Check for Updates" -- which
    // was both wrong to read and, if clicked, a second download of the same exe.
    private void SyncWithUpdateState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(SyncWithUpdateState);
            return;
        }

        if (HelideUpdateState.IsReadyToRestart)
        {
            UpdateButton.Content = "restart to update";
            UpdateButton.IsEnabled = true;
        }
        else if (HelideUpdateState.IsInProgress)
        {
            UpdateButton.Content = HelideUpdateState.Status;
            UpdateButton.IsEnabled = false;
        }
        else
        {
            UpdateButton.Content = "Check for Updates";
            UpdateButton.IsEnabled = true;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();

    private void GitHubLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/ujjwalvivek/helide")
        { UseShellExecute = true });

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (HelideUpdateState.IsReadyToRestart)
        {
            try { HelideUpdateState.Restart(); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to restart Helide: {ex.Message}", "Restart Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            return;
        }

        // A download is already running somewhere -- starting another one would waste
        // the bandwidth on the same exe twice.
        if (HelideUpdateState.IsInProgress)
        {
            SyncWithUpdateState();
            return;
        }

        if (UpdateButton.Content.ToString() != "Check for Updates") return;

        UpdateButton.IsEnabled = false;
        try
        {
            var updater = new HelideUpdater();
            bool hasUpdate = await updater.CheckAsync();
            if (!hasUpdate)
            {
                HelideUpdateState.Clear();
                SyncWithUpdateState();
                return;
            }

            HelideUpdateState.SetDownloading();
            await updater.DownloadAndApplyAsync(pct => HelideUpdateState.SetProgress(pct));

            // This window is the other way an update can land on disk, so it has to
            // record the same state the silent download did -- otherwise the shared
            // restart path would restart with no project and the workspace is lost.
            var owner = Owner as MainWindow;
            var projectPath = owner?.CurrentProject;
            if (string.IsNullOrEmpty(projectPath))
                projectPath = owner?.LastProjectPathFromState ?? HelideUpdateState.PendingProjectPath;
            HelideUpdateState.MarkUpdateApplied(projectPath);
        }
        catch (Exception ex)
        {
            HelideUpdateState.Clear();
            UpdateButton.Content = $"failed: {ex.Message}";
            UpdateButton.IsEnabled = false;
            await System.Threading.Tasks.Task.Delay(3000);
            SyncWithUpdateState();
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
