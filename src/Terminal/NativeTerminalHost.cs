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

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetFocus(IntPtr hWnd);

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
        string? filePath = null)
    {
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
            Margin = new Thickness(0),
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
        if (e.Key != Key.Enter || _disposed)
            return;

        WriteRaw("\r");
        e.Handled = true;
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

    private void ConPtyTerm_TerminalOutput(object? sender, TerminalOutputEventArgs output) =>
        Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);

    private void ConPtyTerm_TermReady(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (_disposed)
                return;

            PrepareTerminalSurface();
            ApplyTheme();

            _revealTimer?.Stop();
            _revealTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(90),
            };
            _revealTimer.Tick += (_, _) =>
            {
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

    private void ApplyTheme() => _terminal.Terminal.SetTheme(
        BuildTheme(),
        TerminalFont.Source,
        (short)TerminalFontSize,
        TerminalBackground);

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
