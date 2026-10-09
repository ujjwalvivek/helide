using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Helide.Sessions;

/// <summary>
/// Reports codex turn state by reading the turn records codex writes into its own
/// rollout file, so a pane that finished in the background gets marked.
/// </summary>
/// <remarks>
/// Codex brackets every turn in the rollout file with <c>task_started</c> and
/// <c>task_complete</c> records. Those are the agent's own statements about what it
/// is doing, which is what OpenCode's event stream provides for that tool.
/// <para>
/// The alternative, <see cref="QuietPeriodStatusSource"/>, infers the same thing from
/// silence in the terminal and cannot tell a finished reply from a build that is
/// still running. Here the difference is explicit, so a long silent turn keeps the
/// tab unmarked until codex actually says the turn ended.
/// </para>
/// <para>
/// The file is read with sharing enabled and only the tail is examined, because codex
/// holds the file open for as long as its TUI is up and it grows by append. Anything
/// unparseable is skipped rather than guessed at, so a torn read costs a poll instead
/// of a wrong answer.
/// </para>
/// </remarks>
internal sealed class RolloutStatusSource : ISessionStatusSource
{
    /// <summary>How often the file is checked for a turn boundary.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    // Enough to cover a burst of trailing records without re-reading the whole file on
    // every poll; the file only grows, so a partial tail would just be re-read later.
    private const int TailBytes = 64 * 1024;

    private readonly Func<string?> _resolvePath;
    private readonly DispatcherTimer _timer;

    private string? _path;
    private long _offset;
    private string _pending = string.Empty;
    private bool _busy;
    private bool _sawTurn;

    public RolloutStatusSource(Func<string?> resolvePath, Dispatcher dispatcher)
    {
        _resolvePath = resolvePath;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = PollInterval,
        };

        _timer.Tick += (_, _) => Evaluate();
    }

    /// <summary>
    /// True only while codex has said a turn started and not yet that it completed.
    /// False when idle, and also when the file cannot be read -- unknown is reported
    /// as not-busy so an unreadable file never invents activity.
    /// </summary>
    public bool IsBusy => _busy;

    public event Action? BecameIdle;

    public void Start() => _timer.Start();

    public void Dispose() => _timer.Stop();

    private void Evaluate()
    {
        // Resolved lazily and re-resolved if it goes missing: a pane that has not been
        // used yet has no file, and gains one the moment it does.
        if (_path is null || !File.Exists(_path))
        {
            var resolved = _resolvePath();
            if (resolved is null)
                return;

            _path = resolved;
            _offset = 0;
            _pending = string.Empty;
            _busy = false;
        }

        if (!TryReadNewRecords())
            return;

        if (_busy && _sawTurn)
        {
            _busy = false;
            BecameIdle?.Invoke();
        }
    }

    /// <summary>
    /// Reads whatever has been appended since the last poll and folds the turn markers
    /// into <see cref="_busy"/>. Returns false only when the file could not be opened,
    /// which is a transient state while codex replaces it.
    /// </summary>
    private bool TryReadNewRecords()
    {
        long start;
        try
        {
            using var stream = new FileStream(
                _path!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            start = stream.Length;

            // Never re-read from the beginning: a long conversation would otherwise be
            // re-parsed on every poll, and its oldest task_started would flip the tab
            // back to busy. Skip forward when the tail window cannot cover the growth.
            if (start < _offset)
                _offset = 0;
            else if (start - _offset > TailBytes)
                _offset = start - TailBytes;

            if (start <= _offset)
                return true;

            stream.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            // A read can stop mid-line, and the offset has to stay at the last newline
            // or the completed line's tail is never seen: advancing past it would drop
            // the final task_complete and leave the tab stuck as busy. So the partial
            // line is carried into the next poll instead.
            var pending = _pending;
            var buffer = new char[8192];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var text = pending + new string(buffer, 0, read);
                var lastBreak = text.LastIndexOf('\n');
                if (lastBreak < 0)
                {
                    pending = text;
                    continue;
                }

                foreach (var line in text[..lastBreak].Split('\n'))
                    ApplyRecord(line);

                pending = text[(lastBreak + 1)..];
            }

            _pending = pending;
            _offset = start;
            return true;
        }
        catch (Exception)
        {
            // Codex rewrites the file on resume, so a failed open is momentary. Keeping
            // the previous state means a blip cannot fake a finished turn.
            return false;
        }
    }

    private void ApplyRecord(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return;

        string? type;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (!document.RootElement.TryGetProperty("payload", out var payload))
                return;
            if (!payload.TryGetProperty("type", out var typeElement))
                return;
            type = typeElement.GetString();
        }
        catch (JsonException)
        {
            // A line caught mid-append is not valid JSON yet; the next poll re-reads it.
            return;
        }

        switch (type)
        {
            case "task_started":
                _busy = true;
                _sawTurn = true;
                break;
            case "task_complete":
                _busy = false;
                _sawTurn = true;
                break;
        }
    }
}
