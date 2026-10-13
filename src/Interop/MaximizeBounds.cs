using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Helide.Theme;

namespace Helide.Interop;

internal static class MaximizeBounds
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public nint Hwnd;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Bounds;
        public nint Param;
    }

    // Set while zen mode is covering the screen. A maximised window is then sized to the
    // whole monitor rather than to the space around the taskbar.
    public static bool FillMonitor { get; set; }

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcPaint = 0x0085;
    private const int WmNcUahDrawCaption = 0x00AE;
    private const int WmNcUahDrawFrame = 0x00AF;
    private const int WvrRedraw = 0x0300;

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hwnd);
    private const uint AbmGetState = 4;
    private const uint AbmGetTaskbarPos = 5;
    private const nint AbsAutoHide = 1;

    [DllImport("shell32.dll")]
    private static extern nint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, ref Rect rect, nint brush);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    /// <summary>
    /// Keeps a maximised window one pixel short of the edge an auto-hiding taskbar lives
    /// on. With the taskbar hidden the work area is the whole monitor, so a window that
    /// draws on every pixel of it is taken by Windows for a full-screen app, and the
    /// taskbar can never be brought up. Ordinary windows are left that pixel by the
    /// system; this window draws its own frame, so it has to leave it itself.
    /// </summary>
    public static void Attach(Window window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook(WindowProc);
    }

    private static nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            // Sent by the theme engine to have the stock caption and frame drawn. The
            // window has no such frame, its drawing covers all of it, so the stock caption
            // buttons land on top of the custom title bar and stay there until that corner
            // is next drawn. Moving the pointer along the edges of a maximised window is
            // enough to set it off. Nothing here is the system's to paint.
            case WmNcUahDrawCaption or WmNcUahDrawFrame:
                handled = true;
                return nint.Zero;

            case WmNcCalcSize:
                return CalcSize(hwnd, wParam, lParam, ref handled);

            case WmNcPaint:
                PaintHeldBack(hwnd);
                return nint.Zero;

            default:
                return nint.Zero;
        }
    }

    // The pixel for an auto-hiding taskbar is taken out of the area the window draws on,
    // not out of the window. The window stays where Windows maximises it, so going in and
    // out of zen mode never moves or resizes it: a window that grows shows a strip of
    // whatever is behind it until its content has been laid out again. Windows allows no
    // position in between; a maximised window that reaches past the monitor at all is
    // put back to the system's own.
    private static nint CalcSize(nint hwnd, nint wParam, nint lParam, ref bool handled)
    {
        if (!HeldBack(hwnd, out var monitor, out var edge))
            return nint.Zero;

        // The proposed window rectangle, whichever form the message takes: it is the
        // first field either way. Left as it is, the whole window is drawn on.
        var rect = Marshal.PtrToStructure<Rect>(lParam);
        switch (edge)
        {
            case 0: rect.Left = Math.Max(rect.Left, monitor.Left + 1); break;
            case 1: rect.Top = Math.Max(rect.Top, monitor.Top + 1); break;
            case 2: rect.Right = Math.Min(rect.Right, monitor.Right - 1); break;
            default: rect.Bottom = Math.Min(rect.Bottom, monitor.Bottom - 1); break;
        }
        Marshal.StructureToPtr(rect, lParam, false);

        handled = true;
        return wParam != nint.Zero ? WvrRedraw : nint.Zero;
    }

    // Nothing draws on the pixel held back, so it keeps whatever was last there: white on
    // a window that has just opened. It is filled with the window's own colour.
    private static void PaintHeldBack(nint hwnd)
    {
        if (!HeldBack(hwnd, out var monitor, out var edge) || !GetWindowRect(hwnd, out var window))
            return;

        // The row or column of the monitor along the taskbar's edge, in the window's own
        // coordinates.
        var strip = monitor;
        switch (edge)
        {
            case 0: strip.Right = strip.Left + 1; break;
            case 1: strip.Bottom = strip.Top + 1; break;
            case 2: strip.Left = strip.Right - 1; break;
            default: strip.Top = strip.Bottom - 1; break;
        }
        strip.Left -= window.Left;
        strip.Right -= window.Left;
        strip.Top -= window.Top;
        strip.Bottom -= window.Top;

        var dc = GetWindowDC(hwnd);
        if (dc == nint.Zero)
            return;

        // A COLORREF is 0x00BBGGRR.
        var color = ThemePalette.Color(ThemePalette.WindowBrush);
        var brush = CreateSolidBrush(color.R | (color.G << 8) | (color.B << 16));
        FillRect(dc, ref strip, brush);
        DeleteObject(brush);
        ReleaseDC(hwnd, dc);
    }

    // Whether a pixel is being held back from an auto-hiding taskbar, and on which edge of
    // the monitor: 0 left, 1 top, 2 right, 3 bottom. Not in zen mode, which covers it.
    private static bool HeldBack(nint hwnd, out Rect monitor, out uint edge)
    {
        monitor = default;
        edge = 0;

        if (FillMonitor || !IsZoomed(hwnd) || !MonitorOf(hwnd, out var info))
            return false;

        monitor = info.Monitor;

        var bar = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        if ((SHAppBarMessage(AbmGetState, ref bar) & AbsAutoHide) == 0
            || SHAppBarMessage(AbmGetTaskbarPos, ref bar) == nint.Zero)
            return false;

        // Only the monitor the taskbar is on.
        if (bar.Bounds.Right <= monitor.Left || bar.Bounds.Left >= monitor.Right
            || bar.Bounds.Bottom <= monitor.Top || bar.Bounds.Top >= monitor.Bottom)
            return false;

        edge = bar.Edge;
        return true;
    }

    private static bool MonitorOf(nint hwnd, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return monitor != nint.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static int Frame(nint hwnd)
    {
        var dpi = Math.Max(96u, GetDpiForWindow(hwnd));
        return GetSystemMetricsForDpi(SmCxSizeFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
    }

    private static Rect Inflate(Rect rect, int by)
    {
        rect.Left -= by;
        rect.Top -= by;
        rect.Right += by;
        rect.Bottom += by;
        return rect;
    }

    private const int SmCxSizeFrame = 32;
    private const int SmCxPaddedBorder = 92;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>
    /// Puts an already maximised window where <see cref="FillMonitor"/> says it belongs,
    /// and has the area it draws on worked out again. Windows only asks when the window
    /// changes state, and changing state to get the question asked would flash the window
    /// at its restored size.
    /// </summary>
    public static void Reapply(Window window)
    {
        // Only a window Windows itself holds as maximised. One restored as maximised is
        // not yet, when its handle is first made, and must not be moved as if it were.
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero || !IsZoomed(hwnd) || !MonitorOf(hwnd, out var info))
            return;

        // The frame is added the way the system does it for a maximised window: the
        // overhang is off-screen, and the content is inset back by Measure. Around the
        // monitor in zen mode, which covers a taskbar that is showing; around the work
        // area otherwise. With an auto-hiding taskbar the two are the same rectangle, and
        // all that changes is the pixel CalcSize holds back.
        var rect = Inflate(FillMonitor ? info.Monitor : info.Work, Frame(hwnd));

        SetWindowPos(
            hwnd, nint.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    public static Thickness Measure(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero || !GetClientRect(hwnd, out var rect))
            return new Thickness(0);

        if (!MonitorOf(hwnd, out var info))
            return new Thickness(0);

        // The area drawn on, in screen coordinates. It is the window's own rectangle but
        // for the pixel held back from an auto-hiding taskbar.
        var origin = new Point();
        ClientToScreen(hwnd, ref origin);
        rect.Left += origin.X;
        rect.Right += origin.X;
        rect.Top += origin.Y;
        rect.Bottom += origin.Y;

        // In zen mode the content comes back to the edge of the monitor, otherwise to the
        // edge of the work area. Never negative: the pixel held back from the taskbar is
        // the drawn area being smaller, not content to pull out.
        var area = FillMonitor ? info.Monitor : info.Work;
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        return new Thickness(
            Math.Max(0, area.Left - rect.Left) / scale,
            Math.Max(0, area.Top - rect.Top) / scale,
            Math.Max(0, rect.Right - area.Right) / scale,
            Math.Max(0, rect.Bottom - area.Bottom) / scale);
    }
}
