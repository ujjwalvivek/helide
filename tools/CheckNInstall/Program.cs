using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Helide.Tooling;

/// <summary>
/// The check-and-install tool that used to be a PowerShell script. Same phases, same
/// rules, one exe: check the CLIs Helide shells out to, install what is missing, put
/// winget's portable install dirs on the user PATH, publish the single-file builds, and
/// drop them into %LOCALAPPDATA%\Programs\Helide.
/// </summary>
internal static class Program
{
    // Label -> the executable that actually shows up on PATH, and the winget id that
    // provides it. The names differ for Helix: the binary is `hx`, not `helix`.
    private static readonly (string Label, string Exe, string Winget)[] Requirements =
    [
        ("lazygit",  "lazygit",  "JesseDuffield.lazygit"),
        ("helix",    "hx",       "Helix.Helix"),
        ("yazi",     "yazi",     "sxyazi.yazi"),
        ("opencode", "opencode", "SST.opencode"),
        ("codex",    "codex",    "OpenAI.Codex"),
    ];

    private static readonly string[] MenuItems =
    [
        "Check dependencies",
        "Install missing tools",
        "Repair PATH",
        "Build Helide + AutoUpdater",
        "Install Helide + AutoUpdater",
        "Full run  (all of the above)",
        "Manual",
        "Exit",
    ];

    private const int ExitItem = 7;

    // Shorter than the shell manual it replaces, because the pane it renders into is a
    // fixed height and only the tail of anything longer would be visible.
    private const string Manual = """
        Helide check & install

        Checks the CLIs Helide shells out to, installs what is missing, repairs the user PATH, publishes the single-file builds, and installs them.

        USAGE
            CheckNInstall            interactive menu (this one)
            CheckNInstall --check    report only, exit 0 when ready
            CheckNInstall --full     every phase, no prompts
            CheckNInstall --help     this text

        CONTROLS
            up / down  move        1 - 7  jump to an item
            Enter      select      T      switch theme, mocha / oled
            Esc        quit

        PHASES
            1  Check          lazygit, hx, yazi, opencode, codex.
            2  Install        winget install each missing one.
            3  Repair PATH    The real directory under WinGet\Packages is added to the user PATH, and refused past 2000 characters.
            4  Build          dotnet publish, Helide then AutoUpdater, excluded from Helide.csproj. PublishSingleFile is -time only, so a plain build cannot make it.
            5  Install        %LOCALAPPDATA%\Programs\Helide\Helide.exe, on the user PATH, with a Start Menu entry. AutoUpdater.exe beside it.

        NOTES
            Restart the shell after any phase that touched PATH. Helide.exe and AutoUpdater.exe are the only files installed. This tool is for building and installing; it is never copied anywhere.
        """;

    private static string _repoRoot = AppContext.BaseDirectory;
    private static bool _selfContained = true;

    private static string AppProject => Path.Combine(_repoRoot, "Helide.csproj");

    // The csproj's flatten target lifts the single bundle out of the publish folder and
    // names it after the RID, so this is where a finished build actually lands.
    private static string AppArtifact =>
        Path.Combine(_repoRoot, "bin", "Release", "net8.0-windows", "Helide-win-x64.exe");

    private static string UpdaterProject =>
        Path.Combine(_repoRoot, "tools", "AutoUpdater", "AutoUpdater.csproj");

    private static string UpdaterArtifact => Path.Combine(
        _repoRoot, "tools", "AutoUpdater", "bin", "Release", "net8.0-windows", "AutoUpdater-win-x64.exe");

    private static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Helide");

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _repoRoot = FindRepoRoot();

        // Non-interactive entry points. These stay line-oriented on purpose: they are
        // for build scripts and pipes, where a repainting TUI is worse than useless --
        // and where reading the window size is not possible at all.
        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
            return RunCheck();

        if (args.Contains("--full", StringComparer.OrdinalIgnoreCase))
            return RunAllPhases();

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h"))
            return ShowManual();

        return RunInteractive();
    }

    private static int RunCheck()
    {
        Banner();
        var missing = Check();
        Ok(missing ? "one or more tools are missing" : "ready to install");
        return missing ? ExitFailed : ExitOk;
    }

    private static int ShowManual()
    {
        if (_tui)
        {
            // Rendered through the pane rather than printed, so opening it in the
            // interactive window does not push the frame off the top.
            ClearLog();
            Emit("·", "manual");
        }
        else
        {
            Banner();
        }

        foreach (var line in Manual.Split('\n'))
            Emit("·", line.TrimEnd('\r'));

        return ExitOk;
    }

    // ------------------------------------------------------------------ interactive

    private static int RunInteractive()
    {
        // A window that cannot be measured means the output was redirected, and every
        // positioning call below would throw. Fall back rather than crash.
        if (!TryEnterTui())
        {
            Banner();
            Warn("no console window to draw in; use --check or --full instead");
            return ExitFailed;
        }

        _theme = LoadTheme() == "oled" ? Theme.Oled : Theme.Mocha;

        while (true)
        {
            var choice = Menu();
            if (choice == ExitItem) break;

            _mode = Mode.Log;
            ClearLog();
            Emit("·", MenuItems[choice]);

            switch (choice)
            {
                case 0: Check(); break;
                case 1:
                    // Reports first, so the user sees what the installer is about to act
                    // on rather than a silent winget run.
                    var missing = FindMissing();
                    if (missing.Count == 0) Note("nothing to install");
                    else InstallTools(missing);
                    break;
                case 2: RepairPath(FindMissing()); break;
                case 3: Build(); break;
                case 4: InstallApp(); InstallUpdater(); break;
                case 5: RunAllPhases(); break;
                case 6: ShowManual(); break;
            }

            WaitForMenuKey();
        }

        LeaveTui();
        return ExitOk;
    }

    private static int RunAllPhases()
    {
        var missing = Check();
        if (missing)
        {
            Emit("·", "Install missing tools");
            InstallTools(FindMissing());
        }

        var unresolved = FindMissing();
        if (unresolved.Count > 0)
        {
            Emit("·", "Repair PATH");
            RepairPath(unresolved);
            unresolved = FindMissing();
        }

        // Refuse to build against a machine the app cannot run on: every phase after
        // this would succeed and produce an install that fails on first use.
        if (unresolved.Count > 0)
        {
            foreach (var label in unresolved)
                Emit("·", $"winget install -e --id {WingetFor(label)}");
            Error("still missing, so the install would not run");
            return ExitFailed;
        }

        Emit("·", "Build");
        if (!Build()) { Error("publish failed"); return ExitFailed; }
        Ok("publish succeeded");

        Emit("·", "Install Helide");
        var installedApp = InstallApp();

        Emit("·", "Install updater");
        var installedUpdater = InstallUpdater();

        if (!installedApp || !installedUpdater) return ExitFailed;

        Ok("done. restart your shell, then run: helide");
        return ExitOk;
    }

    // ------------------------------------------------------------------ phases

    /// Checks every required CLI. Returns true when at least one is missing.
    private static bool Check()
    {
        var anyMissing = false;

        foreach (var (label, exe, _) in Requirements)
        {
            if (IsOnPath(exe))
                Emit("ok", $"{label,-9} -> {exe}");
            else
            {
                Emit("missing", $"{label,-9} -> {exe}");
                anyMissing = true;
            }
        }

        return anyMissing;
    }

    private static List<string> FindMissing()
    {
        var missing = new List<string>();
        foreach (var (label, exe, _) in Requirements)
            if (!IsOnPath(exe)) missing.Add(label);
        return missing;
    }

    private static string WingetFor(string label)
    {
        foreach (var requirement in Requirements)
            if (requirement.Label == label) return requirement.Winget;
        return label;
    }

    private static void InstallTools(List<string> labels)
    {
        if (labels.Count == 0) { Note("nothing to install"); return; }

        if (!WingetAvailable())
        {
            Error("winget is not available, so nothing can be installed automatically");
            Note("install App Installer from the Microsoft Store, then re-run");
            return;
        }

        foreach (var label in labels)
        {
            Emit("install", $"winget install -e --id {WingetFor(label)}");
            Run("winget",
                $"install -e --id {WingetFor(label)} --accept-package-agreements --accept-source-agreements");
        }

        Note("restart your shell for the new installs to reach PATH");
    }

    /// winget exposes portable packages through %LOCALAPPDATA%\Microsoft\WinGet\Links,
    /// which can end up empty even when the package installed correctly. The install
    /// directory is added to the user PATH instead of chasing that.
    private static void RepairPath(List<string> labels)
    {
        if (labels.Count == 0) { Note("nothing to repair"); return; }

        foreach (var label in labels)
        {
            var exe = ExeFor(label);
            if (IsOnPath(exe)) { Emit("ok", $"{label,-9} resolved elsewhere"); continue; }

            var dir = FindWingetInstallDir(exe);
            if (dir is null) { Emit("FAILED", $"{label,-9} not found under WinGet\\Packages"); continue; }

            switch (AddToUserPath(dir))
            {
                case "added":
                    Emit("fixed", $"{label,-9} + {dir}");
                    break;
                case "present":
                    Emit("ok", $"{label,-9} already on user PATH");
                    break;
                default:
                    Emit("FAILED", $"{label,-9} could not add {dir}");
                    break;
            }
        }

        Note("restart your shell for PATH changes to apply");
    }

    private static string ExeFor(string label)
    {
        foreach (var requirement in Requirements)
            if (requirement.Label == label) return requirement.Exe;
        return label;
    }

    private static bool Build()
    {
        if (!File.Exists(AppProject))
        {
            Error($"no Helide.csproj under {_repoRoot}");
            return false;
        }

        // PublishSingleFile is a publish-time property, so a plain build cannot produce
        // the single exe -- and publish runs the build as its first step anyway.
        var selfContained = _selfContained ? "--self-contained" : "--self-contained:false";

        Emit("build", $"dotnet publish Helide.csproj -c Release -r win-x64 {selfContained}");
        if (Run("dotnet", $"publish \"{AppProject}\" -c Release -r win-x64 {selfContained}", _repoRoot) != 0)
            return false;

        // The updater is its own project and is excluded from Helide.csproj, so nothing
        // above built it.
        if (!File.Exists(UpdaterProject))
        {
            Note("no AutoUpdater.csproj under tools\\, skipping");
            return true;
        }

        Emit("build", $"dotnet publish AutoUpdater.csproj -c Release -r win-x64 {selfContained}");
        return Run("dotnet",
            $"publish \"{UpdaterProject}\" -c Release -r win-x64 {selfContained}", _repoRoot) == 0;
    }

    private static bool InstallApp()
    {
        if (!File.Exists(AppArtifact))
        {
            Error($"no published exe at {AppArtifact}");
            Note("run the build phase first");
            return false;
        }

        Directory.CreateDirectory(InstallDir);

        // Renamed on install: the file on PATH is `helide`, and the RID belongs to the
        // artifact's name, where win-x64 and arm64 side by side would collide.
        var exe = Path.Combine(InstallDir, "Helide.exe");
        File.Copy(AppArtifact, exe, overwrite: true);

        var megabytes = Math.Round(new FileInfo(exe).Length / 1024d / 1024d, 1);
        Emit("copied", $"Helide.exe ({megabytes} MB) -> {InstallDir}");

        switch (AddToUserPath(InstallDir))
        {
            case "added":
                Emit("fixed", $"added to user PATH: {InstallDir}");
                break;
            case "present":
                Emit("ok", $"already on user PATH: {InstallDir}");
                break;
            default:
                Error($"could not add {InstallDir} to user PATH");
                return false;
        }

        // PATH only makes helide runnable from a shell. Without a Start Menu entry the
        // app is invisible everywhere Windows actually looks for installed apps, and
        // searching comes back with the project folder instead.
        var shortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "Helide.lnk");

        CreateShortcut(shortcut, exe, InstallDir, "Helide terminal workspace");
        Emit("link", $"Start Menu entry -> {shortcut}");

        Note("restart your shell, then run: helide");
        return true;
    }

    private static bool InstallUpdater()
    {
        if (!File.Exists(UpdaterArtifact))
        {
            Error($"no published updater at {UpdaterArtifact}");
            Note("Helide itself is still installed");
            return false;
        }

        Directory.CreateDirectory(InstallDir);

        var exe = Path.Combine(InstallDir, "AutoUpdater.exe");
        File.Copy(UpdaterArtifact, exe, overwrite: true);

        var megabytes = Math.Round(new FileInfo(exe).Length / 1024d / 1024d, 1);
        Emit("copied", $"AutoUpdater.exe ({megabytes} MB) -> {InstallDir}");

        // Deliberately no PATH entry and no Start Menu shortcut: it is an internal tool
        // that finds the app by looking next to itself, so it needs nothing else.
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Windows resolves an unqualified command through PATHEXT, so the bare name
            // is not the only thing that can be there.
            foreach (var candidate in new[] { exe, exe + ".exe", exe + ".cmd", exe + ".bat", exe + ".com" })
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, candidate))) return true;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth throwing over.
                }
            }
        }

        return false;
    }

    private static string? FindWingetInstallDir(string exe)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");

        if (!Directory.Exists(root)) return null;

        try
        {
            var hit = Directory
                .EnumerateFiles(root, $"{exe}.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            return hit is null ? null : Path.GetDirectoryName(hit);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Returns "added", "present", or "failed".
    private static string AddToUserPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "failed";

        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var entries = current
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (entries.Any(entry => string.Equals(entry, directory, StringComparison.OrdinalIgnoreCase)))
            return "present";

        // Guard the length before writing. A truncated PATH breaks every shell on the
        // machine, so refuse rather than write something partial.
        var combined = string.Join(';', entries.Concat(new[] { directory }));
        if (combined.Length > 2000)
        {
            Warn("user PATH would reach " + combined.Length + " chars; refusing");
            return "failed";
        }

        Environment.SetEnvironmentVariable("PATH", combined, EnvironmentVariableTarget.User);
        return "added";
    }

    private static bool WingetAvailable()
    {
        try { return Run("winget", "--version") == 0; }
        catch { return false; }
    }

    private static int Run(string fileName, string arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments) { UseShellExecute = false };
        if (workingDirectory is not null) startInfo.WorkingDirectory = workingDirectory;

        // In the TUI the child inherits a repainted window, so its output is captured
        // and replayed into the log pane instead of being written past the frame.
        if (_tui)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            using var process = Process.Start(startInfo);
            if (process is null) return -1;

            // Two async readers, so a child that fills one pipe cannot deadlock on the
            // other. Guarded because both raise on pool threads.
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Emit("·", e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Emit("!", e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();
            return process.ExitCode;
        }

        using var plain = Process.Start(startInfo);
        if (plain is null) return -1;
        plain.WaitForExit();
        return plain.ExitCode;
    }

    // A Start Menu entry is a .lnk, and the only supported way to write one is the COM
    // object. dynamic keeps this free of an interop assembly reference.
    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = workingDirectory;
            link.Description = description;
            link.Save();
        }
        catch (Exception ex)
        {
            Warn($"could not create the Start Menu entry: {ex.Message}");
        }
    }

    /// Walks up from the build output until it finds the repo root, so the tool works
    /// whether it was run from bin, from tools\, or from the project folder.
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Helide.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    /// Reads the theme the app is currently on, so the TUI opens matching it rather
    /// than defaulting to mocha every time.
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

    private const int ExitOk = 0;
    private const int ExitFailed = 1;

    // ------------------------------------------------------------------ TUI

    private enum Mode { Menu, Log }

    private static bool _tui;
    private static Mode _mode = Mode.Menu;
    private static Theme _theme = Theme.Mocha;

    // Rows the log has been scrolled up from its tail. Zero pins it to the bottom.
    private static int _scroll;

    private static int _width;
    private static int _height;
    private static int _selected;
    private static string _footer = string.Empty;

    // The art is 64 columns at its widest and there is no point in a content pane
    // narrower than this, so the left pane is fixed at the art's width and only the
    // window being too narrow ever shrinks it. Sizing it from the window instead (half
    // of it, say) clips the mark on exactly the widths that could have shown it whole.
    private const int PaneWidth = 64;
    private const int MinimumRightInner = 44;

    private static readonly string[] Wordmark = ["helide"];

    private const string Tagline = "A terminal workspace that brings the project tools you need";

    private static readonly List<(string Tag, string Message)> _log = [];
    private static readonly object _renderLock = new();

    private static string[] _logo = [];

    private static bool TryEnterTui()
    {
        try
        {
            if (Console.WindowWidth < 1 || Console.WindowHeight < 1) return false;

            _width = Console.WindowWidth;
            _height = Console.WindowHeight;
        }
        catch
        {
            return false;
        }

        _logo = EmbeddedLogo().Split('\n');
        _tui = true;

        Console.CursorVisible = false;
        return true;
    }

    private static void LeaveTui()
    {
        if (!_tui) return;
        _tui = false;

        Console.ResetColor();
        Console.Clear();
        Console.CursorVisible = true;
    }

    /// Collapses the buffer to the window so the terminal has nothing to scroll, and
    /// never writes the bottom-right cell -- which is what forces a scroll even when
    /// the buffer already matches.
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

    // One column between the panes, which is where the vertical rule runs.
    private static int DividerColumn => LeftInner + 1;

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

            // The window brush paints the whole viewport, so the box sits on the theme's
            // background rather than the terminal's own.
            Console.Write(Background.Sequence(_theme.WindowAsRgb));
            Console.Clear();

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

            // Parked inside the box on purpose. A cursor left on the final cell is what
            // makes a console scroll, even when the buffer already fits the window.
            Console.SetCursorPosition(0, 0);
        }
    }

    

    // Wraps to the pane and keeps indentation, because the manual is laid out with it and
    // a wrapping that drops it leaves each row flush against the border. A token with no
    // space in it -- a path in a build log -- is broken across rows rather than clipped
    // at the edge, where its tail would be lost completely.
    private static string[] Wrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (width < 4) return [text];

        var lines = new List<string>();

        // Room for the indent on every row, including the first.
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

    // The two panes are built first and then drawn row by row, so a pane's contents can
    // never be written over the frame -- which is what made the rule drift.
    private static List<(string Text, string Colour)> LeftPaneLines()
    {
        var rows = PaneRows;
        var offset = Math.Max(0, (rows - _logo.Length) / 2);
        var lines = new List<(string, string)>();

        for (var index = 0; index < rows; index++)
        {
            var source = index - offset;
            lines.Add(source >= 0 && source < _logo.Length ? (_logo[source], _theme.Mauve) : (string.Empty, _theme.Mauve));
        }

        return lines;
    }

    private static List<(string Text, string Colour)> RightPaneLines()
    {
        var lines = new List<(string, string)>();
        var width = RightInner - 2;

        if (_mode == Mode.Menu)
        {
            // The line the product puts above the menu: what Helide is, rather than only
            // what this tool is about to do.
            foreach (var line in Wrap(Tagline, width))
                lines.Add((line, _theme.TextFaint));

            lines.Add((string.Empty, _theme.TextFaint));
            lines.Add(($"{_repoRoot}", _theme.TextFaint));
            lines.Add((string.Empty, _theme.TextFaint));

            for (var index = 0; index < MenuItems.Length; index++)
            {
                var colour = index == _selected ? _theme.TextStrong : _theme.TextMuted;
                lines.Add((index == _selected ? $"❯ {MenuItems[index]}" : $"  {index + 1}  {MenuItems[index]}", colour));
            }
        }
        else
        {
            var theme = _theme;
            var entries = new List<(string Text, string Colour)>();

            foreach (var (tag, message) in _log)
            {
                var colour = tag switch
                {
                    "ok" or "fixed" => theme.Success,
                    "missing" => theme.TextMuted,
                    "FAILED" or "!" => theme.Danger,
                    "build" or "install" or "copied" or "link" => theme.TextFaint,
                    _ => theme.TextMuted,
                };

                var prefix = tag == "·" ? string.Empty : $"[{tag,-7}] ";
                foreach (var wrapped in Wrap(prefix + message, width))
                    entries.Add((wrapped, colour));
            }

            // The pane is a fixed height and the log is not, so it scrolls: zero is the
            // tail, where a build log is always wanted, and anything higher has been
            // moved up. Without this nothing longer than the pane is reachable at all --
            // the top of the manual simply cannot be seen.
            var available = PaneRows;
            var maxScroll = Math.Max(0, entries.Count - available);
            _scroll = Math.Clamp(_scroll, 0, maxScroll);

            var start = Math.Max(0, entries.Count - available - _scroll);
            lines.AddRange(entries.Skip(start).Take(available));
        }

        return lines;
    }

    // Every boxed row is assembled once, at exactly _width cells, with each segment
    // padded or truncated to its own column budget. Nothing is written over anything
    // else, so the corners cannot drift apart the way a repaint stack does.
    private static string TitleRowLine()
    {
        var inner = _width - 2;
        var brand = $" {Wordmark[0]} ";
        var tool = "check & install";
        var theme = _theme == Theme.Mocha ? "mocha" : "oled";

        var gap = Math.Max(1, inner - brand.Length - tool.Length - $"{theme}  {_repoRoot}".Length);
        var detailWidth = inner - brand.Length - tool.Length - gap;

        var head = _theme.Mauve + brand + Ansi.Reset
            + _theme.TextMuted + tool + Ansi.Reset
            + _theme.Border + new string('═', gap) + Ansi.Reset;

        var detail = detailWidth > 0
            ? _theme.TextFaint + Fit($"{theme}  {_repoRoot}", detailWidth) + Ansi.Reset
            : _theme.Border + new string('═', detailWidth < 0 ? 0 : 0) + Ansi.Reset;

        return _theme.Border + "╔" + head + detail + "╗" + Ansi.Reset;
    }

    private static string DividerRowLine() =>
        _theme.Border + "╠" + new string('═', LeftInner) + "╬" + new string('═', RightInner) + "╣" + Ansi.Reset;

    // The bottom rule is also the status bar, so the frame closes without spending a row
    // of its own, and the footer still gets the full width.
    private static string StatusRowLine()
    {
        var content = Fit($" {_footer} ", _width - 2);
        return _theme.Border + "╚" + Ansi.Reset + _theme.TextFaint + content + Ansi.Reset + _theme.Border + "╝" + Ansi.Reset;
    }

    private static void WriteRow(int row, string line)
    {
        Console.SetCursorPosition(0, row);
        Console.Write(line);
    }

    // Every column budget is honoured exactly -- padded when short, truncated when long
    // -- because the frame is assembled from these widths and a single overflow shifts
    // every row that follows it.
    private static string Fit(string text, int width)
    {
        if (width <= 0) return string.Empty;
        if (string.IsNullOrEmpty(text)) return new string(' ', width);
        return text.Length <= width ? text.PadRight(width) : text[..width];
    }

    

    private static void ClearLog()
    {
        lock (_renderLock)
        {
            _log.Clear();
            _scroll = 0;
            Render();
        }
    }

    // ------------------------------------------------------------------ input

    private static int Menu()
    {
        _mode = Mode.Menu;
        _footer = "↑↓ move · Enter select · 1-7 jump · T theme · Esc quit";
        Render();

        Console.CursorVisible = false;

        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;

            switch (key)
            {
                case ConsoleKey.UpArrow:
                    _selected = (_selected - 1 + MenuItems.Length) % MenuItems.Length;
                    break;
                case ConsoleKey.DownArrow:
                    _selected = (_selected + 1) % MenuItems.Length;
                    break;
                case ConsoleKey.Enter:
                    if (_selected == ExitItem) return ExitItem;
                    return _selected;
                case ConsoleKey.Escape:
                    return ExitItem;
                case ConsoleKey.T:
                    _theme = _theme == Theme.Mocha ? Theme.Oled : Theme.Mocha;
                    break;
                case ConsoleKey.D1:
                case ConsoleKey.NumPad1:
                    if (_selected != 0) return 0;
                    break;
                case ConsoleKey.D2:
                case ConsoleKey.NumPad2:
                    if (_selected != 1) return 1;
                    break;
                case ConsoleKey.D3:
                case ConsoleKey.NumPad3:
                    if (_selected != 2) return 2;
                    break;
                case ConsoleKey.D4:
                case ConsoleKey.NumPad4:
                    if (_selected != 3) return 3;
                    break;
                case ConsoleKey.D5:
                case ConsoleKey.NumPad5:
                    if (_selected != 4) return 4;
                    break;
                case ConsoleKey.D6:
                case ConsoleKey.NumPad6:
                    if (_selected != 5) return 5;
                    break;
                case ConsoleKey.D7:
                case ConsoleKey.NumPad7:
                    if (_selected != 6) return 6;
                    break;
                case ConsoleKey.D8:
                case ConsoleKey.NumPad8:
                    return ExitItem;
                default:
                    continue;
            }

            Render();
        }
    }

    private static void WaitForMenuKey()
    {
        SetFooter();
        Render();

        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            var step = Math.Max(1, PaneRows / 2);

            switch (key)
            {
                case ConsoleKey.UpArrow:
                    _scroll++;
                    break;
                case ConsoleKey.DownArrow:
                    _scroll--;
                    break;
                case ConsoleKey.PageUp:
                    _scroll += step;
                    break;
                case ConsoleKey.PageDown:
                case ConsoleKey.Spacebar:
                    _scroll -= step;
                    break;
                case ConsoleKey.Home:
                    _scroll = int.MaxValue;
                    break;
                case ConsoleKey.End:
                    _scroll = 0;
                    break;
                default:
                    return;
            }

            SetFooter();
            Render();
        }
    }

    private static void SetFooter()
    {
        var hints = "↑↓ scroll · PgUp/PgDn page · Home/End · any key → menu";

        // Stating the offset without saying how many rows are above is a number that
        // means nothing.
        _footer = _scroll > 0 ? $"{hints}   [ {_scroll} more above ]" : hints;
    }

    // ------------------------------------------------------------------ output

    // One sink for every line the phases produce. In the TUI it lands in the log pane
    // and triggers a repaint; without a console it goes straight to stdout, which is
    // what the --check and --full entry points want.
    private static void Emit(string tag, string message)
    {
        if (!_tui)
        {
            Console.WriteLine(tag == "·" ? $"  {message}" : $"  [{tag}] {message}");
            return;
        }

        lock (_renderLock)
        {
            _log.Add((tag, message));
            Render();
        }
    }

    private static void Ok(string message) => Emit("·", $"✓ {message}");

    private static void Error(string message) => Emit("·", $"✗ {message}");

    private static void Warn(string message) => Emit("!", message);

    private static void Note(string message) => Emit("·", message);

    private static void Banner()
    {
        if (_tui) return;

        var logo = EmbeddedLogo();
        if (!string.IsNullOrEmpty(logo))
        {
            Console.WriteLine(logo);
            Console.WriteLine();
        }

        Console.WriteLine($"check & install   repo: {_repoRoot}");
    }

    private static string EmbeddedLogo()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("helide.txt");
        if (stream is null) return string.Empty;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }
}

/// <summary>The two palettes, in the same values the app's theme dictionaries use.</summary>
internal sealed record Theme
{
    public required string Name { get; init; }
    public required string Mauve { get; init; }
    public required string Text { get; init; }
    public required string TextStrong { get; init; }
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
        Border = Ansi.Rgb(0x31, 0x32, 0x44),
        Text = Ansi.Rgb(0xcd, 0xd6, 0xf4),
        TextStrong = Ansi.Rgb(0xba, 0xc2, 0xde),
        TextMuted = Ansi.Rgb(0xa6, 0xad, 0xc8),
        TextFaint = Ansi.Rgb(0x6c, 0x70, 0x86),
        Accent = Ansi.Rgb(0x89, 0xb4, 0xfa),
        Success = Ansi.Rgb(0xa6, 0xe3, 0xa1),
        Danger = Ansi.Rgb(0xf3, 0x8b, 0xa8),
        // The Helide mark is mauve, so the palette's secondary column uses the same
        // value rather than a grey.
        Mauve = Ansi.Rgb(0xcb, 0xa6, 0xf7),
    };

    public static readonly Theme Oled = new()
    {
        Name = "oled",
        // Near-black rather than true black, with the chrome in small greyscale steps.
        WindowAsRgb = "14;14;14",
        Border = Ansi.Rgb(0x1e, 0x1e, 0x1e),
        Text = Ansi.Rgb(0xf2, 0xf2, 0xf2),
        TextStrong = Ansi.Rgb(0xd9, 0xd9, 0xd9),
        TextMuted = Ansi.Rgb(0xb0, 0xb0, 0xb0),
        TextFaint = Ansi.Rgb(0x6e, 0x6e, 0x6e),
        // Pure white: nothing depends on telling the accent apart from anything else.
        Accent = Ansi.Rgb(0xff, 0xff, 0xff),
        Success = Ansi.Rgb(0x8f, 0xcf, 0x8f),
        Danger = Ansi.Rgb(0xd0, 0x6c, 0x6c),
        // Muted relative to Catppuccin, where a full-strength mauve would read as a
        // third accent rather than as the secondary tone.
        Mauve = Ansi.Rgb(0xb7, 0x9a, 0xcb),
    };
}

internal static class Ansi
{
    public const string Reset = "\x1b[0m";

    public static string Rgb(int red, int green, int blue) => $"\x1b[38;2;{red};{green};{blue}m";
}

/// <summary>Paints the viewport with the theme's window brush.</summary>
internal static class Background
{
    public static string Sequence(string rgbTriplet) => $"\x1b[48;2;{rgbTriplet}m";
}
