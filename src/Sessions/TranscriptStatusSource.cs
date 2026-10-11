using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Helide.Sessions;

/// <summary>What one transcript record says about the turn it belongs to.</summary>
internal enum TurnMark
{
    /// <summary>The record says nothing about turn boundaries.</summary>
    None,

    /// <summary>A turn began.</summary>
    Started,

    /// <summary>A turn ended.</summary>
    Finished,
}

/// <summary>
/// Reports turn state for an agent that keeps a transcript on disk, by reading the
/// records the agent itself appends to it, so a pane that finished in the background
/// gets marked.
/// </summary>
/// <remarks>
/// Codex brackets every turn in its rollout file with <c>task_started</c> and
/// <c>task_complete</c>, and Claude's transcript carries the prompt that opens a turn and
/// the reply that closes it. Those are the agent's own statements about what it is doing.
/// Which records mean what is the only thing that differs between the two, so it is passed
/// in and everything else is shared.
/// <para>
/// The alternative, <see cref="QuietPeriodStatusSource"/>, infers the same thing from
/// silence in the terminal and cannot tell a finished reply from a build that is
/// still running. Here the difference is explicit, so a long silent turn keeps the
/// tab unmarked until the agent actually says the turn ended.
/// </para>
/// <para>
/// The file is read with sharing enabled and only the tail is examined, because the agent
/// holds the file open for as long as its TUI is up and it grows by append. Anything
/// unparseable is skipped rather than guessed at, so a torn read costs a poll instead
/// of a wrong answer.
/// </para>
/// <para>
/// A transcript that is already there when the pane starts is history: the turns in it
/// finished before anyone could have been away, so the first read of it only establishes
/// where things stand. A transcript that appears while the pane is being watched is the
/// pane's first turn, and everything in it counts.
/// </para>
/// </remarks>
internal sealed class TranscriptStatusSource : ISessionStatusSource
{
    /// <summary>How often the file is checked for a turn boundary.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    // Enough to cover a burst of trailing records without re-reading the whole file on
    // every poll; the file only grows, so a partial tail would just be re-read later.
    private const int TailBytes = 64 * 1024;

    private readonly Func<string?> _resolvePath;
    private readonly Func<JsonElement, TurnMark> _readTurn;
    private readonly DispatcherTimer _timer;

    private string? _path;
    private long _offset;
    private string _pending = string.Empty;
    private bool _busy;
    private bool _finished;
    private bool _baselinePending;
    private bool _lookedBefore;

    public TranscriptStatusSource(
        Func<string?> resolvePath,
        Func<JsonElement, TurnMark> readTurn,
        Dispatcher dispatcher)
    {
        _resolvePath = resolvePath;
        _readTurn = readTurn;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = PollInterval,
        };

        _timer.Tick += (_, _) => Evaluate();
    }

    /// <summary>
    /// True only while the agent has said a turn started and not yet that it ended.
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
            var replaced = _path is not null;
            var firstLook = !_lookedBefore;
            _lookedBefore = true;

            var resolved = _resolvePath();
            if (resolved is null)
                return;

            _path = resolved;
            _offset = 0;
            _pending = string.Empty;
            _busy = false;
            _finished = false;

            // Found on the first look, or found again after the agent replaced it: what
            // it holds was written before this pane was being watched.
            _baselinePending = firstLook || replaced;
        }

        if (!TryReadNewRecords())
            return;

        var finished = _finished;
        _finished = false;

        if (_baselinePending)
        {
            _baselinePending = false;
            return;
        }

        if (finished)
            BecameIdle?.Invoke();
    }

    /// <summary>
    /// Reads whatever has been appended since the last poll and folds the turn markers
    /// into <see cref="_busy"/>. Returns false only when the file could not be opened,
    /// which is a transient state while the agent replaces it.
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
            // re-parsed on every poll. Skip forward when the tail window cannot cover
            // the growth.
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
            // the record that ends the turn and leave the tab stuck as busy. So the
            // partial line is carried into the next poll instead.
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
            // The file can be replaced on resume, so a failed open is momentary. Keeping
            // the previous state means a blip cannot fake a finished turn.
            return false;
        }
    }

    private void ApplyRecord(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return;

        TurnMark mark;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            mark = _readTurn(document.RootElement);
        }
        catch (Exception)
        {
            // A line caught mid-append is not valid JSON yet, and a record in a shape
            // this does not know says nothing either way.
            return;
        }

        switch (mark)
        {
            case TurnMark.Started:
                _busy = true;
                break;
            case TurnMark.Finished:
                // Only a turn that was seen running can be said to have stopped, which
                // is what keeps a reply made of several records from counting twice.
                if (_busy)
                    _finished = true;
                _busy = false;
                break;
        }
    }
}
