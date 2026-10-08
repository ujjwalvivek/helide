using System.Diagnostics;
using System.Windows;
using System.Reflection;

namespace Helide;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // The SDK fills this in from <Version> in the csproj, so it is the one number
        // that cannot go stale. Hardcoding it in XAML is how v1.3.0 shipped still
        // advertising 1.2.0.
        VersionText.Text = $"Version {DescribeVersion()}";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();

    private void GitHubLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/ujjwalvivek/helide")
        {
            UseShellExecute = true,
        });

    private void UpdateButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/ujjwalvivek/helide/releases/latest")
        {
            UseShellExecute = true,
        });

        // Informational rather than AssemblyVersion: the SDK appends "+<commit sha>" to it
        // by default, which the About box does not want, so the build metadata is stripped.
    private static string DescribeVersion()
    {
        var informational = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        
        if (string.IsNullOrEmpty(informational))
            return "unknown";
        
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
