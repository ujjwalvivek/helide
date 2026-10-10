using System;
using System.Diagnostics;
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
    private const string ToolName = "auto-updater";

    private const string ReleasesApi =
        "https://api.github.com/repos/ujjwalvivek/helide/releases/latest";

    // The frame is the same one CheckNInstall draws -- same palette, same panes, same
    // rules -- so the two tools read as one product rather than two console programs.
    private const int PaneWidth = 64;
    private const int MinimumRightInner = 44;
    private static readonly string[] Wordmark = ["helide"];

    private static Theme _theme = Theme.Mocha;
    private static bool _tui;
    private static int _width, _height, _scroll;
    private static string[] _logo = [];
    private static string _status = string.Empty;
    private static string _version = string.Empty;

    private static readonly List<(string Tag, string Text)> _log = [];
    private static readonly object _renderLock = new();
    private static ConsoleCancelEventHandler? _cancelHandler;

    // The names are the ones the output below already used. Each now resolves through
    // the active theme rather than a fixed 4-bit colour, so a mocha user and an oled
    // user get the same states in their own palette.
    private static class Color
    {
        public static string Reset => "\x1b[0m";
        public static string BrightWhite => _theme.Text;
        public static string Dim => _theme.TextFaint;
        public static string Cyan => _theme.Accent;
        public static string Green => _theme.Success;
        public static string Yellow => _theme.Mauve;
        public static string Red => _theme.Danger;
    }

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _theme = LoadTheme() == "oled" ? Theme.Oled : Theme.Mocha;

        var silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        var checkOnly = args.Contains("--check", StringComparer.OrdinalIgnoreCase);

        EnterTui();

        try
        {
            // Deletes the .old exes the previous update displaced. This process is not
            // the one holding them open, so the locks are gone and they can go.
            AutoUpdater.DeleteStaleOldExecutables();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var updater = new AutoUpdater(cts);
            _version = $"current {updater.CurrentVersion}";

            Log("·", $"checking {AppName} for updates...");
            Log("·", $"source {ReleasesApi}");

            bool hasUpdate;
            using (var spinner = new Timer(Spin, null, 0, 120))
            {
                try { hasUpdate = await updater.CheckForUpdateAsync(); }
                catch (Exception ex) { Log("!", $"check failed: {ex.Message}"); hasUpdate = false; }
            }

            _version = $"current {updater.CurrentVersion}   latest {updater.LatestVersion}";

            if (!hasUpdate)
            {
                Log("ok", $"no update available (current {updater.CurrentVersion})");
                if (!silent) WaitForKey();
                return 0;
            }

            Log("·", "new version available");
            Log("·", $"current {updater.CurrentVersion}");
            Log("·", $"latest  {updater.LatestVersion}");

            if (checkOnly)
            {
                Log("·", "run without --check to download and install");
                if (!silent) WaitForKey();
                return 0;
            }

            if (!silent) Log("·", "starting download...");

            // The installer reports whether it actually finished. Claiming success on a
            // failure is what restarted Helide over and over, because every launch then
            // found the same "new" version and tried again.
            var installed = await updater.DownloadAndInstallUpdateAsync(
                DrawProgress,
                message => Log("·", message));

            if (!installed)
            {
                Log("FAILED", $"update was not installed. {AppName} left running.");
                if (!silent) WaitForKey();
                return 1;
            }

            Log("ok", $"update installed. restarting {AppName}...");
            updater.RestartApp();
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED", ex.Message);
            if (!silent) WaitForKey();
            return 1;
        }
        finally
        {
            LeaveTui();
        }
    }

    // ------------------------------------------------------------------ TUI

    private static void EnterTui()
    {
        try
        {
            if (Console.WindowWidth < 1 || Console.WindowHeight < 1) return;

            _width = Console.WindowWidth;
            _height = Console.WindowHeight;
        }
        catch
        {
            // A pipe or a redirected console gets the plain, line-oriented output below.
            return;
        }

        var stream = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("helide.txt");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            _logo = reader.ReadToEnd().TrimEnd('\r', '\n').Split('\n');
        }

        _tui = true;

        // The frame is drawn on the terminal's alternate screen, which is what a full
        // screen program is meant to use. Two things follow from it, and both were
        // symptoms before: the repaint no longer clears the buffer, so nothing scrolls
        // behind the frame, and leaving throws the whole thing away and puts the shell's
        // own scrollback back exactly as it was -- no clearing, no residue.
        Console.Write("\x1b[?1049h");
        Console.Write(Background.Sequence(_theme.WindowAsRgb));
        Console.CursorVisible = false;
        Console.Clear();

        // Ctrl-C is a console event, not a key, and the default behaviour kills the
        // process on the spot -- leaving the cursor hidden, the palette set, and the
        // frame still on screen for the next prompt to draw over.
        _cancelHandler = OnCancelKeyPress;
        Console.CancelKeyPress += _cancelHandler;

        Render();
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Cancelled so this handler gets to finish; the process is exited by hand rather
        // than being allowed to die in the middle of a frame.
        e.Cancel = true;

        LeaveTui();
        Environment.Exit(0);
    }

    private static void LeaveTui()
    {
        if (!_tui) return;
        _tui = false;

        if (_cancelHandler is not null)
        {
            Console.CancelKeyPress -= _cancelHandler;
            _cancelHandler = null;
        }

        // The window brush was set with a raw escape, which ResetColor knows nothing
        // about, so it is undone first. Then the alternate screen is dropped, which
        // discards the frame and restores whatever the shell had before -- so there is
        // nothing left to clear and the scrollback is untouched.
        Console.Write("\x1b[0m");
        Console.ResetColor();
        Console.CursorVisible = true;
        Console.Write("\x1b[?1049l");
    }

    // Called by the updater before Environment.Exit, which skips finally blocks.
    internal static void RestoreTui() => LeaveTui();

    /// Reads the theme the app is on, so this tool opens matching it.
    private static string LoadTheme()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Helide", "state.json");

            if (!File.Exists(path)) return "mocha";

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("Theme", out var theme) &&
                theme.GetString() is { } value &&
                value == "oled")
            {
                return "oled";
            }
        }
        catch
        {
            // A theme that cannot be read is not worth failing over.
        }

        return "mocha";
    }

    // Collapses the buffer to the window so the terminal has nothing to scroll.
    private static void LockViewport()
    {
        try
        {
            if (Console.WindowWidth != _width || Console.WindowHeight != _height)
            {
                _width = Console.WindowWidth;
                _height = Console.WindowHeight;
            }

            if (Console.BufferWidth != _width) Console.BufferWidth = _width;
            if (Console.BufferHeight != _height) Console.BufferHeight = _height;
        }
        catch
        {
            // A console that cannot be resized still repaints; it just scrolls.
        }
    }

    private static int LeftInner =>
        _width - 3 >= PaneWidth + MinimumRightInner ? PaneWidth : Math.Max(20, _width - 3 - MinimumRightInner);

    private static int RightInner => Math.Max(12, _width - 3 - LeftInner);

    private static int TitleRow => 0;
    private static int DividerRow => _height - 2;
    private static int StatusRow => _height - 1;

    // Rows available between the title and the rule above the status bar.
    private static int PaneRows => DividerRow - TitleRow - 1;

    private static void Render()
    {
        if (!_tui) return;

        lock (_renderLock)
        {
            LockViewport();

            Console.Write(Background.Sequence(_theme.WindowAsRgb));

            var left = LeftPaneLines();
            var right = RightPaneLines();

            for (var row = TitleRow + 1; row < DividerRow; row++)
            {
                var index = row - TitleRow - 1;
                var (leftText, leftColour) = index < left.Count ? left[index] : (string.Empty, _theme.Mauve);
                var (rightText, rightColour) = index < right.Count ? right[index] : (string.Empty, _theme.TextMuted);

                WriteRow(row, _theme.Border + "║" + Ansi.Reset
                    + leftColour + Fit(leftText, LeftInner) + Ansi.Reset
                    + _theme.Border + "║" + Ansi.Reset
                    + rightColour + Fit(rightText, RightInner) + Ansi.Reset
                    + _theme.Border + "║" + Ansi.Reset);
            }

            WriteRow(TitleRow, TitleRowLine());
            WriteRow(DividerRow, DividerRowLine());
            WriteRow(StatusRow, StatusRowLine());

            // Parked inside the box on purpose: a cursor left on the final cell is what
            // makes a console scroll, even when the buffer already fits.
            Console.SetCursorPosition(0, 0);
        }
    }

    // Brand on the left of the rule, what the tool is doing and where it is on the right.
    private static string TitleRowLine()
    {
        var inner = _width - 2;
        var brand = $" {Wordmark[0]} ";
        var tool = ToolName;
        var theme = _theme.Name;

        var gap = Math.Max(1, inner - brand.Length - tool.Length - $"{theme}  {_version}".Length);
        var detailWidth = inner - brand.Length - tool.Length - gap;

        var head = _theme.Mauve + brand + Ansi.Reset
            + _theme.TextMuted + tool + Ansi.Reset
            + _theme.Border + new string('═', gap) + Ansi.Reset;

        var detail = detailWidth > 0
            ? _theme.TextFaint + Fit($"{theme}  {_version}", detailWidth) + Ansi.Reset
            : string.Empty;

        return _theme.Border + "╔" + head + detail + "╗" + Ansi.Reset;
    }

    private static string DividerRowLine() =>
        _theme.Border + "╠" + new string('═', LeftInner) + "╬" + new string('═', RightInner) + "╣" + Ansi.Reset;

    // The bottom rule doubles as the status bar: the download bar lives here, and the
    // keyboard hints are here the rest of the time.
    private static string StatusRowLine()
    {
        // Padded to _width - 2 so the row is exactly as wide as the frame above it. At
        // two columns short the corners never met, and the gap is what let the terminal
        // treat the row as overflowing.
        var hint = _status.Length > 0 ? _status : "T theme · Ctrl+C quit";
        var content = Fit($" {hint} ", _width - 2);

        return _theme.Border + "╚" + Ansi.Reset
            + _theme.TextFaint + content + Ansi.Reset
            + _theme.Border + "╝" + Ansi.Reset;
    }

    private static List<(string Text, string Colour)> LeftPaneLines()
    {
        var rows = PaneRows;
        var offset = Math.Max(0, (rows - _logo.Length) / 2);
        var lines = new List<(string, string)>();

        for (var index = 0; index < rows; index++)
        {
            var source = index - offset;
            lines.Add(source >= 0 && source < _logo.Length
                ? (_logo[source], _theme.Mauve)
                : (string.Empty, _theme.Mauve));
        }

        return lines;
    }

    private static List<(string Text, string Colour)> RightPaneLines()
    {
        var lines = new List<(string Text, string Colour)>();
        var width = RightInner - 2;

        var entries = new List<(string Text, string Colour)>();

        foreach (var (tag, message) in _log)
        {
            var colour = tag switch
            {
                "ok" or "fixed" => _theme.Success,
                "missing" => _theme.TextMuted,
                "FAILED" or "!" => _theme.Danger,
                _ => _theme.TextMuted,
            };

            var prefix = tag == "·" || tag == "spin" ? string.Empty : $"[{tag,-7}] ";
            foreach (var wrapped in Wrap(prefix + message, width))
                entries.Add((wrapped, colour));
        }

        // The pane is a fixed height and the log is not, so it scrolls: zero is the tail,
        // which is where the newest line always wants to be.
        var available = PaneRows;
        var maxScroll = Math.Max(0, entries.Count - available);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        var start = Math.Max(0, entries.Count - available - _scroll);
        lines.AddRange(entries.Skip(start).Take(available));

        return lines;
    }

    // Wraps to the pane and keeps indentation. A token with no space in it -- a path or a
    // version string -- is broken across rows rather than clipped at the edge, where its
    // tail would be lost.
    private static string[] Wrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (width < 4) return [text];

        var lines = new List<string>();
        var budget = Math.Max(8, width - 8);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { lines.Add(string.Empty); continue; }

            var trimmed = line.TrimStart(' ');
            var indent = line[..^trimmed.Length];
            var current = string.Empty;

            foreach (var word in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var piece in Chunk(word, budget))
                {
                    if (current.Length == 0) current = indent + piece;
                    else if (current.Length + 1 + piece.Length <= width) current += " " + piece;
                    else { lines.Add(current); current = indent + piece; }
                }
            }

            if (current.Length > 0) lines.Add(current);
        }

        return [.. lines];
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        if (size <= 0) { yield return text; yield break; }

        for (var start = 0; start < text.Length; start += size)
            yield return text.Substring(start, Math.Min(size, text.Length - start));
    }

    private static string Fit(string text, int width)
    {
        if (width <= 0) return string.Empty;
        if (string.IsNullOrEmpty(text)) return new string(' ', width);
        return text.Length <= width ? text.PadRight(width) : text[..width];
    }

    private static void WriteRow(int row, string line)
    {
        Console.SetCursorPosition(0, row);
        Console.Write(line);
    }

    // One line of output, tagged the way the pane colours it. Without a console the same
    // text goes to stdout, which is what --silent and a pipe want.
    private static void Log(string tag, string message)
    {
        if (!_tui)
        {
            Console.WriteLine(tag == "·" ? $"  {message}" : $"  [{tag}] {message}");
            return;
        }

        lock (_renderLock)
        {
            // The spinner owns the last row and is replaced rather than appended, so the
            // log does not grow one line per tick.
            if (_log.Count > 0 && _log[^1].Tag == "spin")
                _log[^1] = (tag, message);
            else
                _log.Add((tag, message));

            Render();
        }
    }

    private static void Spin(object? _)
    {
        if (!_tui) return;

        lock (_renderLock)
        {
            var chars = new[] { '|', '/', '-', '\\' };
            _log[^1] = _log.Count > 0 && _log[^1].Tag == "spin"
                ? ("spin", $"checking... {chars[Environment.TickCount / 120 % 4]}")
                : ("spin", $"checking... {chars[0]}");

            // T cycles the palette, the same key CheckNInstall uses.
            while (Console.KeyAvailable)
            {
                if (Console.ReadKey(intercept: true).Key != ConsoleKey.T) continue;
                _theme = _theme == Theme.Mocha ? Theme.Oled : Theme.Mocha;
                Console.Write(Background.Sequence(_theme.WindowAsRgb));
            }

            Render();
        }
    }

// The download bar. It lives in the status row, so it does not push the log up.
    private static void DrawProgress(int percent, string status)
    {
        if (!_tui)
        {
            Console.Write($"\r[{percent,3}%] {status}".PadRight(100));
            return;
        }

        // The callback fires once per 8 KB chunk -- about twelve thousand times for a
        // 90 MB download -- and every one of them changes the byte count, so comparing
        // content to skip no-op repaints skipped nothing at all.
        //
        // The bar is repainted on two conditions instead: it has gained at least one
        // cell, or a heartbeat has passed so the byte count still advances. The bar is
        // only as many cells wide as it is wide, so the first condition bounds the
        // repaints during the download itself; the heartbeat is what makes the numbers
        // tick along without dragging the frame with them.
        var barWidth = Math.Max(10, _width - 2 - status.Length - 12);
        var filled = (int)(percent / 100.0 * barWidth);

        // The bar appearing and reaching the end are the two moments that matter, so
        // neither waits for a heartbeat.
        var isFirst = _drawnFilled < 0;
        var isComplete = percent >= 100;

        var now = Stopwatch.GetTimestamp();
        var grew = filled > _drawnFilled;
        var beat = now - _lastProgressTick >= HeartbeatTicks;

        if (!isFirst && !isComplete && !grew && !beat) return;

        _lastProgressTick = now;
        _drawnFilled = filled;

        _status = $"[{new string('#', filled)}{new string('.', Math.Max(0, barWidth - filled))}] {percent,3}%  {status}";
        Render();
    }

    // The byte count still moves between bar cells, so this is what keeps it visibly
    // alive. Precomputed to stopwatch ticks because that is the unit the clock comparison
    // above is in.
    private static readonly long HeartbeatTicks = Stopwatch.Frequency / 2;

    private static long _lastProgressTick;
    private static int _drawnFilled = -1;

    private static void WaitForKey()
    {
        _status = "any key to close";
        Render();

        try { Console.ReadKey(intercept: true); } catch { }
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
        var token = System.Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(token))
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

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

    public async Task<bool> DownloadAndInstallUpdateAsync(Action<int, string> progressCallback, Action<string> logCallback)
    {
        // Declared out here rather than inside the try, so the finally below can reach
        // it on both paths.
        var scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "helide-update");

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
                return false;
            }

            // The download goes to %TEMP% rather than beside the app: a 90 MB partial
            // file left by a cancelled run would sit in the install directory, which is
            // where the running app lives and where a stray file is both in the way and
            // visible to the user.
            System.IO.Directory.CreateDirectory(scratch);
            var tempFile = System.IO.Path.Combine(scratch, $"helide.{LatestVersion}.{System.Guid.NewGuid().ToString("N")[..8]}.tmp");

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(downloadUrl));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            response.EnsureSuccessStatusCode();

            var totalSize = response.Content.Headers.ContentLength ?? -1L;
            var downloaded = 0L;

            logCallback?.Invoke($"Downloading Helide v{LatestVersion}...");

            // Block-scoped on purpose rather than `using var` at method scope. File.Create
            // opens with FileShare.None, so while this handle is open nothing -- not the
            // extractor, not the copy -- can read the file, and every install failed with
            // "being used by another process". The handle closes here, before the install.
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = System.IO.File.Create(tempFile))
            {
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
            }

            progressCallback?.Invoke(100, "Installing...");

            // Extract if it's a zip file
            string downloadedFile = tempFile;
            if (downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetExtension(tempFile).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var extractDir = System.IO.Path.Combine(scratch, $"helide.extract.{System.Guid.NewGuid().ToString("N")[..6]}");
                System.IO.Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(tempFile, extractDir);

                var files = System.IO.Directory.GetFiles(extractDir, "*.exe", System.IO.SearchOption.AllDirectories);
                if (files.Length == 0)
                    throw new Exception("Update zip contains no .exe.");

                // The release archive ships this updater alongside the app, so the
                // first match is AutoUpdater.exe and not Helide.exe -- installing that
                // over Helide.exe left the app launching the updater in an endless
                // terminal loop, since the copy it restarted was itself.
                downloadedFile = PickAppExecutable(files);
                ReplaceRunningExecutable(downloadedFile);

                // Both exes travel together in the archive, so this updater has to move
                // itself too or it is left reporting a version the app already passed.
                // It is running, hence the same rename-out-of-the-way the app needs.
                //
                // The target is the canonical name, not this process's file name: when
                // Helide.exe has already been overwritten by this updater, the running
                // module *is* Helide.exe, and naming the target after it installs the
                // updater over the app again -- which is the loop this replaces.
                var newSelf = files.FirstOrDefault(f =>
                    string.Equals(System.IO.Path.GetFileName(f), "AutoUpdater.exe", StringComparison.OrdinalIgnoreCase));
                if (newSelf is not null)
                    ReplaceRunningExecutable(newSelf, "AutoUpdater.exe");
            }
            else
            {
                // Direct .exe download
                ReplaceRunningExecutable(downloadedFile);
            }

            logCallback?.Invoke("Update installed.");
            return true;
        }
        catch (Exception ex)
        {
            logCallback?.Invoke($"Download failed: {ex.Message}");
            return false;
        }
        finally
        {
            // The whole scratch directory goes, whether it worked or not. The archive held
            // the updater as well as the app, so leaving it behind would cost 90 MB a run.
            try
            {
                if (System.IO.Directory.Exists(scratch))
                    System.IO.Directory.Delete(scratch, recursive: true);
            }
            catch { /* a file still held open is cleaned up on the next run */ }
        }
    }

    // Windows refuses to overwrite an executable that is currently running, and both
    // Helide and this updater are while it is open. Renaming the running file out of the
    // way works because the OS keeps the open handle alive on the old name, then the new
    // file lands at the expected path. Same trick the in-app updater uses.
    //
    // The displaced file cannot be deleted here -- the process still executing it owns
    // that handle. DeleteStaleOldExecutables() removes it on the next run, once the
    // process has shut down and the lock is gone.
    private void ReplaceRunningExecutable(string newFile, string targetName = "Helide.exe")
    {
        var target = System.IO.Path.Combine(_exeDir, targetName);
        var oldPath = target + ".old." + System.Guid.NewGuid().ToString("N")[..6];
        try { if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath); } catch { }

        if (System.IO.File.Exists(target))
            System.IO.File.Move(target, oldPath);

        System.IO.File.Copy(newFile, target, overwrite: true);
    }

    /// <summary>
    /// Deletes the executables earlier updates renamed out of the way. The process that
    /// was executing one held it open, so nothing could remove it while it ran; this
    /// updater is a different process, so by now the lock is gone.
    /// </summary>
    internal static void DeleteStaleOldExecutables()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            var dir = string.IsNullOrEmpty(exe)
                ? null
                : System.IO.Path.GetDirectoryName(exe);
            if (dir is null) return;

            // The app and this updater are installed beside one another and both get
            // replaced by an update, so both leave a displaced file behind.
            foreach (var name in new[] { "Helide.exe", "AutoUpdater.exe" })
            {
                foreach (var stale in System.IO.Directory.EnumerateFiles(dir, name + ".old.*"))
                {
                    try { System.IO.File.Delete(stale); } catch { /* still in use elsewhere */ }
                }
            }

            // Earlier versions downloaded into the install directory rather than into
            // %TEMP%, so a 90 MB partial download and an extraction folder were left
            // sitting next to the app. The download moved, but nothing collected what the
            // old builds dropped, so this does it once per run.
            foreach (var pattern in new[] { "helide.*.tmp", "*.zip.tmp" })
            {
                foreach (var stale in System.IO.Directory.EnumerateFiles(dir, pattern))
                {
                    try { System.IO.File.Delete(stale); } catch { /* still being written */ }
                }
            }

            foreach (var folder in System.IO.Directory.EnumerateDirectories(dir, "helide.extract.*"))
            {
                try { System.IO.Directory.Delete(folder, recursive: true); }
                catch { /* something still open in there */ }
            }
        }
        catch { /* housekeeping must never stop the update */ }
    }

    // Chooses which extracted file is the app. The archive deliberately contains more
    // than one executable once this updater is bundled in, so "the first .exe" is not
    // a usable rule. Prefer the known app name, fall back to its flattened release
    // name, and only then anything that is not this updater.
    private static string PickAppExecutable(IReadOnlyList<string> candidates)
    {
        foreach (var want in new[] { "Helide.exe", "Helide-win-x64.exe" })
        {
            var exact = candidates.FirstOrDefault(f =>
                string.Equals(System.IO.Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        var notSelf = candidates.FirstOrDefault(f =>
            !string.Equals(System.IO.Path.GetFileName(f), "AutoUpdater.exe", StringComparison.OrdinalIgnoreCase));

        if (notSelf is not null) return notSelf;
        throw new Exception("Update zip contains the updater but no Helide executable.");
    }

    public void RestartApp()
    {
        // Environment.Exit does not run finally blocks, so the frame is torn down here
        // rather than by the caller's finally. Without this the whole window is left on
        // screen with the cursor hidden when the app takes over.
        Program.RestoreTui();

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


/// <summary>The two palettes, in the same values the app's theme dictionaries use.</summary>
internal sealed record Theme
{
    public required string Name { get; init; }
    public required string Mauve { get; init; }
    public required string Text { get; init; }
    public required string TextMuted { get; init; }
    public required string TextFaint { get; init; }
    public required string Accent { get; init; }
    public required string Success { get; init; }
    public required string Danger { get; init; }
    public required string Border { get; init; }
    public required string WindowAsRgb { get; init; }

    public static readonly Theme Mocha = new()
    {
        Name = "mocha",
        // Catppuccin Mocha. Window #D9191724, surfaces lifted from that base.
        WindowAsRgb = "25;23;36",
        Border = "\x1b[38;2;49;50;68m",
        Text = "\x1b[38;2;205;214;244m",
        TextMuted = "\x1b[38;2;166;173;200m",
        TextFaint = "\x1b[38;2;108;112;134m",
        Accent = "\x1b[38;2;137;180;250m",
        Success = "\x1b[38;2;166;227;161m",
        Danger = "\x1b[38;2;243;139;168m",
        // The Helide mark is mauve, so the palette's secondary column uses the same value
        // rather than a grey.
        Mauve = "\x1b[38;2;203;166;247m",
    };

    public static readonly Theme Oled = new()
    {
        Name = "oled",
        // Near-black rather than true black, with the chrome in small greyscale steps.
        WindowAsRgb = "14;14;14",
        Border = "\x1b[38;2;30;30;30m",
        Text = "\x1b[38;2;242;242;242m",
        TextMuted = "\x1b[38;2;176;176;176m",
        TextFaint = "\x1b[38;2;110;110;110m",
        // Pure white: nothing depends on telling the accent apart from anything else.
        Accent = "\x1b[38;2;255;255;255m",
        Success = "\x1b[38;2;143;207;143m",
        Danger = "\x1b[38;2;208;108;108m",
        // Muted relative to Catppuccin, where a full-strength mauve would read as a third
        // accent rather than as the secondary tone.
        Mauve = "\x1b[38;2;183;154;203m",
    };
}

/// <summary>Paints the viewport with the theme's window brush.</summary>
internal static class Background
{
    public static string Sequence(string rgbTriplet) => $"\x1b[48;2;{rgbTriplet}m";
}

internal static class Ansi
{
    public const string Reset = "\x1b[0m";
}
