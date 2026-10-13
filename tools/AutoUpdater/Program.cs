using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoUpdater;

internal enum Stage { Check, Download, Install, Restart }

internal enum StageState { Pending, Active, Done, Failed, Skipped }

internal enum Level { Info, Ok, Warn, Error }

internal class Program
{
    private const string AppName = "Helide";
    private const string ToolName = "auto-updater";

    // helide.txt is the mark inside a heavy two-row, four-column frame. The frame is cut
    // away so the hourglass sits in this tool's own border instead of a box in a box;
    // the asset is shared with the app and CheckNInstall, so it is trimmed here rather
    // than edited.
    private const int FrameRows = 2;
    private const int FrameColumns = 4;
    private const int LogoMargin = 4;

    private const int Pad = 2;
    private const int MinimumRightInner = 46;
    private const int MaximumContent = 72;
    private const int StageLabelWidth = 20;
    private const int PercentWidth = 6;

    private const string Bold = "\x1b[1m";

    // Every glyph below is ASCII, Latin-1, single-line box drawing or a block element.
    // Rounded corners, braille, arrows and the geometric circles are all missing from
    // common terminal fonts and come out as a "?" or as nothing at all, which is what
    // left the pending rows without a marker.
    private static readonly string[] Spinner = ["|", "/", "-", "\\"];
    private static readonly string[] StageLabels = ["Check for updates", "Download", "Install", "Restart " + AppName];
    private static readonly Run[] Blank = [];

    private static Theme _theme = Theme.Mocha;
    private static bool _tui;
    private static int _width, _height;
    private static string[] _logo = [];
    private static int _logoWidth;

    private static readonly object _renderLock = new();
    private static readonly ManualResetEventSlim _closeRequested = new(false);
    private static ConsoleCancelEventHandler? _cancelHandler;
    private static Timer? _ticker;

    // Everything below is state the frame is drawn from. It is only touched under
    // _renderLock, because the download thread writes it and the ticker reads it.
    private static readonly StageState[] _stages = new StageState[4];
    private static readonly string[] _details = ["", "", "", ""];
    private static readonly List<(Level Level, string Text)> _log = [];

    private static string _current = string.Empty;
    private static string _latest = string.Empty;
    private static bool _updateAvailable;
    private static bool _finished;
    private static bool _dirty = true;
    private static int _frame;

    private static long _downloaded;
    private static long _total;
    private static long _speedBytes;
    private static long _speedStamp;
    private static double _speed;
    private static int _lastDecile = -1;

    static async Task<int> Main(string[] args)
    {
        // Nothing is configurable: running the exe checks, installs and restarts. Whatever
        // is passed on the command line, such as the flags an older shortcut still
        // carries, is ignored rather than rejected.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _theme = LoadTheme() == "oled" ? Theme.Oled : Theme.Mocha;

        EnterTui();

        try
        {
            // Deletes the .old exes the previous update displaced. This process is not
            // the one holding them open, so the locks are gone and they can go.
            AutoUpdater.DeleteStaleOldExecutables();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var updater = new AutoUpdater(cts);
            _current = updater.CurrentVersion;

            SetStage(Stage.Check, StageState.Active, "contacting GitHub");
            Log(Level.Warn, $"deprecated: {AppName} updates itself from the title bar and the About window");
            Log(Level.Info, $"checking {AppName} for updates");

            bool hasUpdate;
            try
            {
                hasUpdate = await updater.CheckForUpdateAsync();
            }
            catch (Exception ex)
            {
                // A check that could not run is not "up to date", and saying so is how
                // an offline machine used to look like a current one.
                SetStage(Stage.Check, StageState.Failed, ex.Message.ReplaceLineEndings(" "));
                Log(Level.Error, $"check failed: {ex.Message}");
                SkipPending();
                return Finish(1);
            }

            _latest = updater.LatestVersion;

            if (!hasUpdate)
            {
                SetStage(Stage.Check, StageState.Done, "up to date");
                SkipPending();
                Log(Level.Ok, $"no update available (current {Versions.Pretty(_current)})");
                return Finish(0);
            }

            _updateAvailable = true;
            SetStage(Stage.Check, StageState.Done, $"v{Versions.Pretty(_latest)} available");
            Log(Level.Info, $"new version available: v{Versions.Pretty(_current)} -> v{Versions.Pretty(_latest)}");
            SetStage(Stage.Download, StageState.Active, "starting");

            // The installer reports whether it actually finished. Claiming success on a
            // failure is what restarted Helide over and over, because every launch then
            // found the same "new" version and tried again.
            var installed = await updater.DownloadAndInstallUpdateAsync(
                OnProgress,
                message => Log(Level.Info, message),
                OnInstalling);

            if (!installed)
            {
                FailActive(updater.LastError ?? "unknown error");
                SkipPending();
                Log(Level.Error, $"update was not installed. {AppName} left running.");
                return Finish(1);
            }

            SetStage(Stage.Install, StageState.Done, "files replaced");
            SetStage(Stage.Restart, StageState.Active, $"launching {AppName}");
            Log(Level.Ok, $"restarting {AppName}...");

            // Long enough to see the finished state; the restart tears the frame down.
            if (_tui) Thread.Sleep(900);

            updater.RestartApp();
            return 0;
        }
        catch (Exception ex)
        {
            FailActive(ex.Message);
            SkipPending();
            Log(Level.Error, ex.Message);
            return Finish(1);
        }
        finally
        {
            LeaveTui();
        }
    }

    // ------------------------------------------------------------------ state

    private static void SetStage(Stage stage, StageState state, string detail = "")
    {
        lock (_renderLock)
        {
            _stages[(int)stage] = state;
            _details[(int)stage] = detail;
            _dirty = true;
        }
    }

    // Marks whatever was in flight as the thing that broke.
    private static void FailActive(string reason)
    {
        lock (_renderLock)
        {
            var index = Array.IndexOf(_stages, StageState.Active);
            if (index < 0) index = Array.IndexOf(_stages, StageState.Pending);
            if (index < 0) return;

            _stages[index] = StageState.Failed;
            _details[index] = reason.ReplaceLineEndings(" ");
            _dirty = true;
        }
    }

    // Steps that will not run now read as skipped rather than as work still to come.
    private static void SkipPending()
    {
        lock (_renderLock)
        {
            for (var index = 0; index < _stages.Length; index++)
                if (_stages[index] == StageState.Pending) _stages[index] = StageState.Skipped;

            _dirty = true;
        }
    }

    private static void OnInstalling()
    {
        lock (_renderLock)
        {
            _stages[(int)Stage.Download] = StageState.Done;
            _details[(int)Stage.Download] = _total > 0 ? AutoUpdater.FormatBytes(_total) : string.Empty;
            _stages[(int)Stage.Install] = StageState.Active;
            _details[(int)Stage.Install] = "replacing executables";
            _dirty = true;
        }

        Log(Level.Info, "download complete, installing");
    }

    // Called once per 8 KB chunk, which is about twelve thousand times for the archive.
    // It only records numbers; the ticker decides when to paint them, so the download
    // is never held up by a repaint and a repaint is never triggered per chunk.
    private static void OnProgress(long downloaded, long total)
    {
        if (!_tui)
        {
            // Without a console there is no frame, so progress is a line every tenth.
            if (total <= 0) return;
            var decile = (int)(downloaded * 10 / total);
            if (decile > _lastDecile)
            {
                _lastDecile = decile;
                Console.WriteLine($"  [download] {decile * 10}%");
            }
            return;
        }

        lock (_renderLock)
        {
            _downloaded = downloaded;
            _total = total;

            // The row's own detail becomes the size, the same figure it keeps once done.
            if (_stages[(int)Stage.Download] == StageState.Active && total > 0)
                _details[(int)Stage.Download] = AutoUpdater.FormatBytes(total);

            // Speed is smoothed over half-second windows so the figure and the ETA do
            // not jitter with every packet.
            var now = Stopwatch.GetTimestamp();
            if (_speedStamp == 0)
            {
                _speedStamp = now;
                _speedBytes = downloaded;
            }
            else
            {
                var seconds = (now - _speedStamp) / (double)Stopwatch.Frequency;
                if (seconds >= 0.5)
                {
                    var instant = (downloaded - _speedBytes) / seconds;
                    _speed = _speed <= 0 ? instant : _speed * 0.7 + instant * 0.3;
                    _speedStamp = now;
                    _speedBytes = downloaded;
                }
            }

            _dirty = true;
        }
    }

    // One line of output. Without a console the same text goes to stdout, which is what
    // a pipe wants.
    private static void Log(Level level, string message)
    {
        if (!_tui)
        {
            var tag = level switch { Level.Ok => "ok", Level.Warn => "warn", Level.Error => "error", _ => "" };
            Console.WriteLine(tag.Length == 0 ? $"  {message}" : $"  [{tag}] {message}");
            return;
        }

        lock (_renderLock)
        {
            _log.Add((level, message));
            _dirty = true;
        }
    }

    // Holds the finished frame on screen until a key is pressed. Without a console there
    // is nothing to hold, so a pipeline is never left waiting on a keypress.
    private static int Finish(int code)
    {
        if (!_tui) return code;

        lock (_renderLock)
        {
            _finished = true;
            _dirty = true;
        }

        if (!Console.IsInputRedirected) _closeRequested.Wait();
        return code;
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
            // A pipe or a redirected console gets the plain, line-oriented output.
            return;
        }

        var stream = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("helide.txt");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            _logo = CropMark(reader.ReadToEnd().TrimEnd('\r', '\n').Split('\n'));
            _logoWidth = _logo.Length == 0 ? 0 : _logo[0].Length;
        }

        _tui = true;

        // The frame is drawn on the terminal's alternate screen, which is what a full
        // screen program is meant to use: nothing scrolls behind it, and leaving throws
        // it away and puts the shell's own scrollback back exactly as it was.
        Console.Write("\x1b[?1049h");
        Console.Write(Background.Sequence(_theme.WindowAsRgb));
        Console.Write("\x1b[2J\x1b[H");
        Console.CursorVisible = false;

        // Ctrl-C is a console event, not a key, and the default behaviour kills the
        // process on the spot -- leaving the cursor hidden, the palette set, and the
        // frame still on screen for the next prompt to draw over.
        _cancelHandler = OnCancelKeyPress;
        Console.CancelKeyPress += _cancelHandler;

        Render();

        // One heartbeat drives the spinner, the theme key and repaints, for the whole
        // run. It used to exist only while checking, so T did nothing during the
        // download and the key a finished frame waits on had no one to read it.
        _ticker = new Timer(Tick, null, 80, 80);
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

        // The window brush was set with a raw escape, which ResetColor knows nothing
        // about, so it is undone first. Then the alternate screen is dropped, which
        // discards the frame and restores whatever the shell had before.
        Console.Write("\x1b[0m");
        Console.ResetColor();
        Console.CursorVisible = true;
        Console.Write("\x1b[?1049l");

        _closeRequested.Set();
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

    // Cuts the mark out of its frame and trims the empty margin around the hourglass, so
    // every row is exactly as wide as the artwork.
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

    private static void Tick(object? _)
    {
        lock (_renderLock)
        {
            if (!_tui) return;

            PollKeys();
            _frame++;

            var animating = _stages.Contains(StageState.Active) && !_finished;
            var resized = false;
            try { resized = Console.WindowWidth != _width || Console.WindowHeight != _height; } catch { }

            if (_dirty || animating || resized) Render();
        }
    }

    // T cycles the palette while the update runs; once the frame is waiting to close,
    // any key closes it, T included.
    private static void PollKeys()
    {
        if (Console.IsInputRedirected) return;

        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                if (_finished)
                {
                    _closeRequested.Set();
                    continue;
                }

                if (key.Key != ConsoleKey.T) continue;
                _theme = _theme == Theme.Mocha ? Theme.Oled : Theme.Mocha;
                _dirty = true;
            }
        }
        catch
        {
            // No readable input is not a reason to stop drawing.
        }
    }

    private static void Render()
    {
        lock (_renderLock)
        {
            if (!_tui) return;

            LockViewport();
            _dirty = false;

            // One write per frame, positioned with escapes: a row-by-row repaint was
            // visibly tearing and cost a syscall per line.
            Console.Write(BuildFrame());
        }
    }

    // ------------------------------------------------------------------ layout

    private readonly record struct Run(string Text, string Style);

    private static int LogoPane => _logoWidth + 2 * LogoMargin;

    private static string BuildFrame()
    {
        var sb = new StringBuilder(_width * _height * 4);
        sb.Append("\x1b[0m").Append(Background.Sequence(_theme.WindowAsRgb));

        if (_width < 40 || _height < 9)
        {
            BuildCompact(sb);
            return sb.ToString();
        }

        var border = _theme.Border;
        var withLogo = _logoWidth > 0
            && _width - 3 - LogoPane >= MinimumRightInner
            && _height - 3 >= _logo.Length;

        var leftInner = withLogo ? LogoPane : 0;
        var rightInner = withLogo ? _width - 3 - leftInner : _width - 2;
        var rows = _height - 3;

        var pane = BuildPane(rightInner - 2 * Pad, rows);
        var offset = withLogo ? Math.Max(0, (rows - _logo.Length) / 2) : 0;

        // Top rule: brand on the left, the palette on the right.
        MoveTo(sb, 0);
        var title = new[]
        {
            new Run("┌─ ", border),
            new Run("helide", Bold + _theme.Mauve),
            new Run(" " + ToolName + " ", _theme.TextMuted),
        };
        var palette = new[]
        {
            new Run(" " + _theme.Name + " ", _theme.TextFaint),
            new Run("─┐", border),
        };
        var gap = Math.Max(1, _width - title.Sum(r => r.Text.Length) - palette.Sum(r => r.Text.Length));
        foreach (var run in title) AppendRun(sb, run);
        AppendRun(sb, new Run(new string('─', gap), border));
        foreach (var run in palette) AppendRun(sb, run);

        for (var row = 0; row < rows; row++)
        {
            MoveTo(sb, row + 1);
            AppendRun(sb, new Run("│", border));

            if (withLogo)
            {
                var index = row - offset;
                var mark = index >= 0 && index < _logo.Length
                    ? new[] { new Run(_logo[index], Gradient(_logo.Length <= 1 ? 0 : index / (double)(_logo.Length - 1))) }
                    : Blank;

                AppendCell(sb, leftInner, mark, LogoMargin);
                AppendRun(sb, new Run("│", border));
            }

            AppendCell(sb, rightInner, row < pane.Count ? pane[row] : Blank, Pad);
            AppendRun(sb, new Run("│", border));
        }

        // Bottom rule, with a junction where the logo pane meets the log pane.
        MoveTo(sb, _height - 2);
        AppendRun(sb, new Run(
            withLogo
                ? "└" + new string('─', leftInner) + "┴" + new string('─', rightInner) + "┘"
                : "└" + new string('─', rightInner) + "┘",
            border));

        // Keyboard hints sit outside the frame, so the box stays a pure status surface.
        MoveTo(sb, _height - 1);
        var hints = _finished
            ? new[] { new Run(" any key", _theme.TextMuted), new Run(" to close", _theme.TextFaint) }
            : new[]
            {
                new Run(" T", _theme.TextMuted), new Run(" theme   ", _theme.TextFaint),
                new Run("Ctrl+C", _theme.TextMuted), new Run(" quit", _theme.TextFaint),
            };
        AppendCell(sb, _width, hints, 0);

        // Parked on the first cell on purpose: a cursor left on the final cell is what
        // makes a console scroll, even when the buffer already fits.
        sb.Append("\x1b[1;1H");
        return sb.ToString();
    }

    // A window too small for the frame still says what is happening.
    private static void BuildCompact(StringBuilder sb)
    {
        var (headline, tone) = Headline();
        var percent = _total > 0 ? $"  {Math.Clamp(_downloaded * 100 / _total, 0, 100)}%" : string.Empty;

        for (var row = 0; row < _height; row++)
        {
            MoveTo(sb, row);
            AppendCell(sb, _width, row == 0
                ? new[] { new Run(headline + percent, tone) }
                : Blank, 0);
        }

        sb.Append("\x1b[1;1H");
    }

    // The right-hand pane: what is happening, the steps, the numbers, then the trail.
    private static List<Run[]> BuildPane(int width, int rows)
    {
        // Past this the bar and the rule only stretch; the text on them does not.
        width = Math.Min(width, MaximumContent);

        var lines = new List<Run[]> { Blank };

        var (headline, tone) = Headline();
        lines.Add([new Run(headline, Bold + tone)]);
        lines.Add(VersionLine());
        lines.Add(Blank);

        for (var index = 0; index < StageLabels.Length; index++)
            lines.Add(StageLine(index));

        var transferring = _stages[(int)Stage.Download] is StageState.Active or StageState.Done
            || (_stages[(int)Stage.Download] == StageState.Failed && _downloaded > 0);
        if (transferring)
        {
            lines.Add(Blank);
            lines.AddRange(ProgressLines(width));
        }

        lines.Add(Blank);
        lines.Add([
            new Run("activity ", _theme.TextFaint),
            new Run(new string('─', Math.Max(0, width - 9)), _theme.Border),
        ]);

        // The log is the only part that grows, so it takes whatever rows are left and
        // shows its tail, which is where the newest line always is.
        var entries = new List<Run[]>();
        foreach (var (level, text) in _log)
        {
            var (glyph, colour) = level switch
            {
                Level.Ok => ("√", _theme.Success),
                Level.Warn => ("!", _theme.Mauve),
                Level.Error => ("×", _theme.Danger),
                _ => ("·", _theme.TextFaint),
            };
            var body = level == Level.Error ? _theme.Danger : _theme.TextMuted;

            var first = true;
            foreach (var wrapped in Wrap(text, Math.Max(4, width - 2)))
            {
                entries.Add([
                    new Run(first ? glyph + " " : "  ", colour),
                    new Run(wrapped, body),
                ]);
                first = false;
            }
        }

        var available = Math.Max(0, rows - lines.Count);
        lines.AddRange(entries.Skip(Math.Max(0, entries.Count - available)));
        return lines;
    }

    private static (string Text, string Tone) Headline()
    {
        var failed = Array.IndexOf(_stages, StageState.Failed);
        if (failed == (int)Stage.Check) return ("Couldn't check for updates", _theme.Danger);
        if (failed >= 0) return ("Update failed", _theme.Danger);

        if (_stages[(int)Stage.Restart] is StageState.Active or StageState.Done)
            return ("Update installed", _theme.Success);
        if (_stages[(int)Stage.Install] == StageState.Active) return ("Installing update", _theme.Accent);
        if (_stages[(int)Stage.Download] == StageState.Active)
            return ($"Downloading {AppName} v{Versions.Pretty(_latest)}", _theme.Accent);

        if (_stages[(int)Stage.Check] == StageState.Done && !_updateAvailable)
            return ($"{AppName} is up to date", _theme.Success);

        return ("Checking for updates", _theme.Accent);
    }

    // "1.7.2 -> 1.7.3", with the new number in the accent. Until the answer is in, the
    // right side is an ellipsis rather than the word "unknown".
    private static Run[] VersionLine()
    {
        var current = "v" + Versions.Pretty(_current);

        if (_stages[(int)Stage.Check] == StageState.Done && !_updateAvailable)
            return [new Run(current, Bold + _theme.Text), new Run("   latest release", _theme.TextFaint)];

        if (_stages[(int)Stage.Check] == StageState.Failed)
            return [new Run(current, Bold + _theme.Text), new Run("   installed", _theme.TextFaint)];

        return
        [
            new Run(current, _theme.TextMuted),
            new Run("  ->  ", _theme.TextFaint),
            _latest.Length == 0
                ? new Run("...", _theme.TextFaint)
                : new Run("v" + Versions.Pretty(_latest), Bold + _theme.Accent),
        ];
    }

    private static Run[] StageLine(int index)
    {
        var state = _stages[index];
        var (glyph, glyphStyle, labelStyle) = state switch
        {
            // Two ticks per spinner frame: at the heartbeat's own rate it spins too fast
            // to read as a spinner.
            StageState.Active => (Spinner[_frame / 2 % Spinner.Length], _theme.Accent, Bold + _theme.Text),
            StageState.Done => ("√", _theme.Success, _theme.TextMuted),
            StageState.Failed => ("×", _theme.Danger, Bold + _theme.Danger),
            StageState.Skipped => ("-", _theme.TextFaint, _theme.TextFaint),
            _ => ("·", _theme.TextFaint, _theme.TextFaint),
        };

        var detailStyle = state == StageState.Failed ? _theme.Danger : _theme.TextFaint;

        return
        [
            new Run(glyph + "  ", glyphStyle),
            new Run(StageLabels[index].PadRight(StageLabelWidth), labelStyle),
            new Run(_details[index], detailStyle),
        ];
    }

    private static List<Run[]> ProgressLines(int width)
    {
        var cells = Math.Max(8, width - PercentWidth);
        var done = _stages[(int)Stage.Download] == StageState.Done;
        var known = _total > 0;
        var fraction = done ? 1.0 : known ? Math.Clamp((double)_downloaded / _total, 0, 1) : 0;

        var bar = new List<Run>();
        if (known || done)
        {
            // Half a cell is the finest step every font has a block for.
            var halves = (int)Math.Round(fraction * cells * 2);
            var whole = halves / 2;
            var half = halves % 2 == 1;

            for (var cell = 0; cell < cells; cell++)
            {
                var colour = Gradient(cells <= 1 ? 0 : cell / (double)(cells - 1));
                if (cell < whole) bar.Add(new Run("█", colour));
                else if (cell == whole && half) bar.Add(new Run("▌", colour));
                else bar.Add(new Run("░", _theme.Track));
            }
        }
        else
        {
            // No length from the server, so no honest percentage: a block sweeps the
            // track to show the transfer is alive.
            const int sweep = 6;
            var start = (_frame / 2) % (cells + sweep) - sweep;
            for (var cell = 0; cell < cells; cell++)
            {
                var lit = cell >= start && cell < start + sweep;
                bar.Add(lit
                    ? new Run("█", Gradient(cell / (double)Math.Max(1, cells - 1)))
                    : new Run("░", _theme.Track));
            }
        }

        var percent = known || done ? $"{(int)Math.Floor(fraction * 100),4}%" : "     ";
        bar.Add(new Run("  " + percent.TrimStart().PadLeft(4), _theme.Text));

        var stats = new List<Run>();
        if (done)
        {
            stats.Add(new Run(_total > 0 ? AutoUpdater.FormatBytes(_total) : AutoUpdater.FormatBytes(_downloaded), _theme.TextMuted));
            stats.Add(new Run("   downloaded", _theme.TextFaint));
        }
        else
        {
            var transferred = known
                ? $"{AutoUpdater.FormatBytes(_downloaded)} / {AutoUpdater.FormatBytes(_total)}"
                : AutoUpdater.FormatBytes(_downloaded);
            stats.Add(new Run(transferred, _theme.TextMuted));

            if (_speed > 0)
            {
                stats.Add(new Run("   ·   ", _theme.TextFaint));
                stats.Add(new Run($"{AutoUpdater.FormatBytes((long)_speed)}/s", _theme.TextMuted));

                if (known && _downloaded < _total)
                {
                    stats.Add(new Run("   ·   ", _theme.TextFaint));
                    stats.Add(new Run($"{Duration((_total - _downloaded) / _speed)} left", _theme.TextMuted));
                }
            }
        }

        return [bar.ToArray(), stats.ToArray()];
    }

    private static string Duration(double seconds) => seconds < 60
        ? $"{Math.Max(1, (int)Math.Ceiling(seconds))}s"
        : $"{(int)seconds / 60}m {(int)seconds % 60:00}s";

    // ------------------------------------------------------------------ drawing

    private static string Gradient(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var (fromR, fromG, fromB) = _theme.GradientFrom;
        var (toR, toG, toB) = _theme.GradientTo;

        return $"\x1b[38;2;{Lerp(fromR, toR, t)};{Lerp(fromG, toG, t)};{Lerp(fromB, toB, t)}m";
    }

    private static int Lerp(int from, int to, double t) => (int)Math.Round(from + (to - from) * t);

    private static void MoveTo(StringBuilder sb, int row) =>
        sb.Append("\x1b[").Append(row + 1).Append(";1H");

    // Colour is set and then only the foreground and weight are reset, so the window
    // brush written at the top of the frame stays on every cell it passes through.
    private static void AppendRun(StringBuilder sb, Run run) =>
        sb.Append(run.Style).Append(run.Text).Append("\x1b[22;39m");

    // Writes exactly `width` cells: the margin, the runs clipped to what is left, and
    // blanks to the edge. Clipping marks itself with an ellipsis so nothing is cut
    // silently.
    private static void AppendCell(StringBuilder sb, int width, Run[] runs, int pad)
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

            AppendRun(sb, new Run(text, run.Style));
            written += text.Length;
        }

        sb.Append(' ', Math.Max(0, width - Math.Min(pad, width) - written));
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
        // ProcessPath rather than MainModule, which is nullable and was dereferenced
        // twice here. It is the updater's own exe either way, single-file or not.
        var here = System.IO.Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
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
        return local;
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

    /// <summary>
    /// True when the latest release is newer than the install. A check that could not
    /// run throws instead of returning false: "no update" and "could not ask" are
    /// different answers, and collapsing them made an offline machine read as current.
    /// </summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        var json = await _httpClient.GetStringAsync(ReleasesApi, _cts.Token);
        using var doc = JsonDocument.Parse(json);
        var tagName = doc.RootElement.GetProperty("tag_name").GetString();
        if (string.IsNullOrEmpty(tagName))
            throw new InvalidOperationException("the latest release has no tag");

        var remoteText = tagName.TrimStart('v', 'V');
        if (!Version.TryParse(remoteText, out var remote))
            throw new InvalidOperationException($"unrecognised release tag '{tagName}'");

        // Recorded whether or not it is newer, so "latest" is known on the up-to-date
        // path as well and does not have to read "unknown".
        LatestVersion = remoteText;

        if (!Version.TryParse(CurrentVersion, out var current))
            current = new Version(1, 0, 0);

        return Versions.Normalize(remote) > Versions.Normalize(current);
    }

    /// <summary>Why the last install attempt returned false, for the interface to show.</summary>
    public string? LastError { get; private set; }

    public async Task<bool> DownloadAndInstallUpdateAsync(
        Action<long, long> progressCallback,
        Action<string> logCallback,
        Action? installingCallback = null)
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
                downloadUrl = $"{DownloadBase}/helide-win-x64.zip";

            if (string.IsNullOrEmpty(downloadUrl))
            {
                LastError = "no download URL in the release";
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
                    progressCallback?.Invoke(downloaded, totalSize);
                }
            }

            // With no length from the server the byte count is the only total there is.
            progressCallback?.Invoke(downloaded, totalSize > 0 ? totalSize : downloaded);
            installingCallback?.Invoke();

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
                logCallback?.Invoke("replaced Helide.exe");

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
                {
                    ReplaceRunningExecutable(newSelf, "AutoUpdater.exe");
                    logCallback?.Invoke("replaced AutoUpdater.exe");
                }
            }
            else
            {
                // Direct .exe download
                ReplaceRunningExecutable(downloadedFile);
                logCallback?.Invoke("replaced Helide.exe");
            }

            logCallback?.Invoke("Update installed.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
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

    internal static string FormatBytes(long bytes)
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

/// <summary>Version strings as the updater compares and shows them.</summary>
internal static class Versions
{
    // An unset component is -1, which sorts below 0: "1.7.3" would then be older than
    // "1.7.3.0", and an install whose file version carries a revision would be offered
    // the same release it already has.
    public static Version Normalize(Version version) =>
        new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

    // The file version is always four-part; a trailing .0 is noise to a reader.
    public static string Pretty(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "?";

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        text = text.TrimStart('v', 'V');

        if (!Version.TryParse(text, out var parsed)) return text;

        var version = Normalize(parsed);
        return version.Revision == 0 ? version.ToString(3) : version.ToString();
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
    public required string Track { get; init; }
    public required string WindowAsRgb { get; init; }

    // The mark and the progress bar both run from one end of this pair to the other.
    public required (int R, int G, int B) GradientFrom { get; init; }
    public required (int R, int G, int B) GradientTo { get; init; }

    public static readonly Theme Mocha = new()
    {
        Name = "mocha",
        // Catppuccin Mocha. Window #D9191724, surfaces lifted from that base.
        WindowAsRgb = "25;23;36",
        Border = "\x1b[38;2;69;71;90m",
        Track = "\x1b[38;2;88;91;112m",
        Text = "\x1b[38;2;205;214;244m",
        TextMuted = "\x1b[38;2;166;173;200m",
        TextFaint = "\x1b[38;2;108;112;134m",
        Accent = "\x1b[38;2;137;180;250m",
        Success = "\x1b[38;2;166;227;161m",
        Danger = "\x1b[38;2;243;139;168m",
        // The Helide mark is mauve, so the palette's secondary column uses the same value
        // rather than a grey.
        Mauve = "\x1b[38;2;203;166;247m",
        GradientFrom = (203, 166, 247),
        GradientTo = (137, 180, 250),
    };

    public static readonly Theme Oled = new()
    {
        Name = "oled",
        // Near-black rather than true black, with the chrome in small greyscale steps.
        WindowAsRgb = "14;14;14",
        Border = "\x1b[38;2;52;52;52m",
        Track = "\x1b[38;2;78;78;78m",
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
        GradientFrom = (183, 154, 203),
        GradientTo = (242, 242, 242),
    };
}

/// <summary>Paints the viewport with the theme's window brush.</summary>
internal static class Background
{
    public static string Sequence(string rgbTriplet) => $"\x1b[48;2;{rgbTriplet}m";
}
