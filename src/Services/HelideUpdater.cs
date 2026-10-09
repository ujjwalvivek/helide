using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Helide;

public class HelideUpdater
{
    private static readonly HttpClient Client = new();
    private const string ReleasesApi = "https://api.github.com/repos/ujjwalvivek/helide/releases/latest";
    private const string DownloadBaseUrl = "https://github.com/ujjwalvivek/helide/releases/latest/download";
    private readonly string _currentVer;
    private readonly string _exePath;

    public string LatestAvailable { get; private set; } = "";

    public HelideUpdater()
    {
        _exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
        _currentVer = ReadInstalledVersion();
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Helide-Updater/1.0");
        var token = System.Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private string ReadInstalledVersion()
    {
        try
        {
            if (File.Exists(_exePath))
            {
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(_exePath);
                var s = v.FileVersion ?? v.ProductVersion ?? "1.0.0";
                var p = s.IndexOf('+');
                return p >= 0 ? s[..p] : s;
            }
        }
        catch { }
        return "1.0.0";
    }

    public async Task<bool> CheckAsync(Action<string>? status = null)
    {
        status?.Invoke("downloading");
        try
        {
            var json = await Client.GetStringAsync(ReleasesApi);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (!string.IsNullOrEmpty(tag))
            {
                var remoteStr = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
                var remote = new Version(remoteStr);
                var current = new Version(_currentVer);
                if (remote > current)
                {
                    LatestAvailable = remoteStr;
                    status?.Invoke($"Update available: v{remoteStr}");
                    return true;
                }
            }
        }
        catch { }
        status?.Invoke("restart to update");
        return false;
    }

    public async Task DownloadAndApplyAsync(Action<int>? progress = null)
    {
        try
        {
            var releaseJson = await Client.GetStringAsync(ReleasesApi);
            using var doc = System.Text.Json.JsonDocument.Parse(releaseJson);
            var assets = doc.RootElement.GetProperty("assets").EnumerateArray();
            string? downloadUrl = null;
            foreach (var a in assets)
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (string.IsNullOrEmpty(downloadUrl))
                downloadUrl = $"{DownloadBaseUrl}/Helide-win-x64.exe";
            if (string.IsNullOrEmpty(downloadUrl))
                throw new Exception("No download URL.");

            var tempDir = Path.Combine(Path.GetTempPath(), "helide-update");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, $"helide.{LatestAvailable}.tmp");

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(downloadUrl));
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            long downloaded = 0;

            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = File.Create(tempFile))
            {
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (total > 0) progress?.Invoke((int)(downloaded * 100 / total));
                }
            }

            progress?.Invoke(100);

            // Extract zip or copy exe
            string installedFile = tempFile;
            if (downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var extractDir = Path.Combine(tempDir, "extract");
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, extractDir);
                var files = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);
                if (files.Length == 0)
                    throw new Exception("Update zip missing .exe.");
                installedFile = files[0];
            }

            // Rename running .exe first (Windows allows), then move new file in
            var oldPath = _exePath + ".old." + Guid.NewGuid().ToString("N")[..6];
            try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
            File.Move(_exePath, oldPath);
            File.Copy(installedFile, _exePath, overwrite: true);
            try { File.Delete(installedFile); } catch { }
        }
        catch (Exception ex)
        {
            throw new Exception($"Update failed: {ex.Message}");
        }
    }

    public void RestartApp()
    {
        if (!File.Exists(_exePath))
        {
            throw new Exception("Helide executable not found at: " + _exePath);
        }
        try
        {
            var dir = Path.GetDirectoryName(_exePath) ?? ".";
            var psi = new System.Diagnostics.ProcessStartInfo(_exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = dir
            };
            System.Diagnostics.Process.Start(psi);
            System.Threading.Thread.Sleep(800);
            System.Environment.Exit(0);
        }
        catch (Exception ex)
        {
            // Re-throw so AboutWindow can show the error
            throw new Exception($"Failed to restart Helide: {ex.Message}. Path: {_exePath}", ex);
        }
    }
}

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
}
