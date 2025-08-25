using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

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

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    public static Thickness Measure(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var rect))
            return new Thickness(0);

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
            return new Thickness(0);

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return new Thickness(0);

        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        return new Thickness(
            (info.Work.Left - rect.Left) / scale,
            (info.Work.Top - rect.Top) / scale,
            (rect.Right - info.Work.Right) / scale,
            (rect.Bottom - info.Work.Bottom) / scale);
    }
}