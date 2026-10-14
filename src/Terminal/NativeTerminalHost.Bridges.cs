using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Helide.Terminal;

// What other panes can ask of this one: text put in as if pasted, a command run in the
// editor, and what a shell printed. Each panel stays its own process; these are the seams
// between them.
internal sealed partial class NativeTerminalHost
{
    /// <summary>
    /// Puts text in the program's input as a paste, never as keystrokes.
    /// </summary>
    /// <remarks>
    /// A paste is what makes a multi-line message safe to hand an agent: typed, every line
    /// break would be Enter and send the first line on its own. Inside the bracket the
    /// program takes it all as one piece of input, and nothing is submitted.
    /// </remarks>
    public bool PasteText(string text)
    {
        if (_disposed || State != TerminalHostState.Ready)
            return false;

        var payload = CleanForPaste(text);
        if (payload.Length == 0)
            return false;

        WriteRaw(_bracketedPaste ? "\u001b[200~" + payload + "\u001b[201~" : payload);
        return true;
    }

    // Whatever is left of an escape sequence in the text could end the bracket early and
    // turn the rest of the paste into input, so escapes go. Line breaks are sent as the
    // carriage return Enter is, which is what every program in here reads as a new line.
    private static string CleanForPaste(string text)
    {
        var cleaned = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == '\r')
            {
                cleaned.Append('\r');
                if (index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
            }
            else if (c == '\n')
                cleaned.Append('\r');
            else if (c == '\t' || c >= ' ' && c != '\u007f')
                cleaned.Append(c);
        }

        return cleaned.ToString();
    }

    /// <summary>
    /// Runs a command on Helix's command line, such as <c>:reload-all</c>.
    /// </summary>
    /// <remarks>
    /// Escape first, so the colon lands in normal mode wherever the editor was. A bare
    /// ESC byte will not do: the pseudoconsole cannot tell it from the start of a key
    /// sequence and holds it, and it arrives with the next key as an Alt chord. So it is
    /// sent the way the renderer sends keys, as a win32-input-mode key press and release,
    /// which means Escape and nothing else.
    /// </remarks>
    public void RunEditorCommand(string command)
    {
        if (_disposed || State != TerminalHostState.Ready || _statusProbe is null)
            return;

        WriteRaw("\u001b[27;1;27;1;0;1_\u001b[27;1;27;0;0;1_");

        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_disposed)
                WriteRaw(command + "\r");
        };
        timer.Start();
    }

    /// <summary>The text selected in the pane, or null.</summary>
    public string? SelectedText()
    {
        try
        {
            var text = _terminal.Terminal.GetSelectedText();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this pane keeps what was printed since Enter was last pressed in it. Set on
    /// shell panes, where that is the output of the command just run.
    /// </summary>
    public bool CapturesCommandOutput { get; set; }

    // Plenty for a build error or a stack trace; the start of a longer run is the part
    // least likely to matter.
    private const int CaptureLimit = 64 * 1024;

    private readonly StringBuilder _commandOutput = new();

    // Runs on the ConPTY reader thread.
    private void CaptureOutput(string? data)
    {
        if (!CapturesCommandOutput || string.IsNullOrEmpty(data))
            return;

        lock (_commandOutput)
        {
            _commandOutput.Append(data);
            if (_commandOutput.Length > CaptureLimit)
                _commandOutput.Remove(0, _commandOutput.Length - CaptureLimit);
        }
    }

    // Enter starts a new command, so what came before it belongs to the last one.
    //
    // Its last lines are the prompt and the command just typed, and the same prompt will
    // close the output of this one. They are kept so it can be recognised there: whatever
    // the shell's prompt looks like -- two lines, a clock in it, a theme -- it starts the
    // same way each time. That is learnt from the screen, not from the shell's config.
    private void ResetCapturedOutput()
    {
        if (!CapturesCommandOutput)
            return;

        string previous;
        lock (_commandOutput)
        {
            previous = _commandOutput.ToString();
            _commandOutput.Clear();
        }

        var lines = CleanLines(previous).Where(line => line.Length > 0).ToList();
        if (lines.Count > 0)
            _promptLines = lines.Skip(Math.Max(0, lines.Count - PromptDepth)).ToArray();
    }

    // The most lines a prompt is taken to span, the command line included.
    private const int PromptDepth = 3;

    // How much of a line has to match a prompt line for it to be one.
    private const int PromptPrefix = 8;

    private string[] _promptLines = [];

    private bool LooksLikePrompt(string line)
    {
        if (Prompt.IsMatch(line))
            return true;

        foreach (var prompt in _promptLines)
        {
            var length = Math.Min(PromptPrefix, Math.Min(prompt.Length, line.Length));
            if (length >= PromptPrefix && string.CompareOrdinal(line, 0, prompt, 0, length) == 0)
                return true;
        }

        return false;
    }

    private static readonly Regex Escapes = new(
        @"\u001b\[[0-?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)|\u001b[P_^X][^\u001b]*\u001b\\|\u001b[ -/]*[0-~]",
        RegexOptions.Compiled);

    // A shell prompt on a line of its own: PowerShell's "PS C:\x>", cmd's "C:\x>", and
    // the "$" or "#" of a Unix-style shell.
    private static readonly Regex Prompt = new(
        @"^(?:PS .*>|[A-Za-z]:\\.*>|.*[$#%])\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// What the shell printed since Enter was last pressed in it, as plain text, without
    /// the prompt it came back to. Null when there is nothing.
    /// </summary>
    /// <remarks>
    /// Read from the stream rather than the screen, so colours and cursor movement are
    /// dropped and a line redrawn in place -- a progress bar -- is kept as it ended.
    /// </remarks>
    public string? LastCommandOutput()
    {
        string raw;
        lock (_commandOutput)
            raw = _commandOutput.ToString();

        var lines = CleanLines(raw);

        // The prompt the shell came back to closes the output; one redrawn as Enter was
        // pressed can open it.
        Trim(lines, fromEnd: true);
        Trim(lines, fromEnd: false);

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private void Trim(List<string> lines, bool fromEnd)
    {
        var removed = 0;
        while (lines.Count > 0)
        {
            var index = fromEnd ? lines.Count - 1 : 0;
            if (lines[index].Length == 0)
            {
                lines.RemoveAt(index);
                continue;
            }

            if (removed == PromptDepth || !LooksLikePrompt(lines[index]))
                break;

            lines.RemoveAt(index);
            removed++;
        }
    }

    private static List<string> CleanLines(string raw)
    {
        var lines = new List<string>();
        if (raw.Length == 0)
            return lines;

        foreach (var piece in Escapes.Replace(raw, string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            // A carriage return on its own goes back to the start of the line, and what
            // follows writes over it.
            var line = piece;
            var back = line.LastIndexOf('\r');
            if (back >= 0)
                line = line[(back + 1)..];

            var kept = new StringBuilder(line.Length);
            foreach (var c in line)
            {
                if (c == '\b')
                {
                    if (kept.Length > 0)
                        kept.Length--;
                }
                else if (c == '\t' || c >= ' ')
                    kept.Append(c);
            }

            // A line of nothing but spaces and icon-font glyphs -- what is left of a themed
            // prompt redrawn in place -- says nothing, and counts as blank.
            var text = kept.ToString().TrimEnd();
            lines.Add(text.All(c => char.IsWhiteSpace(c)
                                    || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.PrivateUse)
                ? string.Empty
                : text);
        }

        return lines;
    }
}
