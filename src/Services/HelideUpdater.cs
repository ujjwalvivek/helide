using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Helide;

/// <summary>
/// Helide Auto-Updater - checks remote server, downloads, installs, restarts.
/// Used by AboutWindow (manual check) and by app startup (auto check).
/// </summary>
public class HelideUpdater
{
    private static readonly HttpClient Client = new();

    // Real GitHub releases - no placeholders
    private const string ReleasesApi = "https://api.github.com/repos/ujjwalvivek/helide/releases/latest";
    private const string DownloadBaseUrl = "https://github.com/ujjwalvivek/helide/releases/latest/download";
    private readonly string _currentVer;
    private readonly string _exePath;

    public string LatestAvailable { get; private set; } = "";

    public HelideUpdater()
    {
        _currentVer = GetCurrentVersion();
        _exePath = Process.GetCurrentProcess().MainModule.FileName;
    }

    private static string GetCurrentVersion()
    {
        try
        {
            var attr = typeof(HelideUpdater).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var info = attr?.InformationalVersion ?? "1.0.0";
            var idx = info.IndexOf('+');
            return idx >= 0 ? info[..idx] : info;
        }
        catch { return "1.0.0"; }
    }

    /// <summary>Returns true if a newer version exists on GitHub releases.</summary>
    public async Task<bool> CheckAsync(Action<string>? status = null)
    {
        status?.Invoke("Checking GitHub releases...");
        try
        {
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Helide-Updater/1.0");
            var json = await Client.GetStringAsync(ReleasesApi);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            if (!string.IsNullOrEmpty(tagName))
            {
                var tagStr = tagName.StartsWith("v", System.StringComparison.OrdinalIgnoreCase)
                    ? tagName.Substring(1) : tagName;

                var remote = new Version(tagStr);
                var current = new Version(_currentVer);

                if (remote > current)
                {
                    LatestAvailable = tagStr;
                    status?.Invoke($"Update available: v{tagStr}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            status?.Invoke($"Check failed: {ex.Message}");
        }
        status?.Invoke($"Up to date (v{_currentVer}).");
        return false;
    }

    /// <summary>Download and install the update from GitHub releases, then restart Helide.</summary>
    public async Task DownloadAndApplyAsync(Action<int>? progress = null)
    {
        try
        {
            // Fetch release info to find the actual asset URL
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("Helide-Updater/1.0");
            var releaseJson = await Client.GetStringAsync(ReleasesApi);
            using var doc = System.Text.Json.JsonDocument.Parse(releaseJson);
            var root = doc.RootElement;
            var assets = root.GetProperty("assets").EnumerateArray();
            string? downloadUrl = null;

            // Look for zip or exe asset
            foreach (var asset in assets)
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            // Fallback pattern if no asset found
            if (string.IsNullOrEmpty(downloadUrl))
                downloadUrl = $"{DownloadBaseUrl}/Helide-win-x64.exe";

        var tempDir = Path.Combine(Path.GetTempPath(), "helide-update");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"helide-{LatestAvailable}.exe");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(downloadUrl));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        long downloaded = 0;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var fileStream = File.Create(tempFile);
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;
            if (total > 0) progress?.Invoke((int)(downloaded * 100 / total));
        }

        progress?.Invoke(100);

        // Handle zip download: extract .exe, then install
        if (downloadUrl != null && downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(tempDir, "extract");
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, extractDir);
            var files = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                File.Copy(files[0], _exePath, overwrite: true);
            }
        }
        else if (downloadUrl != null && downloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(tempFile, _exePath, overwrite: true);
        }

        // Replace current exe and restart
        Process.Start(new ProcessStartInfo(_exePath)
        { UseShellExecute = true });
        Environment.Exit(0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update failed: {ex.Message}");
        }
    }
}

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
}
