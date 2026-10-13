namespace Helide.Terminal;

/// <summary>
/// Follows the output of a full-screen editor just far enough to tell whether its
/// status line is showing the "modified" marker.
/// </summary>
/// <remarks>
/// Helix has no way to tell an outside program that its buffer has unsaved changes, but
/// it draws <c>[+]</c> after the file name in its status line while it does. The pane is
/// a pseudoconsole, so what reaches Helide is the same escape-sequence stream a terminal
/// would draw, and this keeps a small character grid from it: printable text, cursor
/// movement and the erase sequences Helix's renderer uses. Colours, modes and everything
/// else are skipped.
/// <para>
/// Only the last two rows are looked at, because Helix puts the status line on the
/// second-to-last row and the command line under it. A <c>[+]</c> in the buffer itself,
/// in source code say, is therefore not mistaken for the marker.
/// </para>
/// <para>
/// Not thread-safe: it is fed from the pseudoconsole's reader thread and nowhere else.
/// </para>
/// </remarks>
internal sealed class StatusLineProbe
{
    private const int Columns = 512;
    private const string Marker = "[+]";

    private enum Mode
    {
        Ground,
        Escape,
        Skip,
        Csi,
        Osc,
        OscEscape,
    }

    private readonly List<char[]> _rows = [];
    private Mode _mode = Mode.Ground;
    private string _parameters = string.Empty;
    private int _row;
    private int _column;
    private int _lastRow = -1;
    private char _previous = ' ';

    /// <summary>Feeds a chunk of output. Returns the marker's state if it changed.</summary>
    public bool? Feed(string text, bool wasModified)
    {
        foreach (var c in text)
            Consume(c);

        var modified = IsMarked();
        return modified == wasModified ? null : modified;
    }

    private bool IsMarked()
    {
        for (var row = Math.Max(0, _lastRow - 1); row <= _lastRow && row < _rows.Count; row++)
        {
            if (new ReadOnlySpan<char>(_rows[row]).IndexOf(Marker.AsSpan()) >= 0)
                return true;
        }

        return false;
    }

    private void Consume(char c)
    {
        switch (_mode)
        {
            case Mode.Ground:
                Ground(c);
                return;

            case Mode.Escape:
                if (c == '[')
                {
                    _mode = Mode.Csi;
                    _parameters = string.Empty;
                }
                else if (c == ']')
                    _mode = Mode.Osc;
                else if (c is '(' or ')' or '*' or '+' or '#' or '%')
                    _mode = Mode.Skip;
                else
                    _mode = Mode.Ground;
                return;

            case Mode.Skip:
                _mode = Mode.Ground;
                return;

            case Mode.Osc:
                if (c == '\u0007')
                    _mode = Mode.Ground;
                else if (c == '\u001b')
                    _mode = Mode.OscEscape;
                return;

            case Mode.OscEscape:
                _mode = Mode.Ground;
                return;

            case Mode.Csi:
                // Parameter and intermediate bytes accumulate; the final byte is 0x40-0x7e.
                if (c >= '@' && c <= '~')
                {
                    _mode = Mode.Ground;
                    Dispatch(c, _parameters);
                }
                else if (_parameters.Length < 64)
                    _parameters += c;
                return;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\u001b':
                _mode = Mode.Escape;
                return;
            case '\r':
                _column = 0;
                return;
            case '\n':
                _row++;
                return;
            case '\b':
                if (_column > 0)
                    _column--;
                return;
            case '\t':
                _column = Math.Min(Columns - 1, (_column / 8 + 1) * 8);
                return;
        }

        if (c < ' ' || c == '\u007f')
            return;

        Put(c);
    }

    private void Put(char c)
    {
        if (_column >= Columns)
            return;

        Row(_row)[_column++] = c;
        _previous = c;
        if (_row > _lastRow)
            _lastRow = _row;
    }

    private char[] Row(int index)
    {
        // A screen is a few dozen rows; anything past this is a runaway cursor.
        index = Math.Clamp(index, 0, 1023);
        while (_rows.Count <= index)
            _rows.Add(Blank());

        return _rows[index];
    }

    private static char[] Blank() => new string(' ', Columns).ToCharArray();

    private void Dispatch(char final, string raw)
    {
        // Private-mode sequences (?25h, ?1049h and the like) carry no text, except that
        // switching screens starts from a blank one.
        if (raw.StartsWith('?'))
        {
            if (final is 'h' or 'l' && raw.Contains("1049"))
                Clear();
            return;
        }

        var numbers = raw.Split(';');
        int N(int index, int fallback)
        {
            if (index >= numbers.Length || !int.TryParse(numbers[index], out var value))
                return fallback;

            return value == 0 ? fallback : value;
        }

        switch (final)
        {
            case 'H':
            case 'f':
                _row = Math.Max(0, N(0, 1) - 1);
                _column = Math.Clamp(N(1, 1) - 1, 0, Columns - 1);
                break;
            case 'A':
                _row = Math.Max(0, _row - N(0, 1));
                break;
            case 'B':
                _row += N(0, 1);
                break;
            case 'C':
                _column = Math.Min(Columns - 1, _column + N(0, 1));
                break;
            case 'D':
                _column = Math.Max(0, _column - N(0, 1));
                break;
            case 'G':
                _column = Math.Clamp(N(0, 1) - 1, 0, Columns - 1);
                break;
            case 'd':
                _row = Math.Max(0, N(0, 1) - 1);
                break;
            case 'K':
                EraseLine(numbers[0]);
                break;
            case 'J':
                EraseDisplay(numbers[0]);
                break;
            case 'X':
                Fill(_row, _column, _column + N(0, 1));
                break;
            case 'b':
                for (var i = N(0, 1); i > 0 && _column < Columns; i--)
                    Put(_previous);
                break;
        }
    }

    private void EraseLine(string mode)
    {
        if (_row >= _rows.Count)
            return;

        switch (mode)
        {
            case "1":
                Fill(_row, 0, _column + 1);
                break;
            case "2":
                Fill(_row, 0, Columns);
                break;
            default:
                Fill(_row, _column, Columns);
                break;
        }
    }

    private void EraseDisplay(string mode)
    {
        if (mode is "2" or "3")
        {
            Clear();
            return;
        }

        if (mode == "1")
        {
            for (var row = 0; row < _row && row < _rows.Count; row++)
                Fill(row, 0, Columns);

            Fill(_row, 0, _column + 1);
            return;
        }

        Fill(_row, _column, Columns);
        for (var row = _row + 1; row < _rows.Count; row++)
            Fill(row, 0, Columns);
    }

    private void Fill(int row, int from, int to)
    {
        if (row >= _rows.Count)
            return;

        var cells = _rows[row];
        for (var column = Math.Max(0, from); column < Math.Min(Columns, to); column++)
            cells[column] = ' ';
    }

    // A cleared screen is also what a resize redraws from, so the row count restarts and
    // a stale taller screen cannot leave the status line looking like it is further down.
    private void Clear()
    {
        _rows.Clear();
        _lastRow = -1;
    }
}
