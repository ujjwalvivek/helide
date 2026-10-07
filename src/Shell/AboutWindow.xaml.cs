using System.Diagnostics;
using System.Windows;

namespace Helide;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
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
}
