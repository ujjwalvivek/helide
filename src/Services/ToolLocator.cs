using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Helide;

/// <summary>One CLI Helide hosts, and the winget package that provides it.</summary>
/// <param name="Label">The name shown on the welcome screen.</param>
/// <param name="Executable">The file a pane launches.</param>
/// <param name="WingetId">The package id <c>winget install</c> takes.</param>
/// <param name="Required">False for a tool Helide has a fallback for.</param>
internal sealed record ManagedTool(string Label, string Executable, string WingetId, bool Required);

/// <summary>
/// Finds the tools Helide runs in its panes wherever a machine has put them, and installs
/// the ones that are missing.
/// </summary>
/// <remarks>
/// A pane is started by <c>CreateProcess</c>, which only searches this process's PATH.
/// That PATH is a copy taken when Helide started, so a tool installed afterwards is
/// invisible to it, and so is one that winget put in a package folder without linking it.
/// Looking in the places a fresh shell would, and then adding what was found to this
/// process's PATH, is what makes "installed it, now it works" true without a restart.
/// </remarks>
internal static class ToolLocator
{
    public static readonly IReadOnlyList<ManagedTool> Tools =
    [
        new("lazygit", "lazygit.exe", "JesseDuffield.lazygit", Required: true),
        new("helix",   "hx.exe",      "Helix.Helix",           Required: true),
        new("yazi",    "yazi.exe",    "sxyazi.yazi",           Required: true),
        new("pwsh",    "pwsh.exe",    "Microsoft.PowerShell",  Required: false),
    ];

    private static string WingetRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WinGet");

    /// <summary>The full path of a tool, or null when it is nowhere Helide can look.</summary>
    public static string? Find(ManagedTool tool)
    {
        // What a pane would find today, then what a new shell would, then winget's own
        // folders. In that order so a tool that is on PATH never costs a directory walk.
        foreach (var directory in SearchDirectories())
        {
            var candidate = Combine(directory, tool.Executable);
            if (candidate is not null && File.Exists(candidate))
                return candidate;
        }

        return FindInPackages(tool);
    }

    public static bool IsInstalled(ManagedTool tool) => Find(tool) is not null;

    /// <summary>The tools that are missing right now, required ones first.</summary>
    public static IReadOnlyList<ManagedTool> Missing() =>
        Tools.Where(tool => !IsInstalled(tool))
            .OrderByDescending(tool => tool.Required)
            .ToArray();

    /// <summary>
    /// Adds the directory of every tool found outside this process's PATH to it, so panes
    /// started from here on can launch them by name.
    /// </summary>
    /// <remarks>
    /// Only this process's copy changes. The user's saved PATH is not touched: Helide
    /// finding its own tools is its business, and rewriting somebody's environment from a
    /// welcome screen is not.
    /// </remarks>
    public static void SyncProcessPath()
    {
        try
        {
            var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var known = new HashSet<string>(
                current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Normalise),
                StringComparer.OrdinalIgnoreCase);

            var additions = new List<string>();
            foreach (var tool in Tools)
            {
                var found = Find(tool);
                var directory = found is null ? null : Path.GetDirectoryName(found);
                if (directory is not null && known.Add(Normalise(directory)))
                    additions.Add(directory);
            }

            if (additions.Count > 0)
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    string.Join(Path.PathSeparator, additions) + Path.PathSeparator + current);
            }
        }
        catch (Exception)
        {
            // A PATH that could not be extended leaves the tools exactly as findable as
            // they were.
        }
    }

    // ------------------------------------------------------------------ install

    /// <summary>Whether <c>winget</c> can be run at all.</summary>
    public static bool WingetAvailable()
    {
        try
        {
            using var process = Process.Start(Hidden("winget", "--version"));
            if (process is null)
                return false;

            process.WaitForExit(15000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // One install at a time, for the whole process, as a queue. The welcome screen and the
    // card in a pane both offer it, and a project can be opened while one is running:
    // a request that arrives mid-install joins the queue instead of being dropped or
    // starting a second winget, and each card can tell its own tool's turn from another's.
    private static readonly object Gate = new();
    private static readonly List<ManagedTool> Queue = [];
    private static Task<IReadOnlyList<ManagedTool>>? _running;
    private static bool _draining;

    /// <summary>True while winget is installing something for Helide.</summary>
    public static bool IsInstalling
    {
        get { lock (Gate) return _draining; }
    }

    /// <summary>The tool being installed right now, or null.</summary>
    public static ManagedTool? Current { get; private set; }

    /// <summary>True when this tool is being installed now or is waiting its turn.</summary>
    public static bool IsPending(ManagedTool tool)
    {
        lock (Gate)
            return _draining && (Current?.Label == tool.Label || Queue.Any(queued => queued.Label == tool.Label));
    }

    /// <summary>True when this tool is the one winget is working on right now.</summary>
    public static bool IsCurrent(ManagedTool tool) => Current?.Label == tool.Label;

    /// <summary>True when the last install could not run because winget is missing.</summary>
    public static bool WingetMissing { get; private set; }

    /// <summary>
    /// Raised when an install starts, moves to the next tool, and ends. From any thread.
    /// </summary>
    public static event Action? InstallStateChanged;

    /// <summary>
    /// Installs the tools, queueing behind the install that is already running if there
    /// is one. Returns the tools that are still not found once the queue has drained.
    /// </summary>
    public static Task<IReadOnlyList<ManagedTool>> InstallAsync(IEnumerable<ManagedTool> tools)
    {
        Task<IReadOnlyList<ManagedTool>> task;
        var requested = tools.ToArray();

        lock (Gate)
        {
            foreach (var tool in requested)
            {
                var known = Current?.Label == tool.Label || Queue.Any(queued => queued.Label == tool.Label);
                if (!known)
                    Queue.Add(tool);
            }

            if (_draining && _running is not null)
            {
                // Joined an install that is already running. Still announced below:
                // returning here used to skip it, so a card that had just queued its tool
                // had nothing telling it to say so, and showed "Install" until something
                // unrelated made it redraw.
                task = _running;
            }
            else
            {
                _draining = true;
                WingetMissing = false;
                task = _running = Task.Run(DrainQueue);
            }
        }

        InstallStateChanged?.Invoke();
        return task;
    }

    private static IReadOnlyList<ManagedTool> DrainQueue()
    {
        var failed = new List<ManagedTool>();

        try
        {
            if (!WingetAvailable())
            {
                WingetMissing = true;
                lock (Gate)
                {
                    failed.AddRange(Queue.Where(tool => !IsInstalled(tool)));
                    Queue.Clear();
                }

                return failed;
            }

            while (true)
            {
                ManagedTool tool;

                // Emptiness is decided under the same lock that decides whether a caller
                // may join: a request cannot be added to a queue that has just been judged
                // finished and then never be looked at.
                lock (Gate)
                {
                    if (Queue.Count == 0)
                    {
                        _draining = false;
                        break;
                    }

                    tool = Queue[0];
                    Queue.RemoveAt(0);
                    Current = tool;
                }

                InstallStateChanged?.Invoke();

                if (!InstallOne(tool))
                    failed.Add(tool);
            }
        }
        finally
        {
            lock (Gate)
            {
                _draining = false;
                Current = null;
            }

            // Off this thread so a listener that reads the state sees it settled.
            Task.Run(() => InstallStateChanged?.Invoke());
        }

        return failed;
    }

    // Judged by whether the tool is there afterwards, not by how winget ended: winget exits
    // non-zero for "already installed" and for "nothing to upgrade", which are both a
    // success here. The file on disk is the only honest answer.
    private static bool InstallOne(ManagedTool tool)
    {
        try
        {
            using var process = Process.Start(Hidden(
                "winget",
                $"install -e --id {tool.WingetId} --silent --disable-interactivity "
                + "--accept-package-agreements --accept-source-agreements"));

            // Drained rather than ignored: an unread redirected pipe fills up and the
            // child then blocks forever.
            process?.BeginOutputReadLine();
            process?.BeginErrorReadLine();
            process?.WaitForExit();
        }
        catch (Exception)
        {
            // Fall through to the check below.
        }

        SyncProcessPath();
        return IsInstalled(tool);
    }

    private static ProcessStartInfo Hidden(string fileName, string arguments) => new(fileName, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    // ------------------------------------------------------------------ lookup

    private static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The saved machine and user values are what the next shell gets, so they are
        // searched alongside this process's copy, which cannot see anything added since.
        string?[] sources =
        [
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Path.Combine(WingetRoot, "Links"),
        ];

        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source))
                continue;

            foreach (var entry in source.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = entry.Trim().Trim('"');
                if (directory.Length > 0 && seen.Add(directory))
                    yield return directory;
            }
        }
    }

    // winget keeps portable packages in a folder named after the package id, and the
    // Links folder that is meant to expose them can be left empty even when the install
    // worked. Only folders for this tool's package are walked: the whole of Packages
    // holds every file of every portable app.
    private static string? FindInPackages(ManagedTool tool)
    {
        try
        {
            var packages = Path.Combine(WingetRoot, "Packages");
            if (!Directory.Exists(packages))
                return null;

            foreach (var folder in Directory.EnumerateDirectories(packages, tool.WingetId + "*"))
            {
                var hit = Directory
                    .EnumerateFiles(folder, tool.Executable, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (hit is not null)
                    return hit;
            }
        }
        catch (Exception)
        {
            // An unreadable folder is a tool that was not found there.
        }

        return null;
    }

    private static string? Combine(string directory, string file)
    {
        try { return Path.Combine(directory, file); }
        catch (ArgumentException) { return null; }
    }

    private static string Normalise(string entry) =>
        Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')).TrimEnd('\\', '/');
}
