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
                    UpdateButton.Content = "restart to update";
                    UpdateButton.IsEnabled = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                UpdateButton.Content = $"failed: {ex.Message}";
                await System.Threading.Tasks.Task.Delay(3000);
            }
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Check for Updates";
        }
        else if (UpdateButton.Content.ToString() == "restart to update")
        {
            try { new HelideUpdater().RestartApp(); }
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
