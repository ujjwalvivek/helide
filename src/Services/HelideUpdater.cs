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
        var tempDir = Path.Combine(Path.GetTempPath(), "helide-update");
        string? extractDir = null;

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
                extractDir = Path.Combine(tempDir, "extract");
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, extractDir);
                var files = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);
                if (files.Length == 0)
                    throw new Exception("Update zip missing .exe.");
                installedFile = PickAppExecutable(files);

                // The archive ships the updater beside the app, so it has to move too.
                // Without this the two drift apart: the app reports the new version and
                // the tool meant to keep it current is still the old one.
                var newUpdater = files.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), "AutoUpdater.exe", StringComparison.OrdinalIgnoreCase));
                if (newUpdater is not null)
                {
                    var target = Path.Combine(Path.GetDirectoryName(_exePath)!, "AutoUpdater.exe");
                    // Not this process's image, so no rename dance is needed -- nothing
                    // is holding it open, unless the user launched it by hand.
                    ReplaceExecutable(newUpdater, target, isRunning: false);
                }
            }

            // Rename running .exe first (Windows allows), then move new file in
            ReplaceExecutable(installedFile, _exePath, isRunning: true);
        }
        catch (Exception ex)
        {
            throw new Exception($"Update failed: {ex.Message}");
        }
        finally
        {
            // The archive held the updater as well as the app, so the extracted copy
            // has to go or every update leaves two stale exes behind in temp.
            TryDeleteTree(tempDir);
        }
    }

    // Puts newFile at targetPath. A file this process is executing cannot be overwritten
    // -- Windows holds its image open -- so it is first renamed out of the way; the OS
    // keeps the old handle alive on the old name and the new file lands cleanly.
    private static void ReplaceExecutable(string newFile, string targetPath, bool isRunning)
    {
        if (!isRunning)
        {
            File.Copy(newFile, targetPath, overwrite: true);
            try { File.Delete(newFile); } catch { }
            return;
        }

        var oldPath = targetPath + ".old." + Guid.NewGuid().ToString("N")[..6];
        try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
        File.Move(targetPath, oldPath);
        File.Copy(newFile, targetPath, overwrite: true);
        try { File.Delete(newFile); } catch { }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* a file still in use is deleted by the next launch's sweep */ }
    }

    // Chooses which extracted file is the app. The release archive ships this app's
    // updater alongside it, so the first match is AutoUpdater.exe -- installing that
    // over Helide.exe left the app launching the updater in an endless loop.
    private static string PickAppExecutable(string[] candidates)
    {
        foreach (var want in new[] { HelideExeName, HelideExeName + "-win-x64" })
        {
            var exact = candidates.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), want + ".exe", StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        var notSelf = candidates.FirstOrDefault(f =>
            !string.Equals(Path.GetFileName(f), "AutoUpdater.exe", StringComparison.OrdinalIgnoreCase));
        if (notSelf is not null) return notSelf;
        throw new Exception("Update zip contains the updater but no Helide executable.");
    }

    private const string HelideExeName = "Helide";

    /// <summary>
    /// Deletes the executables earlier updates renamed out of the way. A running
    /// process holds its own image open, so the exe it was replaced cannot be removed
    /// during the run that moved it; the next launch is the first moment it is free.
    /// Without this the install directory collects one .old file per update.
    /// </summary>
    public static void DeleteStaleOldExecutables()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;
            var dir = Path.GetDirectoryName(exe);
            if (dir is null) return;

            // The app and the updater sit beside each other, and both get replaced by
            // an update, so both leave a displaced file behind.
            foreach (var name in new[] { Path.GetFileName(exe), "AutoUpdater.exe" })
                foreach (var stale in Directory.EnumerateFiles(dir, name + ".old.*"))
                {
                    try { File.Delete(stale); } catch { /* still held by a live process */ }
                }
        }
        catch { /* housekeeping must never block startup */ }
    }

    // Starts the freshly downloaded exe and hands off the workspace to it.
    //
    // The single-instance guard has to be released first. The new process is the same
    // exe at the same path, so it tries to claim exactly the mutex this one still holds;
    // if it loses, it assumes another window already owns the project and shuts down,
    // which is what "clicked the pill and nothing came back" was.
    public void RestartApp(string? projectPath = null)
    {
        if (!File.Exists(_exePath))
        {
            throw new Exception("Helide executable not found at: " + _exePath);
        }
        try
        {
            // Persist the workspace so the new instance can resume it even if the
            // argument is lost, then let go of the instance claim before the child
            // tries to take it.
            if (!string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath))
            {
                var store = new Helide.Persistence.AppStateStore();
                var state = store.Load();
                store.RecordProject(state, projectPath);
            }

            App.ReleaseInstanceGuardForRestart();

            var dir = Path.GetDirectoryName(_exePath) ?? ".";
            var psi = new System.Diagnostics.ProcessStartInfo(_exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = dir,
                Arguments = string.IsNullOrWhiteSpace(projectPath) ? string.Empty : "\"" + projectPath + "\""
            };
            System.Diagnostics.Process.Start(psi);

            // Give the child time to get past its own startup before this process
            // disappears. The mutex is already free, so this is only about not
            // tearing down shared OS state (named events, the updater's HttpClient)
            // out from under a process that is seconds old.
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

/// <summary>
/// The one place the update lifecycle is stored, so the titlebar pill and the About
/// window cannot disagree. Before this, the pill only knew its own TextBlock and the
/// About window recomputed the whole check from scratch -- which meant clicking "Check
/// for Updates" after a silent download downloaded the update a second time.
/// </summary>
public static class HelideUpdateState
{
    // Null while nothing is happening: the About button then reads "Check for Updates".
    // Otherwise the label both surfaces show -- "downloading", "installing ... 42%",
    // or "restart to update" once the exe is on disk.
    public static string? Status { get; private set; }

    /// True once the update is on disk and the app just needs to restart to load it.
    public static bool IsReadyToRestart { get; private set; }

    /// True while the download/install is still in flight, so neither surface offers
    /// an interaction that would start a second one.
    public static bool IsInProgress => !IsReadyToRestart && Status is not null;

    /// The project to reopen after the restart. Empty means start on the welcome screen.
    public static string? PendingProjectPath { get; private set; }

    public static event Action? Changed;

    public static void SetDownloading()
    {
        IsReadyToRestart = false;
        Status = "downloading";
        HelideUpdateBroadcast.WriteStatus(Status);
        Changed?.Invoke();
    }

    public static void SetProgress(int percent)
    {
        IsReadyToRestart = false;
        Status = $"installing ... {percent}%";
        HelideUpdateBroadcast.WriteStatus(Status);
        Changed?.Invoke();
    }

    /// Call after DownloadAndApplyAsync succeeds. Both entry points read from here.
    public static void MarkUpdateApplied(string? projectPath)
    {
        IsReadyToRestart = true;
        PendingProjectPath = projectPath;
        Status = "restart to update";
        HelideUpdateBroadcast.WriteStatus(Status);
        Changed?.Invoke();
    }

    /// Back to idle, which is what "up to date" or a failed attempt means.
    public static void Clear()
    {
        IsReadyToRestart = false;
        Status = null;
        HelideUpdateBroadcast.WriteStatus(string.Empty);
        Changed?.Invoke();
    }

    // Adopts a label another process published. Deliberately does not write back, or
    // the two processes would take turns overwriting each other's status.
    internal static void MirrorStatus(string status)
    {
        status ??= string.Empty;
        if (Status == status) return;

        Status = string.IsNullOrEmpty(status) ? null : status;
        IsReadyToRestart = Status == "restart to update";
        Changed?.Invoke();
    }

    // Re-reads the shared label on demand, for a surface that was opened while an
    // update was already in flight.
    internal static void RefreshFromShared()
    {
        MirrorStatus(HelideUpdateBroadcast.ReadStatus());
    }

    /// The single restart path. The pill and the About window both go through it.
    public static void Restart()
    {
        new HelideUpdater().RestartApp(PendingProjectPath);
    }

    // ---------------------------------------------------------------- cross-process

    // One project per window means one process per window, and every process runs the
    // startup update check on its own. A static field is invisible between them, so
    // without this gate two open windows each download the same exe and each rename
    // Helide.exe out from under the other.
    private const string UpdateGateName = @"Local\Helide-Updater-Gate";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromMinutes(10);

    // Held for exactly one download-and-install. Null means another window already has
    // it, so the caller must not download.
    public static UpdateGate? TryAcquireUpdateGate()
    {
        var gate = new Mutex(false, UpdateGateName);
        try
        {
            if (!gate.WaitOne(0)) { gate.Dispose(); return null; }
            return new UpdateGate(gate);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died mid-install, so we now own it.
            return new UpdateGate(gate);
        }
        catch
        {
            gate.Dispose();
            return null;
        }
    }

    /// Waits for whichever process is mid-install, then reports whether the new exe
    /// landed on disk. Callers that lost the gate use this to reach the same
    /// "restart to update" state as the installer instead of downloading again.
    public static bool WaitForOtherInstaller()
    {
        Mutex gate;
        try
        {
            gate = new Mutex(false, UpdateGateName);
            try { gate.WaitOne(GateTimeout); }
            catch (AbandonedMutexException) { /* owner died; we own it now */ }
        }
        catch { return false; }

        try { return NewerVersionOnDisk(); }
        finally { gate.Dispose(); }
    }

    /// True when the exe on disk is newer than the image this process is running --
    /// which is how a window learns that another one installed the update for it.
    public static bool NewerVersionOnDisk()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

            var text = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (string.IsNullOrEmpty(text)) return false;
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text[..plus];

            if (Version.TryParse(text, out var disk))
                return disk > typeof(HelideUpdateState).Assembly.GetName().Version;
        }
        catch { }
        return false;
    }
}

/// <summary>Ownership of the update gate. Dispose releases it.</summary>
public sealed class UpdateGate : IDisposable
{
    private readonly Mutex _gate;
    private bool _released;

    internal UpdateGate(Mutex gate) => _gate = gate;

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        try { _gate.ReleaseMutex(); } catch { }
        _gate.Dispose();
    }
}

/// <summary>
/// The channel an update travels down between processes. Helide runs one process per
/// project, so a restart in one window has to reach the others, and a window that was
/// never the installer still has to learn the exe on disk moved past the copy it is
/// running. A named kernel object would not do: it disappears when the last handle
/// closes, which is exactly what a restarting process does.
/// </summary>
internal static class HelideUpdateBroadcast
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Helide");

    private static readonly string TicketPath = Path.Combine(Folder, "update-restart.txt");
    private static readonly string StatusPath = Path.Combine(Folder, "update-status.txt");

    // Sentinel so the first write of an empty status still happens.
    private static string _written = "\0";

    /// The value only moves forward, so a process never reacts twice to one request.
    public static long ReadTicket()
    {
        try { return long.Parse(File.ReadAllText(TicketPath).Trim()); }
        catch { return 0; }
    }

    /// Asks every Helide process to restart, each restoring its own workspace.
    public static void RequestRestart()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temporary = TicketPath + ".tmp";
            File.WriteAllText(temporary, DateTime.UtcNow.Ticks.ToString());
            File.Move(temporary, TicketPath, true);
        }
        catch { /* a failed request only means this window restarts alone */ }
    }

    // Publishes the label every window shows. Without it, the window that is not
    // installing has nothing to render -- its own state is empty because it lost the
    // gate -- so its pill stays hidden and its About button still offers a download.
    public static void WriteStatus(string status)
    {
        status ??= string.Empty;
        if (status == _written) return;
        _written = status;

        try
        {
            Directory.CreateDirectory(Folder);
            var temporary = StatusPath + ".tmp";
            File.WriteAllText(temporary, status);
            File.Move(temporary, StatusPath, true);
        }
        catch { /* a missing label only costs one 700ms poll of progress */ }
    }

    public static string ReadStatus()
    {
        try { return File.ReadAllText(StatusPath).Trim(); }
        catch { return string.Empty; }
    }

    /// Drops a stale label left by a previous session so a fresh launch starts clean.
    public static void ClearStatusForSession()
    {
        try
        {
            if (File.Exists(StatusPath)) File.Delete(StatusPath);
        }
        catch { }
        _written = string.Empty;
    }
}

public class UpdateInfo
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
}
