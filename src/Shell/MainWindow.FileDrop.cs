using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Helide.Shell;
using Helide.Terminal;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace Helide;

// Files dragged in from outside, onto a pane that can take them:
//   an agent  -- mentions in its input, as the mention shortcut puts them;
//   a terminal -- their full paths at the prompt, quoted for the shell;
//   yazi      -- copied into the folder it is showing.
// Nothing is sent: a mention or a path is only typed in, for you to finish.
public partial class MainWindow
{
    // ---- agent -----------------------------------------------------------------------

    private void AgentSlot_DragOver(object sender, DragEventArgs e) =>
        AcceptDrop(e, AgentUnder(e) is not null);

    private void AgentSlot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var paths = DroppedPaths(e);
        if (paths.Length == 0 || AgentUnder(e) is not { Host: { } host } session)
            return;

        // Relative to where the agent runs, as a mention is; anything outside that folder
        // keeps its full path. A path with a space in it is quoted, or the agent would
        // read it as two words.
        var root = session.WorkingDirectory ?? _currentProject!;
        var mentions = paths.Select(path =>
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                relative = path;

            relative = relative.Replace('\\', '/');
            return "@" + (relative.Contains(' ') ? $"\"{relative}\"" : relative);
        });

        ActivateAgentSession(session);
        if (host.PasteText(string.Join(" ", mentions) + " "))
            FocusPanel(ToolPanel.Agent, host);
    }

    // The agent pane shows one tab at a time: the tab whose host is under the pointer is
    // the selected one, unless the pane is still on a card.
    private AgentSessionView? AgentUnder(DragEventArgs e) =>
        HostUnder(e) is { } host
            ? _agentSessions.FirstOrDefault(session => ReferenceEquals(session.Host, host))
            : null;

    // ---- terminal --------------------------------------------------------------------

    private void RunnerSlot_DragOver(object sender, DragEventArgs e) =>
        AcceptDrop(e, RunnerUnder(e) is not null);

    private void RunnerSlot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var paths = DroppedPaths(e);
        if (paths.Length == 0 || RunnerUnder(e) is not { Host: { } host } session)
            return;

        // Full paths: the shell may have moved since it started. Quoted for PowerShell
        // when anything in them would be read as more than a path.
        ActivateRunnerSession(session);
        if (host.PasteText(string.Join(" ", paths.Select(ShellPath)) + " "))
            FocusPanel(ToolPanel.Runner, host);
    }

    private AgentSessionView? RunnerUnder(DragEventArgs e) =>
        HostUnder(e) is { } host
            ? _runnerSessions.FirstOrDefault(session => ReferenceEquals(session.Host, host))
            : null;

    private static string ShellPath(string path) =>
        path.All(c => char.IsLetterOrDigit(c) || "\\/:._-".Contains(c))
            ? path
            : "'" + path.Replace("'", "''") + "'";

    // ---- yazi ------------------------------------------------------------------------

    private void LeftToolStack_DragOver(object sender, DragEventArgs e) =>
        AcceptDrop(e, YaziUnder(e));

    private async void LeftToolStack_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var paths = DroppedPaths(e);
        if (paths.Length == 0 || !YaziUnder(e))
            return;

        var folder = _yaziFolder is { } shown && Directory.Exists(shown) ? shown : _currentProject!;
        string? problem = null;
        var copied = await Task.Run(() => CopyInto(folder, paths, out problem));

        if (copied.Count > 0)
        {
            var id = YaziClientId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await RunYa("emit-to", id, "reveal", copied[0]);
            FocusPanel(ToolPanel.Left, _leftHost);
        }

        if (problem is not null)
            ShowNotice("Copy to Project Browser", problem);

        // The bus goes with the yazi that served it; the next folder change needs a new ear.
        if (_yaziFolderWatch is null or { HasExited: true })
            WatchYaziFolder();
    }

    private bool YaziUnder(DragEventArgs e) =>
        _leftHostTool == LeftTool.Project && _leftHost is not null && ReferenceEquals(HostUnder(e), _leftHost);

    // Copies each file or folder into the folder, under a free name: one already there is
    // kept, and the copy is named as yazi's own paste would name it, "name_1.ext". A
    // path dropped onto the folder it is already in is left alone.
    private static List<string> CopyInto(string folder, string[] paths, out string? failed)
    {
        var done = new List<string>();
        failed = null;

        foreach (var source in paths)
        {
            try
            {
                var trimmed = source.TrimEnd('\\', '/');
                if (string.Equals(Path.GetDirectoryName(trimmed), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    continue;

                var isFolder = Directory.Exists(trimmed);
                if (isFolder && (folder + "\\").StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    failed = $"{Path.GetFileName(trimmed)} cannot be copied into itself.";
                    continue;
                }

                var target = FreeName(folder, Path.GetFileName(trimmed), isFolder);
                if (isFolder)
                    CopyFolder(trimmed, target);
                else
                    File.Copy(trimmed, target);

                done.Add(target);
            }
            catch (Exception exception)
            {
                failed = $"{Path.GetFileName(source)} could not be copied: {exception.Message}";
            }
        }

        return done;
    }

    private static string FreeName(string folder, string name, bool isFolder)
    {
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? string.Empty : Path.GetExtension(name);

        var candidate = Path.Combine(folder, name);
        for (var number = 1; File.Exists(candidate) || Directory.Exists(candidate); number++)
            candidate = Path.Combine(folder, $"{stem}_{number}{extension}");

        return candidate;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
    }

    // The folder Helide's yazi is showing. Yazi announces every change of folder on its
    // own message bus, and `ya sub cd` prints them, each tagged with the instance that
    // sent it; only ours are kept. Until the first one, yazi is where it started.
    private string? _yaziFolder;
    private Process? _yaziFolderWatch;

    private void WatchYaziFolder()
    {
        if (_yaziFolderWatch is { HasExited: false })
            return;

        // Yazi takes a moment to open its bus, and `ya sub` gives up when there is none.
        var attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_leftHostTool != LeftTool.Project || _leftHost is null || attempts++ >= 20)
            {
                timer.Stop();
                return;
            }

            if (StartYaziFolderWatch())
                timer.Stop();
        };
        timer.Start();
    }

    private bool StartYaziFolderWatch()
    {
        try
        {
            var start = new ProcessStartInfo("ya.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("sub");
            start.ArgumentList.Add("cd");

            var process = Process.Start(start);
            if (process is null)
                return false;

            // Still running after a moment: it is connected.
            if (process.WaitForExit(500))
            {
                process.Dispose();
                return false;
            }

            var sender = "," + YaziClientId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",";
            process.OutputDataReceived += (_, line) =>
            {
                // cd,<receiver>,<sender>,{"tab":1,"url":"..."}
                if (line.Data is not { } text || !text.StartsWith("cd,", StringComparison.Ordinal))
                    return;

                var body = text.IndexOf(",{", StringComparison.Ordinal);
                if (body < 0 || !text[..body].EndsWith(sender[..^1], StringComparison.Ordinal))
                    return;

                try
                {
                    using var document = JsonDocument.Parse(text[(body + 1)..]);
                    if (document.RootElement.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } folder)
                        Dispatcher.BeginInvoke(() => _yaziFolder = folder);
                }
                catch (JsonException)
                {
                    // Not one of yazi's; skipped.
                }
            };
            process.BeginOutputReadLine();
            _yaziFolderWatch = process;
            return true;
        }
        catch (Exception)
        {
            // No ya: a drop lands in the project folder.
            return false;
        }
    }

    private void StopYaziFolderWatch()
    {
        try
        {
            if (_yaziFolderWatch is { HasExited: false } process)
                process.Kill();
        }
        catch (Exception)
        {
            // Already gone.
        }

        _yaziFolderWatch?.Dispose();
        _yaziFolderWatch = null;
    }

    // ---- shared ----------------------------------------------------------------------

    private static void AcceptDrop(DragEventArgs e, bool target)
    {
        e.Effects = target && DroppedPaths(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths
            : [];

    // The terminal pane under the pointer.
    private static NativeTerminalHost? HostUnder(DragEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = ParentOf(node))
        {
            if (node is NativeTerminalHost host)
                return host;
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
