using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
        ("pwsh",     "pwsh",     "Microsoft.PowerShell"),
    ];

    // Installed like the rest, but never a reason to stop: the runner pane falls back to
    // Windows PowerShell and then cmd, so Helide runs without it.
    private const string PreferredOnly = "pwsh";

    // The agents Helide can run in its agent pane. None is required: the pane is off until
    // it is switched on from the welcome screen, and it offers whichever of these it finds.
    // So they are reported and never installed, and a machine with none of them is ready.
    private static readonly (string Label, string Exe)[] OptionalAgents =
    [
        ("opencode", "opencode"),
        ("codex",    "codex"),
        ("claude",   "claude"),
    ];

    // The five phases are still the unit of work; the menu offers them in the groups people
    // actually want. Installing tools without repairing the PATH they land on, or building
    // without installing, were two separate trips through the menu for one intention.
    private enum Workflow { Setup, FixTools, BuildInstall, BuildOnly, CheckOnly }

    private static readonly (string Label, Workflow Flow)[] MenuItems =
    [
        ("Set up everything",      Workflow.Setup),
        ("Fix tools",              Workflow.FixTools),
        ("Build & install Helide", Workflow.BuildInstall),
    ];

    private const string StepCheck = "Check tools";
    private const string StepInstall = "Install missing tools";
    private const string StepPath = "Repair PATH";
    private const string StepBuildApp = "Build Helide";
    private const string StepBuildUpdater = "Build AutoUpdater";
    private const string StepInstallApp = "Install Helide";
    private const string StepInstallUpdater = "Install AutoUpdater";

    // Shorter than the shell manual it replaces, because the pane it renders into is a
    // fixed height and the rest is reached by scrolling.
    private const string Manual = """
        Helide check & install

        Checks the CLIs Helide shells out to, installs what is missing, repairs the user PATH, publishes the single-file builds, and installs them.

        USAGE
            CheckNInstall            interactive (this one)
            CheckNInstall --check    report only, exit 0 when ready
            CheckNInstall --full     set up everything, no prompts

        WORKFLOWS
            Set up everything        check, install missing tools, repair PATH, build, install
            Fix tools                check, winget install what is missing, repair PATH
            Build & install Helide   publish Helide and AutoUpdater, then install both

        KEYS
            Up / Down  move          Enter    run the selected workflow
            1 - 3      run directly  B        build only, without installing
            R          re-check      ?        this manual
            T          theme         Esc      quit

        PHASES
            1  Check          lazygit, hx, yazi. The agents (opencode, codex, claude) are optional and only reported.
            2  Install        winget install each missing one. Agents are never installed.
            3  Repair PATH    The real directory under WinGet\Packages is added to the user PATH, and refused past 2000 characters.
            4  Build          dotnet publish, Helide then AutoUpdater, excluded from Helide.csproj. PublishSingleFile is -time only, so a plain build cannot make it.
            5  Install        %LOCALAPPDATA%\Programs\Helide\Helide.exe, on the user PATH, with a Start Menu entry. AutoUpdater.exe beside it.

        NOTES
            Restart the shell after any phase that touched PATH. Helide.exe and AutoUpdater.exe are the only files installed. This tool is for building and installing; it is never copied anywhere.
        """;

    private static string _repoRoot = AppContext.BaseDirectory;
    private static bool _selfContained = true;
    private static bool _shellRestartNeeded;

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

        // The two non-interactive entry points. In a real console they draw the same
        // frame as the menu and run one workflow in it; without one -- a pipe, a build
        // script, CI -- they fall back to plain lines, where a repainting TUI is worse
        // than useless and reading the window size is not possible at all.
        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
            return RunHeadless(Workflow.CheckOnly);

        if (args.Contains("--full", StringComparer.OrdinalIgnoreCase))
            return RunHeadless(Workflow.Setup);

        // No --help: `dotnet run --help` is answered by dotnet itself and never reaches
        // this exe. The manual is in the interactive view, on `?`.
        return RunInteractive();
    }

    // One workflow, no menu. Drawn when there is a console to draw in, plain otherwise.
    private static int RunHeadless(Workflow flow)
    {
        if (!TryEnterTui())
        {
            if (flow == Workflow.CheckOnly) Banner(showLogo: false);
            return RunWorkflow(flow);
        }

        _theme = LoadTheme() == "oled" ? Theme.Oled : Theme.Mocha;
        _headless = true;
        _mode = Mode.Run;
        _running = true;
        _title = flow == Workflow.CheckOnly ? "Check" : "Set up everything";
        ClearLog();

        var code = RunWorkflow(flow);

        lock (_renderLock)
        {
            _running = false;
            _succeeded = code == ExitOk;
            SkipPending();
            _dirty = true;
        }

        // Held for a key so the result can be read, but only when someone is there to
        // press one: redirected input means a script, and a script must never block.
        if (!Console.IsInputRedirected)
        {
            try { while (Console.KeyAvailable) Console.ReadKey(intercept: true); } catch { }
            Browse();
        }

        LeaveTui();

        // The frame went with the alternate screen, so the verdict is left behind in the
        // scrollback in the same form the plain output uses.
        if (_verdict.Length > 0) Console.WriteLine($"  [{(_succeeded ? "ok" : "FAILED")}] {_verdict}");
        if (_shellRestartNeeded) Console.WriteLine("  restart your shell for PATH changes to apply");

        return code;
    }

    private static bool _headless;
    private static string _verdict = string.Empty;

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
        _snapshot = TakeSnapshot();

        while (true)
        {
            var flow = Menu();
            if (flow is null) break;

            Execute(flow.Value);
        }

        LeaveTui();

        // The frame is gone with the alternate screen, so the one thing the user has to
        // act on is said again where they will see it.
        if (_shellRestartNeeded)
            Console.WriteLine("restart your shell for PATH changes to apply");

        return ExitOk;
    }

    private static void Execute(Workflow flow)
    {
        _mode = Mode.Run;
        _running = true;
        _scroll = 0;
        _title = flow == Workflow.BuildOnly
            ? "Build only"
            : MenuItems.First(item => item.Flow == flow).Label;
        ClearLog();

        var code = RunWorkflow(flow);

        lock (_renderLock)
        {
            _running = false;
            _succeeded = code == ExitOk;
            SkipPending();
            _snapshot = TakeSnapshot();
            _dirty = true;
        }

        // Keys pressed while it worked would otherwise dismiss the result unread.
        try { while (Console.KeyAvailable) Console.ReadKey(intercept: true); } catch { }

        Browse();
    }

    // ------------------------------------------------------------------ workflows

    private static int RunWorkflow(Workflow flow)
    {
        switch (flow)
        {
            case Workflow.CheckOnly:
            {
                // Report only: nothing is installed, repaired or built.
                Plan(StepCheck);
                var missing = false;
                Do(StepCheck, () => { missing = Check(); return true; });

                if (missing) Error("one or more tools are missing");
                else Ok("ready to install");
                return missing ? ExitFailed : ExitOk;
            }

            case Workflow.FixTools:
            {
                Plan(StepCheck, StepInstall, StepPath);
                var unresolved = FixTools();

                if (unresolved.Remove(PreferredOnly))
                    Warn("pwsh is not installed; the runner pane will use Windows PowerShell or cmd");

                if (unresolved.Count > 0)
                {
                    foreach (var label in unresolved)
                        Emit("·", $"winget install -e --id {WingetFor(label)}");
                    Error("still missing");
                    return ExitFailed;
                }

                Ok("all required tools are on PATH");
                return ExitOk;
            }

            case Workflow.BuildInstall:
                Plan(StepBuildApp, StepBuildUpdater, StepInstallApp, StepInstallUpdater);
                return BuildAndInstall();

            case Workflow.BuildOnly:
                Plan(StepBuildApp, StepBuildUpdater);
                if (!BuildSteps()) { Error("publish failed"); return ExitFailed; }
                Ok("publish succeeded");
                return ExitOk;

            default:
            {
                Plan(StepCheck, StepInstall, StepPath,
                    StepBuildApp, StepBuildUpdater, StepInstallApp, StepInstallUpdater);

                var unresolved = FixTools();

                if (unresolved.Remove(PreferredOnly))
                    Warn("pwsh is not installed; the runner pane will use Windows PowerShell or cmd");

                // Refuse to build against a machine the app cannot run on: every phase after
                // this would succeed and produce an install that fails on first use.
                if (unresolved.Count > 0)
                {
                    foreach (var label in unresolved)
                        Emit("·", $"winget install -e --id {WingetFor(label)}");
                    Error("still missing, so the install would not run");
                    return ExitFailed;
                }

                return BuildAndInstall();
            }
        }
    }

    // Check, then install and repair only what that found missing. Returns the labels
    // that are still unresolved afterwards.
    private static List<string> FixTools()
    {
        var anyMissing = false;
        Do(StepCheck, () => { anyMissing = Check(); return true; });

        if (!anyMissing)
        {
            Skip(StepInstall, "nothing missing");
            Skip(StepPath, "nothing to repair");
            return [];
        }

        Do(StepInstall, () => InstallTools(FindMissing()));

        var unresolved = FindMissing();
        if (unresolved.Count == 0)
        {
            Skip(StepPath, "already resolved");
            return unresolved;
        }

        Do(StepPath, () => RepairPath(unresolved));
        return FindMissing();
    }

    private static bool BuildSteps()
    {
        if (!Do(StepBuildApp, BuildApp)) return false;
        return Do(StepBuildUpdater, BuildUpdater);
    }

    private static int BuildAndInstall()
    {
        if (!BuildSteps())
        {
            Error("publish failed");
            return ExitFailed;
        }

        Ok("publish succeeded");

        var installedApp = Do(StepInstallApp, InstallApp);
        var installedUpdater = Do(StepInstallUpdater, InstallUpdater);

        if (!installedApp || !installedUpdater) return ExitFailed;

        Ok("done. restart your shell, then run: helide");
        return ExitOk;
    }

    // ------------------------------------------------------------------ steps

    private enum StepState { Pending, Active, Done, Failed, Skipped }

    private sealed class Step
    {
        public string Label = string.Empty;
        public StepState State;
        public string Detail = string.Empty;
    }

    private static readonly List<Step> _steps = [];

    private static void Plan(params string[] labels)
    {
        lock (_renderLock)
        {
            _steps.Clear();
            foreach (var label in labels) _steps.Add(new Step { Label = label });
            _dirty = true;
        }
    }

    private static void SetStep(string label, StepState state, string detail = "")
    {
        lock (_renderLock)
        {
            var step = _steps.FirstOrDefault(s => s.Label == label);
            if (step is null) return;

            step.State = state;
            step.Detail = detail;
            _dirty = true;
        }
    }

    private static void Skip(string label, string reason) => SetStep(label, StepState.Skipped, reason);

    // Steps that will not run now read as skipped rather than as work still to come.
    private static void SkipPending()
    {
        lock (_renderLock)
        {
            foreach (var step in _steps.Where(s => s.State == StepState.Pending))
                step.State = StepState.Skipped;
            _dirty = true;
        }
    }

    // Runs one phase under its checklist row. Without a TUI the phase name is printed, as
    // the plain output always did.
    private static bool Do(string label, Func<bool> body)
    {
        SetStep(label, StepState.Active);
        if (!_tui) Emit("·", label);

        var ok = false;
        try { ok = body(); }
        catch (Exception ex) { Emit("FAILED", ex.Message); }

        SetStep(label, ok ? StepState.Done : StepState.Failed);
        return ok;
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

        foreach (var (label, exe) in OptionalAgents)
            Emit(IsOnPath(exe) ? "ok" : "skipped", $"{label,-9} -> {exe}  (optional agent)");

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

    private static bool InstallTools(List<string> labels)
    {
        if (labels.Count == 0) { Note("nothing to install"); return true; }

        if (!WingetAvailable())
        {
            Error("winget is not available, so nothing can be installed automatically");
            Note("install App Installer from the Microsoft Store, then re-run");
            return false;
        }

        foreach (var label in labels)
        {
            Emit("install", $"winget install -e --id {WingetFor(label)}");
            Run("winget",
                $"install -e --id {WingetFor(label)} --accept-package-agreements --accept-source-agreements");
        }

        _shellRestartNeeded = true;
        Note("restart your shell for the new installs to reach PATH");
        return true;
    }

    /// winget exposes portable packages through %LOCALAPPDATA%\Microsoft\WinGet\Links,
    /// which can end up empty even when the package installed correctly. The install
    /// directory is added to the user PATH instead of chasing that.
    private static bool RepairPath(List<string> labels)
    {
        if (labels.Count == 0) { Note("nothing to repair"); return true; }

        var allFixed = true;

        foreach (var label in labels)
        {
            var exe = ExeFor(label);
            if (IsOnPath(exe)) { Emit("ok", $"{label,-9} resolved elsewhere"); continue; }

            var dir = FindWingetInstallDir(exe);
            if (dir is null)
            {
                Emit("FAILED", $"{label,-9} not found under WinGet\\Packages");
                allFixed = false;
                continue;
            }

            switch (AddToUserPath(dir))
            {
                case "added":
                    _shellRestartNeeded = true;
                    Emit("fixed", $"{label,-9} + {dir}");
                    break;
                case "present":
                    Emit("ok", $"{label,-9} already on user PATH");
                    break;
                default:
                    Emit("FAILED", $"{label,-9} could not add {dir}");
                    allFixed = false;
                    break;
            }
        }

        Note("restart your shell for PATH changes to apply");
        return allFixed;
    }

    private static string ExeFor(string label)
    {
        foreach (var requirement in Requirements)
            if (requirement.Label == label) return requirement.Exe;
        return label;
    }

    private static bool BuildApp()
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
        return Run("dotnet", $"publish \"{AppProject}\" -c Release -r win-x64 {selfContained}", _repoRoot) == 0;
    }

    private static bool BuildUpdater()
    {
        // The updater is its own project and is excluded from Helide.csproj, so nothing
        // above built it.
        if (!File.Exists(UpdaterProject))
        {
            Note("no AutoUpdater.csproj under tools\\, skipping");
            return true;
        }

        var selfContained = _selfContained ? "--self-contained" : "--self-contained:false";

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
                _shellRestartNeeded = true;
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

    // ------------------------------------------------------------------ snapshot

    // What the menu shows before anything is run, so the first screen answers "what state
    // is this machine in" instead of making the user pick "Check" to find out.
    private sealed record Snapshot(
        List<(string Label, string Exe, bool Found)> Tools,
        List<(string Label, bool Found)> Agents,
        string? AppVersion,
        bool UpdaterInstalled,
        bool Built);

    private static Snapshot _snapshot = new([], [], null, false, false);

    private static Snapshot TakeSnapshot()
    {
        var tools = Requirements.Select(r => (r.Label, r.Exe, IsOnPath(r.Exe))).ToList();
        var agents = OptionalAgents.Select(a => (a.Label, IsOnPath(a.Exe))).ToList();

        string? version = null;
        try
        {
            var exe = Path.Combine(InstallDir, "Helide.exe");
            if (File.Exists(exe))
            {
                version = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "?";
                var plus = version.IndexOf('+');
                if (plus >= 0) version = version[..plus];
                if (version.EndsWith(".0", StringComparison.Ordinal) && version.Count(c => c == '.') == 3)
                    version = version[..^2];
            }
        }
        catch { version = "?"; }

        return new Snapshot(
            tools,
            agents,
            version,
            File.Exists(Path.Combine(InstallDir, "AutoUpdater.exe")),
            File.Exists(AppArtifact));
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsOnPath(string exe)
    {
        // This process's PATH is a copy taken when it started, so it cannot see anything
        // an install or a repair added since -- which made a full run install all three
        // tools and then report all three as still missing. The saved machine and user
        // values are what the next shell will get, so they are searched as well.
        var path = string.Join(';',
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));

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

        // The value as it is stored, not as it expands. The user PATH is an expandable
        // string and routinely holds references such as %USERPROFILE%; reading it through
        // Environment expands them, so writing the result back would bake every one of
        // them in for good. It also made the length check below measure the wrong thing:
        // a stored "%PATH%;C:\tools" is 14 characters, but expands to the whole PATH and
        // was refused as four thousand.
        string current;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
            current = key?.GetValue(
                "Path", string.Empty,
                Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        }
        catch (Exception)
        {
            return "failed";
        }

        var entries = current
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Compared after expansion and without a trailing slash, so an entry written as
        // %LOCALAPPDATA%\Programs\Helide\ still counts as the directory it names.
        static string Normalise(string entry) =>
            Environment.ExpandEnvironmentVariables(entry).TrimEnd('\\', '/');

        if (entries.Any(entry => string.Equals(
                Normalise(entry), Normalise(directory), StringComparison.OrdinalIgnoreCase)))
            return "present";

        // A PATH that refers to itself is somebody's shell command gone wrong -- "%PATH%"
        // typed where it was not expanded -- and it doubles every entry in each new
        // shell. It is reported, not removed: it is the user's value, and what the rest
        // of it should have been is not something this tool can know.
        if (entries.Any(entry => string.Equals(entry, "%PATH%", StringComparison.OrdinalIgnoreCase)))
            Warn("user PATH contains a literal %PATH% entry, which repeats the whole PATH; worth removing by hand");

        // Guard the length before writing. A truncated PATH breaks every shell on the
        // machine, so refuse rather than write something partial.
        var combined = string.Join(';', entries.Concat(new[] { directory }));
        if (combined.Length > 2000)
        {
            Warn("user PATH would reach " + combined.Length + " chars; refusing");
            return "failed";
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Environment");
            key.SetValue("Path", combined, Microsoft.Win32.RegistryValueKind.ExpandString);
        }
        catch (Exception)
        {
            return "failed";
        }

        // Written to the registry directly, so the notification Environment would have
        // sent has to be sent here: without it Explorer keeps handing the old PATH to
        // everything it starts until the next sign-in.
        BroadcastEnvironmentChange();
        return "added";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, UIntPtr wParam, string lParam,
        uint flags, uint timeoutMilliseconds, out UIntPtr result);

    private static void BroadcastEnvironmentChange()
    {
        const uint settingChange = 0x001A;
        const uint abortIfHung = 0x0002;
        var everyTopLevelWindow = new IntPtr(0xFFFF);

        try
        {
            SendMessageTimeout(
                everyTopLevelWindow, settingChange, UIntPtr.Zero, "Environment",
                abortIfHung, 2000, out _);
        }
        catch (Exception)
        {
            // A window that never answers costs the notification, not the change.
        }
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

    /// Walks up from the executable until it finds the repo root, so the tool works
    /// whether it was run from bin, from tools\, or from the publish folder.
    ///
    /// The starting point is the real exe rather than the app base directory: a
    /// single-file build unpacks its assemblies into a temp folder and points
    /// AppContext.BaseDirectory there, so walking up from that never leaves %TEMP% and
    /// every phase then reports a missing Helide.csproj.
    private static string FindRepoRoot()
    {
        var start = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? AppContext.BaseDirectory;

        var directory = new DirectoryInfo(Path.GetDirectoryName(start)!);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Helide.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return Path.GetDirectoryName(start) ?? AppContext.BaseDirectory;
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

    private enum Mode { Menu, Run, Manual }

    // Every glyph below is ASCII, Latin-1, single-line box drawing or a block element.
    // Rounded corners, braille, arrows and the geometric circles are missing from common
    // terminal fonts and come out as a "?" or as nothing at all.
    private static readonly string[] Spinner = ["|", "/", "-", "\\"];

    private const string Bold = "\x1b[1m";
    private const int FrameRows = 2;
    private const int FrameColumns = 4;
    private const int LogoMargin = 4;
    private const int Pad = 2;
    private const int MinimumRightInner = 46;
    private const int MaximumContent = 76;

    private static bool _tui;
    private static Mode _mode = Mode.Menu;
    private static Theme _theme = Theme.Mocha;

    // Rows the log has been scrolled up from its tail. Zero pins it to the bottom.
    private static int _scroll;

    private static int _width;
    private static int _height;
    private static int _selected;
    private static int _frame;
    private static bool _dirty = true;
    private static bool _running;
    private static bool _succeeded;
    private static string _title = string.Empty;

    private static readonly List<(string Tag, string Message)> _log = [];
    private static readonly object _renderLock = new();
    private static string[] _logo = [];
    private static int _logoWidth;
    private static Timer? _ticker;

    // Held only so it can be unsubscribed on the way out.
    private static ConsoleCancelEventHandler? _cancelHandler;

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

        _logo = CropMark(EmbeddedLogo().Split('\n'));
        _logoWidth = _logo.Length == 0 ? 0 : _logo[0].Length;
        _tui = true;

        // The frame is drawn on the terminal's alternate screen, which is what a full
        // screen program is meant to use: nothing scrolls behind it, and leaving throws
        // it away and puts the shell's own scrollback back exactly as it was.
        Console.Write("\x1b[?1049h");
        Console.Write(Background.Sequence(Theme.Mocha.WindowAsRgb));
        Console.Write("\x1b[2J\x1b[H");
        Console.CursorVisible = false;

        // Ctrl-C is delivered as a console event rather than a key, and the default
        // behaviour tears the process down on the spot -- so the cursor stays hidden, the
        // palette stays set, and the frame is still on screen when the next prompt draws
        // over it. Taking the event and cleaning up on the way out is what keeps the
        // shell usable.
        _cancelHandler = OnCancelKeyPress;
        Console.CancelKeyPress += _cancelHandler;

        // One heartbeat for spinner animation and for lines produced on other threads.
        // A winget install can be silent for a minute, and a frame that only repainted
        // on output looked frozen for exactly that long.
        _ticker = new Timer(Tick, null, 80, 80);
        return true;
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Cancelled so this handler gets to finish; the process is exited by hand a line
        // later rather than being allowed to die in the middle of a frame.
        e.Cancel = true;

        LeaveTui();
        Environment.Exit(ExitOk);
    }

    private static void LeaveTui()
    {
        lock (_renderLock)
        {
            if (!_tui) return;
            _tui = false;
        }

        _ticker?.Dispose();
        _ticker = null;

        if (_cancelHandler is not null)
        {
            Console.CancelKeyPress -= _cancelHandler;
            _cancelHandler = null;
        }

        // The brush was set with a raw escape, which ResetColor knows nothing about, so it
        // is undone first. Dropping the alternate screen then discards the frame and
        // restores whatever the shell had before.
        Console.Write(Ansi.Reset);
        Console.ResetColor();
        Console.CursorVisible = true;
        Console.Write("\x1b[?1049l");
    }

    // Cuts the mark out of its frame and trims the empty margin around the hourglass.
    private static string[] CropMark(string[] source)
    {
        var rows = source
            .Select(line => line.TrimEnd('\r'))
            .Skip(FrameRows)
            .Take(Math.Max(0, source.Length - 2 * FrameRows))
            .Select(line => line.Length > 2 * FrameColumns
                ? line[FrameColumns..^FrameColumns]
                : string.Empty)
            .ToList();

        while (rows.Count > 0 && rows[0].Trim().Length == 0) rows.RemoveAt(0);
        while (rows.Count > 0 && rows[^1].Trim().Length == 0) rows.RemoveAt(rows.Count - 1);
        if (rows.Count == 0) return [];

        var inked = rows.Where(row => row.Trim().Length > 0).ToList();
        var left = inked.Min(row => row.Length - row.TrimStart().Length);
        var right = inked.Max(row => row.TrimEnd().Length);
        if (right <= left) return [];

        return rows.Select(row => row.PadRight(right)[left..right]).ToArray();
    }

    /// Collapses the buffer to the window so the terminal has nothing to scroll.
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

    private static void Tick(object? _)
    {
        lock (_renderLock)
        {
            if (!_tui) return;

            _frame++;

            var animating = _running && _steps.Any(step => step.State == StepState.Active);
            var resized = false;
            try { resized = Console.WindowWidth != _width || Console.WindowHeight != _height; } catch { }

            if (_dirty || animating || resized) Render();
        }
    }

    private static void Render()
    {
        lock (_renderLock)
        {
            if (!_tui) return;

            LockViewport();
            _dirty = false;

            // One write per frame, positioned with escapes.
            Console.Write(BuildFrame());
        }
    }

    // ------------------------------------------------------------------ layout

    private readonly record struct Cell(string Text, string Style);

    private static readonly Cell[] Blank = [];

    private static int LogoPane => _logoWidth + 2 * LogoMargin;

    private static string BuildFrame()
    {
        var sb = new StringBuilder(_width * _height * 4);
        sb.Append("\x1b[0m").Append(Background.Sequence(_theme.WindowAsRgb));

        if (_width < 40 || _height < 9)
        {
            for (var row = 0; row < _height; row++)
            {
                MoveTo(sb, row);
                AppendCell(sb, _width, row == 0 ? [new Cell("helide check & install", _theme.Mauve)] : Blank, 0);
            }

            sb.Append("\x1b[1;1H");
            return sb.ToString();
        }

        var border = _theme.Border;
        var withLogo = _logoWidth > 0
            && _width - 3 - LogoPane >= MinimumRightInner
            && _height - 3 >= _logo.Length;

        var leftInner = withLogo ? LogoPane : 0;
        var rightInner = withLogo ? _width - 3 - leftInner : _width - 2;
        var rows = _height - 3;

        var pane = BuildPane(Math.Min(rightInner - 2 * Pad, MaximumContent), rows);
        var offset = withLogo ? Math.Max(0, (rows - _logo.Length) / 2) : 0;

        // Top rule: brand on the left, the palette on the right.
        MoveTo(sb, 0);
        var title = new[]
        {
            new Cell("┌─ ", border),
            new Cell("helide", Bold + _theme.Mauve),
            new Cell(" check & install ", _theme.TextMuted),
        };
        var palette = new[]
        {
            new Cell(" " + _theme.Name + " ", _theme.TextFaint),
            new Cell("─┐", border),
        };
        var gap = Math.Max(1, _width - title.Sum(c => c.Text.Length) - palette.Sum(c => c.Text.Length));
        foreach (var cell in title) AppendRun(sb, cell);
        AppendRun(sb, new Cell(new string('─', gap), border));
        foreach (var cell in palette) AppendRun(sb, cell);

        for (var row = 0; row < rows; row++)
        {
            MoveTo(sb, row + 1);
            AppendRun(sb, new Cell("│", border));

            if (withLogo)
            {
                var index = row - offset;
                var mark = index >= 0 && index < _logo.Length
                    ? new[] { new Cell(_logo[index], Gradient(_logo.Length <= 1 ? 0 : index / (double)(_logo.Length - 1))) }
                    : Blank;

                AppendCell(sb, leftInner, mark, LogoMargin);
                AppendRun(sb, new Cell("│", border));
            }

            AppendCell(sb, rightInner, row < pane.Count ? pane[row] : Blank, Pad);
            AppendRun(sb, new Cell("│", border));
        }

        MoveTo(sb, _height - 2);
        AppendRun(sb, new Cell(
            withLogo
                ? "└" + new string('─', leftInner) + "┴" + new string('─', rightInner) + "┘"
                : "└" + new string('─', rightInner) + "┘",
            border));

        // Keyboard hints sit outside the frame, so the box stays a pure status surface.
        MoveTo(sb, _height - 1);
        AppendCell(sb, _width, Hints(), 0);

        // Parked on the first cell on purpose: a cursor left on the final cell is what
        // makes a console scroll, even when the buffer already fits.
        sb.Append("\x1b[1;1H");
        return sb.ToString();
    }

    private static Cell[] Hints()
    {
        var key = _theme.TextMuted;
        var text = _theme.TextFaint;

        if (_mode == Mode.Run && _running)
            return [new Cell(" working", key), new Cell("   Ctrl+C", key), new Cell(" quit", text)];

        if (_mode != Mode.Menu)
        {
            var more = _scroll > 0 ? $"   [ {_scroll} more above ]" : string.Empty;
            return
            [
                new Cell(" Up/Down", key), new Cell(" scroll   ", text),
                new Cell("PgUp/PgDn", key), new Cell(" page   ", text),
                new Cell("any other key", key), new Cell((_headless ? " close" : " back") + more, text),
            ];
        }

        return
        [
            new Cell(" Up/Down", key), new Cell(" move   ", text),
            new Cell("Enter", key), new Cell(" run   ", text),
            new Cell("1-" + MenuItems.Length, key), new Cell(" jump   ", text),
            new Cell("R", key), new Cell(" recheck   ", text),
            new Cell("?", key), new Cell(" manual   ", text),
            new Cell("T", key), new Cell(" theme   ", text),
            new Cell("Esc", key), new Cell(" quit", text),
        ];
    }

    private static List<Cell[]> BuildPane(int width, int rows) => _mode switch
    {
        Mode.Menu => MenuPane(width),
        Mode.Manual => ManualPane(width, rows),
        _ => RunPane(width, rows),
    };

    private static Cell[] Rule(string label, int width) =>
    [
        new Cell(label + " ", _theme.TextFaint),
        new Cell(new string('─', Math.Max(0, width - label.Length - 1)), _theme.Border),
    ];

    private static (string Glyph, string Style) Glyph(bool ok, bool required = true) =>
        ok ? ("√", _theme.Success)
           : required ? ("×", _theme.Danger) : ("-", _theme.TextFaint);

    // What the machine looks like and what can be done about it, in that order of need:
    // the verdict, the actions, then the detail behind the verdict.
    private static List<Cell[]> MenuPane(int width)
    {
        var lines = new List<Cell[]> { Blank };
        var snap = _snapshot;

        var missing = snap.Tools.Where(t => !t.Found && t.Label != PreferredOnly).Select(t => t.Label).ToList();

        lines.Add([new Cell("Helide check & install", Bold + _theme.Accent)]);
        lines.Add(missing.Count == 0
            ? [new Cell("√ ", _theme.Success), new Cell("every required tool is on PATH", _theme.TextMuted)]
            : [new Cell("× ", _theme.Danger),
               new Cell($"{missing.Count} required tool{(missing.Count == 1 ? "" : "s")} missing: {string.Join(", ", missing)}", _theme.Danger)]);
        lines.Add(Blank);

        for (var index = 0; index < MenuItems.Length; index++)
        {
            var selected = index == _selected;
            var label = MenuItems[index].Label.PadRight(24);

            lines.Add(
            [
                new Cell(selected ? "> " : "  ", _theme.Accent),
                new Cell($"{index + 1}  ", selected ? _theme.Text : _theme.TextFaint),
                new Cell(label, selected ? Bold + _theme.Text : _theme.TextMuted),
                new Cell(Hint(MenuItems[index].Flow, missing), _theme.TextFaint),
            ]);
        }

        lines.Add(Blank);
        lines.Add(Rule("tools", width));

        foreach (var (label, exe, found) in snap.Tools)
        {
            var required = label != PreferredOnly;
            var (glyph, style) = Glyph(found, required);
            lines.Add(
            [
                new Cell(glyph + "  ", style),
                new Cell(label.PadRight(10), found ? _theme.TextMuted : _theme.Text),
                new Cell(exe.PadRight(10), _theme.TextFaint),
                new Cell(found ? string.Empty : required ? "missing" : "optional, not installed",
                    required ? _theme.Danger : _theme.TextFaint),
            ]);
        }

        var agents = new List<Cell>();
        foreach (var (label, found) in snap.Agents)
        {
            var (glyph, style) = Glyph(found, required: false);
            agents.Add(new Cell(glyph + " ", style));
            agents.Add(new Cell(label.PadRight(10), found ? _theme.TextMuted : _theme.TextFaint));
        }

        lines.Add(Blank);
        lines.Add(Rule("agents (optional)", width));
        lines.Add(agents.ToArray());

        lines.Add(Blank);
        lines.Add(Rule("helide", width));

        lines.Add(
        [
            new Cell((snap.AppVersion is null ? "-" : "√") + "  ", snap.AppVersion is null ? _theme.TextFaint : _theme.Success),
            new Cell("app".PadRight(10), _theme.TextMuted),
            new Cell(snap.AppVersion is null ? "not installed" : $"v{snap.AppVersion}", _theme.TextFaint),
        ]);
        lines.Add(
        [
            new Cell((snap.UpdaterInstalled ? "√" : "-") + "  ", snap.UpdaterInstalled ? _theme.Success : _theme.TextFaint),
            new Cell("updater".PadRight(10), _theme.TextMuted),
            new Cell(snap.UpdaterInstalled ? "installed" : "not installed", _theme.TextFaint),
        ]);
        lines.Add(
        [
            new Cell((snap.Built ? "√" : "-") + "  ", snap.Built ? _theme.Success : _theme.TextFaint),
            new Cell("build".PadRight(10), _theme.TextMuted),
            new Cell(snap.Built ? "published exe present" : "not built yet", _theme.TextFaint),
        ]);
        lines.Add(
        [
            new Cell("   ", _theme.TextFaint),
            new Cell("source".PadRight(10), _theme.TextMuted),
            new Cell(_repoRoot, _theme.TextFaint),
        ]);

        return lines;
    }

    private static string Hint(Workflow flow, List<string> missing) => flow switch
    {
        Workflow.Setup => missing.Count > 0
            ? "fix tools, then build and install"
            : _snapshot.AppVersion is null ? "build and install Helide" : "rebuild and reinstall",
        Workflow.FixTools => missing.Count > 0 ? "install + repair PATH" : "nothing to fix",
        Workflow.BuildInstall => _snapshot.AppVersion is null ? "first install" : $"installed v{_snapshot.AppVersion}",
        _ => string.Empty,
    };

    private static List<Cell[]> RunPane(int width, int rows)
    {
        var lines = new List<Cell[]> { Blank };

        var (headline, tone) = _running
            ? (_title, _theme.Accent)
            : _succeeded ? ($"{_title}: done", _theme.Success) : ($"{_title}: stopped, see the log", _theme.Danger);
        lines.Add([new Cell(headline, Bold + tone)]);
        lines.Add(Blank);

        foreach (var step in _steps)
        {
            var (glyph, glyphStyle, labelStyle) = step.State switch
            {
                StepState.Active => (Spinner[_frame / 2 % Spinner.Length], _theme.Accent, Bold + _theme.Text),
                StepState.Done => ("√", _theme.Success, _theme.TextMuted),
                StepState.Failed => ("×", _theme.Danger, Bold + _theme.Danger),
                StepState.Skipped => ("-", _theme.TextFaint, _theme.TextFaint),
                _ => ("·", _theme.TextFaint, _theme.TextFaint),
            };

            lines.Add(
            [
                new Cell(glyph + "  ", glyphStyle),
                new Cell(step.Label.PadRight(24), labelStyle),
                new Cell(step.Detail, _theme.TextFaint),
            ]);
        }

        lines.Add(Blank);
        lines.Add(Rule("activity", width));

        // The log is the only part that grows, so it takes whatever rows are left and
        // shows its tail, which is where a build log is always wanted.
        var entries = new List<Cell[]>();
        foreach (var (tag, message) in _log)
        {
            var (glyph, colour, body) = tag switch
            {
                "ok" or "fixed" => ("√", _theme.Success, _theme.TextMuted),
                "missing" or "FAILED" => ("×", _theme.Danger, _theme.Danger),
                "skipped" => ("-", _theme.TextFaint, _theme.TextFaint),
                "!" => ("!", _theme.Mauve, _theme.TextMuted),
                "build" or "install" or "copied" or "link" => (">", _theme.Accent, _theme.TextMuted),
                _ => ("·", _theme.TextFaint, _theme.TextMuted),
            };

            var first = true;
            foreach (var wrapped in Wrap(message, Math.Max(4, width - 2)))
            {
                entries.Add([new Cell(first ? glyph + " " : "  ", colour), new Cell(wrapped, body)]);
                first = false;
            }
        }

        Page(lines, entries, rows);
        return lines;
    }

    private static List<Cell[]> ManualPane(int width, int rows)
    {
        var lines = new List<Cell[]> { Blank, new[] { new Cell("Manual", Bold + _theme.Accent) }, Blank };

        var entries = new List<Cell[]>();
        foreach (var line in Manual.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            var isHeading = text.Length > 0 && text.All(c => char.IsUpper(c) || c == ' ');

            foreach (var wrapped in Wrap(text, width))
                entries.Add([new Cell(wrapped, isHeading ? Bold + _theme.Mauve : _theme.TextMuted)]);

            if (text.Length == 0) entries.Add(Blank);
        }

        Page(lines, entries, rows);
        return lines;
    }

    // Appends the visible window of a scrollable list. Zero is the tail; anything higher
    // has been moved up, and without that nothing longer than the pane is reachable.
    private static void Page(List<Cell[]> lines, List<Cell[]> entries, int rows)
    {
        var available = Math.Max(1, rows - lines.Count);
        var maxScroll = Math.Max(0, entries.Count - available);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        var start = Math.Max(0, entries.Count - available - _scroll);
        lines.AddRange(entries.Skip(start).Take(available));
    }

    // ------------------------------------------------------------------ drawing

    private static string Gradient(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var (fromR, fromG, fromB) = _theme.GradientFrom;
        var (toR, toG, toB) = _theme.GradientTo;

        int Lerp(int from, int to) => (int)Math.Round(from + (to - from) * t);
        return Ansi.Rgb(Lerp(fromR, toR), Lerp(fromG, toG), Lerp(fromB, toB));
    }

    private static void MoveTo(StringBuilder sb, int row) =>
        sb.Append("\x1b[").Append(row + 1).Append(";1H");

    // Colour is set and then only the foreground and weight are reset, so the window
    // brush written at the top of the frame stays on every cell it passes through.
    private static void AppendRun(StringBuilder sb, Cell cell) =>
        sb.Append(cell.Style).Append(cell.Text).Append("\x1b[22;39m");

    // Writes exactly `width` cells: the margin, the runs clipped to what is left, and
    // blanks to the edge. Clipping marks itself so nothing is cut silently.
    private static void AppendCell(StringBuilder sb, int width, Cell[] runs, int pad)
    {
        var room = Math.Max(0, width - 2 * pad);
        sb.Append(' ', Math.Min(pad, width));

        var written = 0;
        foreach (var run in runs)
        {
            var remaining = room - written;
            if (remaining <= 0) break;

            var text = run.Text;
            if (text.Length > remaining)
                text = remaining > 2 ? text[..(remaining - 2)] + ".." : text[..remaining];

            AppendRun(sb, new Cell(text, run.Style));
            written += text.Length;
        }

        sb.Append(' ', Math.Max(0, width - Math.Min(pad, width) - written));
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
        var budget = Math.Max(8, width - 8);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { lines.Add(string.Empty); continue; }

            var trimmed = line.TrimStart(' ');
            var indent = line[..^trimmed.Length];
            var current = string.Empty;

            // Empty tokens are the extra spaces of an aligned column; dropping them is
            // what flattened "name      description" into "name description".
            foreach (var word in trimmed.Split(' '))
            {
                if (word.Length == 0)
                {
                    if (current.Length > 0) current += " ";
                    continue;
                }

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

    private static void ClearLog()
    {
        lock (_renderLock)
        {
            _log.Clear();
            _scroll = 0;
            _dirty = true;
        }
    }

    // ------------------------------------------------------------------ input

    // Returns the workflow to run, or null to quit.
    private static Workflow? Menu()
    {
        _mode = Mode.Menu;
        _scroll = 0;
        _dirty = true;
        Render();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.KeyChar >= '1' && key.KeyChar < '1' + MenuItems.Length)
            {
                _selected = key.KeyChar - '1';
                return MenuItems[_selected].Flow;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    _selected = (_selected - 1 + MenuItems.Length) % MenuItems.Length;
                    break;
                case ConsoleKey.DownArrow:
                    _selected = (_selected + 1) % MenuItems.Length;
                    break;
                case ConsoleKey.Enter:
                    return MenuItems[_selected].Flow;
                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    return null;
                case ConsoleKey.B:
                    return Workflow.BuildOnly;
                case ConsoleKey.R:
                    _snapshot = TakeSnapshot();
                    break;
                case ConsoleKey.T:
                    _theme = _theme == Theme.Mocha ? Theme.Oled : Theme.Mocha;
                    break;
                case ConsoleKey.F1:
                case ConsoleKey.H:
                    ShowManualView();
                    _mode = Mode.Menu;
                    break;
                default:
                    if (key.KeyChar == '?')
                    {
                        ShowManualView();
                        _mode = Mode.Menu;
                    }
                    else continue;
                    break;
            }

            _dirty = true;
            Render();
        }
    }

    private static void ShowManualView()
    {
        _mode = Mode.Manual;
        _running = false;

        // The pane pins to its tail by default, and a manual is read from the top, so it
        // starts scrolled as far up as it goes; the first draw clamps that to the real top.
        _scroll = int.MaxValue;
        Browse();
    }

    // Scrolls the current pane until a key that is not a scroll key is pressed.
    private static void Browse()
    {
        _dirty = true;
        Render();

        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            var step = Math.Max(1, (_height - 3) / 2);

            switch (key)
            {
                case ConsoleKey.UpArrow: _scroll++; break;
                case ConsoleKey.DownArrow: _scroll--; break;
                case ConsoleKey.PageUp: _scroll += step; break;
                case ConsoleKey.PageDown:
                case ConsoleKey.Spacebar:
                    _scroll -= step;
                    break;
                case ConsoleKey.Home: _scroll = int.MaxValue; break;
                case ConsoleKey.End: _scroll = 0; break;
                default: return;
            }

            _dirty = true;
            Render();
        }
    }

    // ------------------------------------------------------------------ output

    // One sink for every line the phases produce. In the TUI it lands in the log pane;
    // without a console it goes straight to stdout, which is what the --check and --full
    // entry points want.
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
            _dirty = true;
        }
    }

    private static void Ok(string message)
    {
        _verdict = message;
        Emit("ok", message);
    }

    private static void Error(string message)
    {
        _verdict = message;
        Emit("FAILED", message);
    }

    private static void Warn(string message) => Emit("!", message);

    private static void Note(string message) => Emit("·", message);

    private static void Banner(bool showLogo = true)
    {
        if (_tui) return;

        // --check is the one mode a build script reads, and 27 rows of art ahead of the
        // answer is noise in a log. Everything else keeps it.
        if (showLogo)
        {
            var logo = EmbeddedLogo();
            if (!string.IsNullOrEmpty(logo))
            {
                Console.WriteLine(logo);
                Console.WriteLine();
            }
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
    public required string TextMuted { get; init; }
    public required string TextFaint { get; init; }
    public required string Accent { get; init; }
    public required string Success { get; init; }
    public required string Danger { get; init; }
    public required string Border { get; init; }
    public required string WindowAsRgb { get; init; }

    // The mark runs from one end of this pair to the other, top to bottom.
    public required (int R, int G, int B) GradientFrom { get; init; }
    public required (int R, int G, int B) GradientTo { get; init; }

    public static readonly Theme Mocha = new()
    {
        Name = "mocha",
        // Catppuccin Mocha. Window #D9191724, surfaces lifted from that base.
        WindowAsRgb = "25;23;36",
        Border = Ansi.Rgb(0x45, 0x47, 0x5a),
        Text = Ansi.Rgb(0xcd, 0xd6, 0xf4),
        TextMuted = Ansi.Rgb(0xa6, 0xad, 0xc8),
        TextFaint = Ansi.Rgb(0x6c, 0x70, 0x86),
        Accent = Ansi.Rgb(0x89, 0xb4, 0xfa),
        Success = Ansi.Rgb(0xa6, 0xe3, 0xa1),
        Danger = Ansi.Rgb(0xf3, 0x8b, 0xa8),
        // The Helide mark is mauve, so the palette's secondary column uses the same
        // value rather than a grey.
        Mauve = Ansi.Rgb(0xcb, 0xa6, 0xf7),
        GradientFrom = (0xcb, 0xa6, 0xf7),
        GradientTo = (0x89, 0xb4, 0xfa),
    };

    public static readonly Theme Oled = new()
    {
        Name = "oled",
        // Near-black rather than true black, with the chrome in small greyscale steps.
        WindowAsRgb = "14;14;14",
        Border = Ansi.Rgb(0x34, 0x34, 0x34),
        Text = Ansi.Rgb(0xf2, 0xf2, 0xf2),
        TextMuted = Ansi.Rgb(0xb0, 0xb0, 0xb0),
        TextFaint = Ansi.Rgb(0x6e, 0x6e, 0x6e),
        // Pure white: nothing depends on telling the accent apart from anything else.
        Accent = Ansi.Rgb(0xff, 0xff, 0xff),
        Success = Ansi.Rgb(0x8f, 0xcf, 0x8f),
        Danger = Ansi.Rgb(0xd0, 0x6c, 0x6c),
        // Muted relative to Catppuccin, where a full-strength mauve would read as a
        // third accent rather than as the secondary tone.
        Mauve = Ansi.Rgb(0xb7, 0x9a, 0xcb),
        GradientFrom = (0xb7, 0x9a, 0xcb),
        GradientTo = (0xf2, 0xf2, 0xf2),
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
