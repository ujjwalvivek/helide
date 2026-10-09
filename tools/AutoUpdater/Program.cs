using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoUpdater;

internal class Program
{
    private static readonly HttpClient Client = new();
    private const string AppName = "Helide";
    private const int BarWidth = 40;

    static async Task<int> Main(string[] args)
    {
        bool silent = args.Length > 0 && args[0] == "--silent";
        bool checkOnly = args.Length > 0 && args[0] == "--check";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var updater = new AutoUpdater(cts);

            Clear();
            PrintHeader();

            WriteLine($"\n{Color.Cyan}\u27fa Checking for updates...{Color.Reset}");
            WriteLine($"   Source: https://api.github.com/repos/ujjwalvivek/helide/releases/latest");

            var spinnerChars = new[] { '|', '/', '-', '\\' };
            int spinnerIdx = 0;
            bool hasUpdate = false;

            using var spinnerTimer = new Timer(_ =>
            {
                Write($"\r{Color.Dim}{GetSpinnerChar(ref spinnerIdx)} Checking...{Color.Reset} ");
            }, null, 0, 100);

            try { hasUpdate = await updater.CheckForUpdateAsync(); }
            catch { }

            spinnerTimer.Dispose();
            WriteLine($"\r{Color.Green}\u2713 Check complete{Color.Reset}         ");

            if (!hasUpdate)
            {
                WriteLine($"\n{Color.Dim}No update available (current: {updater.CurrentVersion}).{Color.Reset}\n");
                return 0;
            }

            WriteLine($"\n{Color.Yellow}\u27fa New version available!{Color.Reset}");
            WriteLine($"   Current: {updater.CurrentVersion}");
            WriteLine($"   Latest:  {updater.LatestVersion}\n");

            if (checkOnly)
            {
                WriteLine($"{Color.Dim}(run without --check to download and install){Color.Reset}\n");
                return 0;
            }

            if (!silent)
            {
                WriteLine($"{Color.Cyan}\u27fa Starting download...{Color.Reset}\n");
            }

            await updater.DownloadAndInstallUpdateAsync(
                (percent, status) => DrawProgress(percent, status),
                (msg) => WriteLine($"{Color.Yellow}{msg}{Color.Reset}")
            );

            WriteLine($"\n{Color.Green}\u2713 Update installed. Restarting {AppName}...{Color.Reset}\n");
            updater.RestartApp();
            return 0;
        }
        catch (Exception ex)
        {
            WriteLine($"\n{Color.Red}\u2717 Error: {ex.Message}{Color.Reset}\n");
            return 1;
        }
    }

    static void Clear()
    {
        try { Console.Write("\x1b[2J\x1b[H"); } catch { }
    }

    static void PrintHeader()
    {
        WriteLine($"{Color.BrightWhite}{AppName} Auto-Updater{Color.Reset}");
        WriteLine($"{Color.Dim}\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500{Color.Reset}\n");
    }

    static char GetSpinnerChar(ref int idx)
    {
        char[] chars = { '|', '/', '-', '\\' };
        var c = chars[idx % chars.Length];
        idx++;
        return c;
    }

    static void DrawProgress(int percent, string status)
    {
        int filled = (int)(percent / 100.0 * BarWidth);
        string bar = new string('\u2588', filled) + new string('\u2591', BarWidth - filled);
        Console.Write($"\r{Color.Cyan}[{bar}]{Color.Reset} {percent:D3}% {Color.Dim}| {status}{Color.Reset}".PadRight(100));
    }

    static void WriteLine(string text) => Console.WriteLine(text);
    static void Write(string text) => Console.Write(text);

    private static class Color
    {
        public static string Reset => "\x1b[0m";
        public static string Bright = "\x1b[1m";
        public static string BrightWhite => "\x1b[1;97m";
        public static string Red => "\x1b[91m";
        public static string Green => "\x1b[92m";
        public static string Yellow => "\x1b[93m";
        public static string Cyan => "\x1b[96m";
        public static string Dim => "\x1b[2m";
    }
}

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }
    public bool Force { get; set; }
}

public class AutoUpdater : IDisposable
{
    internal const string AppName = "Helide";

    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _cts;
    private readonly string _exePath;
    private readonly string _exeDir;

    public string CurrentVersion { get; }
    public string LatestVersion { get; private set; }

    private const string ReleasesApi = "https://api.github.com/repos/ujjwalvivek/helide/releases/latest";
    private const string DownloadBase = "https://github.com/ujjwalvivek/helide/releases/latest/download";

    public AutoUpdater(CancellationTokenSource cts)
    {
        _cts = cts;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Helide-Updater/1.0");

        // Find Helide installation (not this updater's path)
        _exePath = FindHelideExecutable();
        _exeDir = System.IO.Path.GetDirectoryName(_exePath)!;
        CurrentVersion = GetCurrentAssemblyVersionFromHelide();
        LatestVersion = "unknown";
    }

    private static string FindHelideExecutable()
    {
        // Try installed location first (from check'n'install.ps1)
        var installPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Helide", "Helide.exe");
        if (System.IO.File.Exists(installPath)) return installPath;

        // Try same directory as updater
        var here = System.IO.Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)!;
        var local = System.IO.Path.Combine(here, "Helide.exe");
        if (System.IO.File.Exists(local)) return local;

        // Try bin directory next to updater
        var parent = System.IO.Path.GetDirectoryName(here);
        if (parent != null)
        {
            var projDir = System.IO.Path.Combine(parent, "bin", "Release", "net8.0-windows", "Helide.exe");
            if (System.IO.File.Exists(projDir)) return projDir;
        }

        // Fallback to updater's own dir (if updater was copied into Helide folder)
        return System.IO.Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)! + System.IO.Path.DirectorySeparatorChar + "Helide.exe";
    }

    private string GetCurrentAssemblyVersionFromHelide()
    {
        try
        {
            // Read Helide assembly info from installed exe
            if (System.IO.File.Exists(_exePath))
            {
                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(_exePath);
                var verStr = versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "1.0.0";
                var idx = verStr.IndexOf('+');
                return idx >= 0 ? verStr[..idx] : verStr;
            }
        }
        catch { }
        return "1.0.0";
    }

    private static string GetCurrentAssemblyVersion()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var name = assembly.GetName();
            return name.Version?.ToString() ?? "1.0.0";
        }
        catch { return "1.0.0"; }
    }

    public async Task<bool> CheckForUpdateAsync()
    {
        try
        {
            var json = await _httpClient.GetStringAsync(ReleasesApi, _cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tagName = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrEmpty(tagName)) return false;

            var remoteStr = tagName!.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? tagName.Substring(1) : tagName;

            var remoteVer = new Version(remoteStr);
            var currentVer = new Version(CurrentVersion);
            if (remoteVer > currentVer)
            {
                LatestVersion = tagName.TrimStart('v');
                return true;
            }
        }
        catch { }
        return false;
    }

    public async Task DownloadAndInstallUpdateAsync(Action<int, string> progressCallback, Action<string> logCallback)
    {
        try
        {
            var json = await _httpClient.GetStringAsync(ReleasesApi, _cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var assets = root.GetProperty("assets").EnumerateArray();
            string? downloadUrl = null;

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

            if (string.IsNullOrEmpty(downloadUrl))
                downloadUrl = $"{DownloadBase}/helide.zip";

            if (string.IsNullOrEmpty(downloadUrl))
            {
                logCallback?.Invoke("Could not find update download URL.");
                return;
            }

            var tempFile = System.IO.Path.Combine(_exeDir, $"helide.{LatestVersion}.{System.Guid.NewGuid():N[..8]}.tmp");

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(downloadUrl));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            response.EnsureSuccessStatusCode();

            var totalSize = response.Content.Headers.ContentLength ?? -1L;
            var downloaded = 0L;

            logCallback?.Invoke($"Downloading Helide v{LatestVersion}...");

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = System.IO.File.Create(tempFile);
            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token);
                downloaded += bytesRead;
                if (totalSize > 0)
                {
                    var percent = (int)(downloaded * 100 / totalSize);
                    var status = $"{FormatBytes(downloaded)} / {FormatBytes(totalSize)}";
                    progressCallback?.Invoke(percent, status);
                }
            }

            progressCallback?.Invoke(100, "Installing...");

            // Extract if it's a zip file
            if (downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetExtension(tempFile).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var extractDir = System.IO.Path.Combine(_exeDir, $"helide.extract.{System.Guid.NewGuid():N[..6]}");
                System.IO.Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(tempFile, extractDir);

                // Find the .exe inside the zip (usually Helide-win-x64.exe or Helide.exe)
                var files = System.IO.Directory.GetFiles(extractDir, "*.exe", System.IO.SearchOption.AllDirectories);
                string installedFile = files.Length > 0 ? files[0] : tempFile;

                // Copy to install location (same as current exe path for standalone updater)
                System.IO.File.Copy(installedFile, _exePath, overwrite: true);

                // Clean up temp
                System.IO.File.Delete(tempFile);
                System.IO.Directory.Delete(extractDir, recursive: true);
            }
            else
            {
                // Direct .exe download
                System.IO.File.Move(tempFile, _exePath);
            }

            logCallback?.Invoke("Update installed. Restarting...");
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"Download failed: {ex.Message}");
        }
    }

    public void RestartApp()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = true
            });
            System.Environment.Exit(0);
        }
        catch { }
    }

    private static string FormatBytes(long bytes)
    {
        var abs = Math.Abs(bytes);
        if (abs < 1024) return $"{abs} B";
        if (abs < 1024 * 1024) return $"{abs / 1024.0:0.0} KB";
        if (abs < 1024 * 1024 * 1024) return $"{abs / (1024.0 * 1024):0.0} MB";
        return $"{abs / (1024.0 * 1024 * 1024):0.0} GB";
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
