using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;

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
    private const int TerminalFontSize = 10;
    private static readonly Color TerminalBackground = Color.FromRgb(30, 30, 46);
    private static readonly SolidColorBrush TerminalBackgroundBrush =
        new(TerminalBackground);
    private static readonly FieldInfo? ScrollBarField = typeof(TerminalControl)
        .GetField("scrollbar", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Lazy<string> TerminalFont = new(ResolveTerminalFont);

    private readonly EasyTerminalControl _terminal;
    private readonly KeyboardFocusChangedEventHandler _focusChangedHandler;
    private readonly Border _startupSurface;
    private readonly TextBlock _startupStatus;
    private DispatcherTimer? _revealTimer;
    private bool _disposed;

    public NativeTerminalHost(string label, string commandLine, string workingDirectory)
    {
        _focusChangedHandler = Terminal_GotKeyboardFocus;
        Label = label;
        Background = TerminalBackgroundBrush;
        ClipToBounds = true;
        SnapsToDevicePixels = true;

        _startupStatus = new TextBlock
        {
            Text = $"Starting {label}…",
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(119, 130, 164)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };

        _startupSurface = new Border
        {
            Background = TerminalBackgroundBrush,
            Child = _startupStatus,
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
            FontFamilyWhenSettingTheme = new FontFamily(TerminalFont.Value),
            FontSizeWhenSettingTheme = TerminalFontSize,
            Win32InputMode = true,
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey |
                           EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
            Theme = BuildTheme(),
        };

        PrepareTerminalSurface();
        _terminal.Terminal.Loaded += Terminal_Loaded;
        _terminal.Terminal.AddHandler(
            Keyboard.GotKeyboardFocusEvent,
            _focusChangedHandler,
            handledEventsToo: true);
        _terminal.ConPTYTerm.TermReady += ConPtyTerm_TermReady;

        Children.Add(_startupSurface);
        Children.Add(_terminal);
        SetState(TerminalHostState.Starting);
    }

    public string Label { get; }

    public TerminalHostState State { get; private set; }

    public event Action<NativeTerminalHost, TerminalHostState>? StateChanged;

    public void FocusTerminal()
    {
        _terminal.Focus();
        _terminal.Terminal.Focus();
    }

    public void WriteLine(string command)
    {
        if (_disposed || State != TerminalHostState.Ready)
            return;

        _terminal.ConPTYTerm.WriteToTerm(command + "\r");
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
        TerminalFont.Value,
        TerminalFontSize,
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

    private static string ResolveTerminalFont()
    {
        string[] preferredFonts =
        [
            "DepartureMono Nerd Font Mono",
            "CaskaydiaCove Nerd Font Mono",
            "JetBrainsMono Nerd Font Mono",
        ];

        var installedFonts = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .ToArray();

        foreach (var preferredFont in preferredFonts)
        {
            var match = installedFonts.FirstOrDefault(font =>
                string.Equals(font, preferredFont, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return installedFonts.FirstOrDefault(font =>
                   font.Contains("Nerd Font Mono", StringComparison.OrdinalIgnoreCase) ||
                   font.Contains("NerdFontMono", StringComparison.OrdinalIgnoreCase))
               ?? "Cascadia Mono";
    }

    private static uint TerminalColor(byte red, byte green, byte blue) =>
        EasyTerminalControl.ColorToVal(Color.FromRgb(red, green, blue));

    private static TerminalTheme BuildTheme() => new()
    {
        DefaultBackground = TerminalColor(30, 30, 46),
        DefaultForeground = TerminalColor(205, 214, 244),
        DefaultSelectionBackground = TerminalColor(49, 50, 68),
        CursorStyle = CursorStyle.SteadyBar,
        ColorTable =
        [
            TerminalColor(30, 30, 46),
            TerminalColor(243, 139, 168),
            TerminalColor(166, 227, 161),
            TerminalColor(249, 226, 175),
            TerminalColor(137, 180, 250),
            TerminalColor(245, 194, 231),
            TerminalColor(148, 226, 213),
            TerminalColor(205, 214, 244),
            TerminalColor(88, 91, 112),
            TerminalColor(243, 139, 168),
            TerminalColor(166, 227, 161),
            TerminalColor(249, 226, 175),
            TerminalColor(137, 180, 250),
            TerminalColor(245, 194, 231),
            TerminalColor(148, 226, 213),
            TerminalColor(255, 255, 255),
        ],
    };

    private void Fail(string message)
    {
        _startupStatus.Text = message;
        _startupStatus.Foreground = new SolidColorBrush(Color.FromRgb(243, 139, 168));
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
        _terminal.Terminal.Loaded -= Terminal_Loaded;
        _terminal.Terminal.RemoveHandler(Keyboard.GotKeyboardFocusEvent, _focusChangedHandler);
        if (_terminal.ConPTYTerm is not null)
            _terminal.ConPTYTerm.TermReady -= ConPtyTerm_TermReady;

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
