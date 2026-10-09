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
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Checking...";

        try
        {
            var updater = new HelideUpdater();
            bool hasUpdate = await updater.CheckAsync(s =>
                Dispatcher.Invoke(() => UpdateButton.Content = s));

            if (hasUpdate)
            {
                var msg = $"New version v{updater.LatestAvailable} is available.\n\nDownload and install now?\nApp will restart after install.";
                var result = System.Windows.MessageBox.Show(msg, "Update Available", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                {
                    await updater.DownloadAndApplyAsync(pct =>
                    {
                        Dispatcher.Invoke(() => UpdateButton.Content = $"Installing... {pct}%");
                    });
                }
                else
                {
                    Dispatcher.Invoke(() => UpdateButton.Content = $"Update: v{updater.LatestAvailable}");
                }
            }
            else
            {
                Dispatcher.Invoke(() => UpdateButton.Content = "Up to date");
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => UpdateButton.Content = "Check failed");
        }

        await System.Threading.Tasks.Task.Delay(2500);
        Dispatcher.Invoke(() =>
        {
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Check for Updates";
        });
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
