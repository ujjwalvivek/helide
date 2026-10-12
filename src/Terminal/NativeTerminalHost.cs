using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using Helide.Theme;
using Microsoft.Terminal.Wpf;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfKeyEventHandler = System.Windows.Input.KeyEventHandler;

namespace Helide.Terminal;

internal enum TerminalHostState
{
    Starting,
    Ready,
    Failed,
    Stopped,
}

internal sealed class NativeTerminalHost : Grid, IDisposable
{
    private static class NativeMethods
    {
        public const uint SwpNoSize = 0x0001;
        public const uint SwpNoMove = 0x0002;
        public const uint SwpNoActivate = 0x0010;

        public const int GclpHbrBackground = -10;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
        public static extern IntPtr SetClassLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(uint colorRef);

        public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    }

    // Properties rather than cached fields: these are read again every time a theme
    // is applied, so a live pane picks up the palette it was switched to instead
    // of whatever was current when the type was first initialised.
    private static int TerminalFontSize => ThemePalette.FontSize(ThemePalette.FontSizeSmall);
    private static Color TerminalBackground => ThemePalette.Color(ThemePalette.TerminalBackgroundBrush);
    private static SolidColorBrush TerminalBackgroundBrush =>
        ThemePalette.Brush(ThemePalette.TerminalBackgroundBrush);
    private static FontFamily TerminalFont => ThemePalette.Font(ThemePalette.TerminalFontFamily);
    private static readonly FieldInfo? ScrollBarField = typeof(TerminalControl)
        .GetField("scrollbar", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly EasyTerminalControl _terminal;
    private readonly KeyboardFocusChangedEventHandler _focusChangedHandler;
    private readonly Border _startupSurface;
    private readonly TextBlock _startupStatus;
    private DispatcherTimer? _revealTimer;
    private HwndHost? _rendererHost;
    private bool _disposed;

    public NativeTerminalHost(
        string label,
        string commandLine,
        string workingDirectory,
        string? filePath = null,
        bool revealWhenSettled = false)
    {
        _revealWhenSettled = revealWhenSettled;
        _focusChangedHandler = Terminal_GotKeyboardFocus;
        Label = label;
        FilePath = filePath;
        Background = TerminalBackgroundBrush;
        ClipToBounds = true;
        SnapsToDevicePixels = true;

        _startupStatus = new TextBlock
        {
            Text = $"Starting {label}…",
            FontFamily = TerminalFont,
            FontSize = TerminalFontSize,
            Foreground = ThemePalette.Brush(ThemePalette.TerminalHintBrush),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };

        _startupSurface = new Border
        {
            Background = TerminalBackgroundBrush,
            Child = _startupStatus,
            // Never let the placeholder eat input: it covers the whole surface and
            // would swallow clicks while it is still visible.
            IsHitTestVisible = false,
        };

        _terminal = new EasyTerminalControl
        {
            StartupCommandLine = commandLine,
            WorkingDirectory = workingDirectory,
            Visibility = Visibility.Hidden,
            // A little air between the pane edge and the first column. The host behind
            // it is the same colour, so this reads as padding, and it costs under one
            // character cell per pane.
            Margin = new Thickness(6, 4, 4, 2),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = TerminalBackgroundBrush,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            FontFamilyWhenSettingTheme = TerminalFont,
            FontSizeWhenSettingTheme = TerminalFontSize,
            Win32InputMode = true,
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey |
                           EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
            Theme = BuildTheme(),
        };

        PrepareTerminalSurface();
        _terminal.Terminal.Loaded += Terminal_Loaded;
        _terminal.AddHandler(
            Keyboard.PreviewKeyDownEvent,
            new WpfKeyEventHandler(Terminal_PreviewKeyDown));
        _terminal.Terminal.AddHandler(
            Keyboard.GotKeyboardFocusEvent,
            _focusChangedHandler,
            handledEventsToo: true);
        _terminal.ConPTYTerm.TermReady += ConPtyTerm_TermReady;
        _terminal.ConPTYTerm.TerminalOutput += ConPtyTerm_TerminalOutput;
        ThemePalette.ThemeChanged += Palette_ThemeChanged;

        Children.Add(_startupSurface);
        Children.Add(_terminal);
        SetState(TerminalHostState.Starting);
    }

    private void Palette_ThemeChanged(object? sender, EventArgs e)
    {
        // WPF re-skins the chrome around the renderer on its own through
        // {DynamicResource}, but the ConPTY renderer is driven imperatively and
        // holds its own palette, so it has to be told.
        if (_disposed)
            return;

        // The host itself is painted with a brush taken when it was built, not looked up
        // live, and it shows: it is the margin around the renderer. Left alone it kept
        // the old theme's colour as a frame around every pane until a restart.
        Background = TerminalBackgroundBrush;
        _startupSurface.Background = TerminalBackgroundBrush;
        _terminal.Background = TerminalBackgroundBrush;

        try
        {
            PrepareTerminalSurface();
            ApplyTheme();
        }
        catch
        {
            // A pane that cannot repaint is not worth taking the app down for.
        }
    }

    public string Label { get; }

    public string? FilePath { get; }

    public TerminalHostState State { get; private set; }

    // Ticks of the last moment this host produced output, or 0 if it never has.
    // Written from the ConPTY reader thread and polled from the UI thread by the
    // attention timer.
    //
    // Deliberately a polled timestamp rather than an event. TerminalOutput fires
    // once per read chunk -- thousands per second while an agent works -- so
    // anything raising a notification per chunk would either flood the dispatcher
    // or need its own throttle. Recording when output happened leaves the
    // decision about what counts as interesting to the one place that can make it.
    public long LastOutputTicks => Interlocked.Read(ref _lastOutputTicks);

    private long _lastOutputTicks;
    private long _outputLength;

    // An agent takes a second or two to come up, and it does not arrive in one piece: the
    // launcher's console is blank first, then the TUI paints, and a restored conversation
    // repaints once more when its history loads. Shown as it happens that reads as a jump
    // and a flash, so for those panes the placeholder stays up until the output has
    // stopped moving. Everything else is revealed straight away, because helix and yazi
    // paint in a single frame and any wait there is just the app feeling slow.
    private readonly bool _revealWhenSettled;

    // Enough to be a real first paint rather than the handful of mode-setting sequences
    // the pseudoconsole emits on its own before the program has drawn anything.
    private const int SettledMinimumOutput = 400;
    private static readonly TimeSpan SettledQuiet = TimeSpan.FromMilliseconds(220);

    // A pane that never settles -- a spinner, a clock -- is shown anyway.
    private static readonly TimeSpan SettledLimit = TimeSpan.FromSeconds(4);

    private bool HasSettled(DateTime readyUtc)
    {
        var now = DateTime.UtcNow;
        if (now - readyUtc >= SettledLimit)
            return true;

        if (Interlocked.Read(ref _outputLength) < SettledMinimumOutput)
            return false;

        return now.Ticks - LastOutputTicks >= SettledQuiet.Ticks;
    }

    public event Action<NativeTerminalHost, TerminalHostState>? StateChanged;

    public void FocusTerminal()
    {
        _terminal.Focus();
        _terminal.Terminal.Focus();

        // WPF focus is not enough on its own. The renderer is a child HWND of the
        // Helide window (Microsoft.Terminal.Wpf.TerminalContainer derives from
        // HwndHost), and a window can hand real keyboard focus to exactly one child.
        // Without handing focus to the HWND, keystrokes keep going to whichever pane
        // last held it: arrows would move the cursor in one pane while typing landed
        // in another.
        var handle = RendererHandle();
        if (handle != IntPtr.Zero)
            NativeMethods.SetFocus(handle);
    }

    // Resolved lazily because the renderer only builds its HwndHost once the control
    // has been loaded and laid out.
    private IntPtr RendererHandle()
    {
        var host = FindRendererHost();
        if (host is null)
            return IntPtr.Zero;

        try
        {
            return host.Handle;
        }
        catch
        {
            // HwndHost throws while its window is being torn down.
            return IntPtr.Zero;
        }
    }

    private HwndHost? FindRendererHost()
    {
        if (_rendererHost is not null)
            return _rendererHost;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(_terminal); index++)
        {
            var match = FindRendererHost(VisualTreeHelper.GetChild(_terminal, index));
            if (match is not null)
            {
                _rendererHost = match;
                return match;
            }
        }

        return null;
    }

    private static HwndHost? FindRendererHost(DependencyObject root)
    {
        if (root is HwndHost host)
            return host;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindRendererHost(VisualTreeHelper.GetChild(root, index));
            if (match is not null)
                return match;
        }

        return null;
    }

    public void WriteLine(string command)
    {
        if (_disposed || State != TerminalHostState.Ready)
            return;

        _terminal.ConPTYTerm.WriteToTerm(command + "\r");
    }

    // Called after the host pane is expanded again. The renderer already resizes
    // itself when arranged at a real size, so this only forces a fresh measure
    // and repaint in case the hidden HWND came back stale.
    public void Refresh()
    {
        if (_disposed)
            return;

        UpdateLayout();
        InvalidateVisual();
    }

    // Panel.SetZIndex only reorders WPF visuals; it has no authority over the child
    // HWNDs that back these renderers, and those are what actually get painted and
    // clicked. So stacking several hosts in one Grid is only safe if the one that
    // should be on top is explicitly moved there. Toggling Visibility alone is not
    // enough: SW_SHOW does not change z-order.
    public void BringToFront()
    {
        if (_disposed)
            return;

        UpdateLayout();
        InvalidateVisual();
        _terminal.InvalidateMeasure();

        RaiseRenderer();
        FocusTerminal();
    }

    private void RaiseRenderer()
    {
        var handle = RendererHandle();
        if (handle == IntPtr.Zero)
            return;

        try
        {
            NativeMethods.SetWindowPos(
                handle,
                new IntPtr(-1), // HWND_TOP
                0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize |
                NativeMethods.SwpNoActivate);
        }
        catch
        {
            // The renderer window is on its way out.
        }
    }

// Enter has to reach the pseudoconsole as a carriage return. The renderer sends a
    // line feed instead, and that is a real difference rather than an equivalent
    // encoding: crossterm only promotes LF to KeyCode::Enter when the tty is NOT in
    // raw mode. Every TUI in here (yazi, lazygit, helix) puts its terminal in raw
    // mode, so LF arrives as Ctrl+J and Enter does nothing -- yazi would highlight
    // files, quit on `q`, and refuse to open or enter anything. Shells and opencode
    // accept LF, which is why only the file browser looked broken.
    private void Terminal_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_disposed)
            return;

        if (IsPasteGesture(e) && PasteFromClipboard())
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        WriteRaw("\r");
        e.Handled = true;
    }

    // Nothing here pastes unless it is told to. Microsoft.Terminal.Wpf renders and
    // turns keys into VT, and copies a selection out, but has no way in for the
    // clipboard: Ctrl+V reaches the program as a bare keystroke, which only a program
    // that reads the clipboard for itself does anything with. Helix does -- it talks to
    // the Win32 clipboard on its own, which is why yanking and pasting worked there and
    // nowhere else. A shell, an agent TUI or yazi do not, so the paste has to be done
    // here, the way Windows Terminal does it.
    private static bool IsPasteGesture(WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.V)
            return modifiers == ModifierKeys.Control
                || modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

        return key == Key.Insert && modifiers == ModifierKeys.Shift;
    }

    // Whether the program running in the pane has asked for bracketed paste
    // (DECSET 2004). Written from the ConPTY reader thread, read on the UI thread.
    private volatile bool _bracketedPaste;

    // The last few characters of the previous chunk of output. The mode switch is
    // eight characters and a read can end anywhere inside it.
    private string _modeTail = "";

    private const string BracketedPasteOn = "\u001b[?2004h";
    private const string BracketedPasteOff = "\u001b[?2004l";

    private void TrackPasteMode(object? data)
    {
        var text = data as string ?? (data is char[] chars ? new string(chars) : null);
        if (string.IsNullOrEmpty(text))
            return;

        var window = _modeTail + text;
        var on = window.LastIndexOf(BracketedPasteOn, StringComparison.Ordinal);
        var off = window.LastIndexOf(BracketedPasteOff, StringComparison.Ordinal);
        if (on >= 0 || off >= 0)
            _bracketedPaste = on > off;

        _modeTail = window.Length > BracketedPasteOn.Length - 1
            ? window[^(BracketedPasteOn.Length - 1)..]
            : window;
    }

    // Returns whether anything was pasted. A clipboard with no text on it (an image,
    // say) is left to the program: the key goes through untouched.
    private bool PasteFromClipboard()
    {
        // The editor pane keeps its own clipboard handling, and in it pasted text lands
        // as keystrokes: a normal-mode `d` or `:` in the middle of a paste would run as a
        // command. Helix is not given this one.
        if (State != TerminalHostState.Ready ||
            string.Equals(Label, "helix", StringComparison.OrdinalIgnoreCase))
            return false;

        var text = ReadClipboardText();
        if (string.IsNullOrEmpty(text))
            return false;

        // Whatever is left of an escape sequence in the text could end the bracket early
        // and turn the rest of the paste into input, so escapes go. Line breaks are sent
        // as the carriage return Enter is, which is what every program in here reads as
        // a new line.
        var cleaned = new System.Text.StringBuilder(text.Length);
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

        if (cleaned.Length == 0)
            return false;

        var payload = cleaned.ToString();
        WriteRaw(_bracketedPaste ? "\u001b[200~" + payload + "\u001b[201~" : payload);
        return true;
    }

    private static string? ReadClipboardText()
    {
        // The clipboard is a shared resource and whoever copied may still be holding it
        // open; a few short retries is what the framework's own helpers do.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
                    return System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);

                // Files copied in Explorer paste as their paths, quoted, like they do in
                // Windows Terminal.
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var paths = System.Windows.Clipboard.GetFileDropList()
                        .Cast<string>()
                        .Select(path => path.Contains(' ') ? $"\"{path}\"" : path);
                    return string.Join(' ', paths);
                }

                return null;
            }
            catch (COMException)
            {
                Thread.Sleep(10);
            }
            catch (ExternalException)
            {
                Thread.Sleep(10);
            }
        }

        return null;
    }

    private void WriteRaw(string text)
    {
        if (_terminal.ConPTYTerm is null)
            return;

        try
        {
            _terminal.ConPTYTerm.WriteToTerm(text);
        }
        catch
        {
            // The pseudoconsole is gone; there is nowhere to send the key.
        }
    }

    private void Terminal_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            PrepareTerminalSurface();
            ApplyTheme();
        }
        catch
        {
            Fail($"{Label} could not initialize its renderer.");
        }
    }

    private void Terminal_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        // A first click into the HWND-backed renderer can leave a viewport-sized
        // transient selection behind. Clear it after WPF has completed the focus
        // handoff so switching panes never looks like a theme/background change.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!_disposed && _terminal.Terminal.IsKeyboardFocusWithin)
                _terminal.Terminal.GetSelectedText();
        }));
    }

    private void ConPtyTerm_TerminalOutput(object? sender, TerminalOutputEventArgs output)
    {
        Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
        Interlocked.Add(ref _outputLength, output.Data?.Length ?? 0);
    }

    private void ConPtyTerm_TermReady(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (_disposed)
                return;

            PrepareTerminalSurface();
            ApplyTheme();

            var readyUtc = DateTime.UtcNow;

            _revealTimer?.Stop();
            _revealTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(_revealWhenSettled ? 40 : 90),
            };
            _revealTimer.Tick += (_, _) =>
            {
                if (_revealWhenSettled && !_disposed && !HasSettled(readyUtc))
                    return;

                _revealTimer?.Stop();
                if (_disposed)
                    return;

                _startupSurface.Visibility = Visibility.Collapsed;
                _terminal.Visibility = Visibility.Visible;
                SetState(TerminalHostState.Ready);
            };
            _revealTimer.Start();
        }));
    }

    private void PrepareTerminalSurface()
    {
        _terminal.Terminal.Margin = new Thickness(0);
        _terminal.Terminal.Padding = new Thickness(0);
        _terminal.Terminal.BorderThickness = new Thickness(0);
        _terminal.Terminal.Background = TerminalBackgroundBrush;
        _terminal.Terminal.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        _terminal.Terminal.VerticalContentAlignment = VerticalAlignment.Stretch;

        if (_terminal.Terminal.Content is System.Windows.Controls.Panel panel)
            panel.Background = TerminalBackgroundBrush;

        var privateScrollBar = ScrollBarField?.GetValue(_terminal.Terminal) as WpfScrollBar;
        if (privateScrollBar is not null)
            HideTerminalScrollBar(privateScrollBar);

        foreach (var scrollBar in FindVisualChildren<WpfScrollBar>(_terminal.Terminal))
            HideTerminalScrollBar(scrollBar);
    }

    private void ApplyTheme()
    {
        _terminal.Terminal.SetTheme(
            BuildTheme(),
            TerminalFont.Source,
            (short)TerminalFontSize,
            TerminalBackground);

        ApplyWindowBackground();
    }

    // One brush per colour for the life of the process. A window class keeps the handle it
    // is given, so these are never freed, and there are only ever as many as themes used.
    private static readonly Dictionary<uint, IntPtr> BackgroundBrushes = [];

    // The renderer is a native window, and until it has drawn its first frame Windows
    // fills it with its class background, which is white. That is the flash when a pane
    // appears, and the white strip down the right edge: the terminal only draws whole
    // character cells, so the last few pixels of the window are never the renderer's and
    // keep showing whatever is underneath. Giving the class the terminal's own colour
    // makes both of those the same colour as everything around them.
    private void ApplyWindowBackground()
    {
        var handle = RendererHandle();
        if (handle == IntPtr.Zero)
            return;

        try
        {
            var colour = TerminalBackground;
            var colorRef = (uint)(colour.R | (colour.G << 8) | (colour.B << 16));

            if (!BackgroundBrushes.TryGetValue(colorRef, out var brush))
            {
                brush = NativeMethods.CreateSolidBrush(colorRef);
                if (brush == IntPtr.Zero)
                    return;

                BackgroundBrushes[colorRef] = brush;
            }

            NativeMethods.SetClassLongPtr(handle, NativeMethods.GclpHbrBackground, brush);

            // The window that paints can be a child of the one WPF hands out.
            NativeMethods.EnumChildWindows(
                handle,
                (child, _) =>
                {
                    NativeMethods.SetClassLongPtr(child, NativeMethods.GclpHbrBackground, brush);
                    return true;
                },
                IntPtr.Zero);

            NativeMethods.InvalidateRect(handle, IntPtr.Zero, true);
        }
        catch
        {
            // Cosmetic. A pane that keeps the default background still works.
        }
    }

    private static void HideTerminalScrollBar(WpfScrollBar scrollBar)
    {
        scrollBar.Visibility = Visibility.Collapsed;
        scrollBar.IsHitTestVisible = false;
        scrollBar.Width = 0;
        scrollBar.Height = 0;
        scrollBar.Margin = new Thickness(0);
        scrollBar.Padding = new Thickness(0);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static uint TerminalColor(string key) =>
        EasyTerminalControl.ColorToVal(ThemePalette.Color(key));

    private static TerminalTheme BuildTheme() => new()
    {
        DefaultBackground = TerminalColor(ThemePalette.TerminalBackgroundBrush),
        DefaultForeground = TerminalColor(ThemePalette.TerminalForegroundBrush),
        DefaultSelectionBackground = TerminalColor(ThemePalette.TerminalSelectionBrush),
        CursorStyle = CursorStyle.SteadyBar,
        ColorTable =
        [
            .. ThemePalette.AnsiBrushKeys.Select(TerminalColor),
        ],
    };

private void Fail(string message)
    {
        _startupStatus.Text = message;
        _startupStatus.Foreground = ThemePalette.Brush(ThemePalette.DangerBrush);
        SetState(TerminalHostState.Failed);
    }

    private void SetState(TerminalHostState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _revealTimer?.Stop();

        ThemePalette.ThemeChanged -= Palette_ThemeChanged;
        _terminal.Terminal.Loaded -= Terminal_Loaded;
        _terminal.Terminal.RemoveHandler(Keyboard.GotKeyboardFocusEvent, _focusChangedHandler);
        if (_terminal.ConPTYTerm is not null)
            _terminal.ConPTYTerm.TermReady -= ConPtyTerm_TermReady;
        if (_terminal.ConPTYTerm is not null)
            _terminal.ConPTYTerm.TerminalOutput -= ConPtyTerm_TerminalOutput;

        try
        {
            _terminal.ConPTYTerm?.CloseStdinToApp();
            _terminal.ConPTYTerm?.StopExternalTermOnly();
            _terminal.DisconnectConPTYTerm();
        }
        catch
        {
        }

        try
        {
            var process = _terminal.ConPTYTerm?.Process;
            if (process is not null && !process.HasExited)
                process.Kill(true);
        }
        catch
        {
        }

        SetState(TerminalHostState.Stopped);
    }
}
