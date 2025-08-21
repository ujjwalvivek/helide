using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Helide.Interop;

internal static class DwmNative
{
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const uint DwmwaWindowCornerPreference = 33;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmwaCaptionColor = 35;
    private const uint DwmwaTextColor = 36;
    private const uint DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;
    private const int DwmsbtTransientWindow = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd, uint attribute, ref int value, int valueSize);

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Set(hwnd, DwmwaUseImmersiveDarkMode, 1);
            Set(hwnd, DwmwaWindowCornerPreference, DwmwcpRound);
            Set(hwnd, DwmwaSystemBackdropType, DwmsbtTransientWindow);
            Set(hwnd, DwmwaBorderColor, ColorRef(49, 50, 68));
            Set(hwnd, DwmwaCaptionColor, ColorRef(24, 24, 37));
            Set(hwnd, DwmwaTextColor, ColorRef(205, 214, 244));
        }
        catch
        {
            // The opaque theme remains available on older Windows builds.
        }
    }

    private static void Set(nint hwnd, uint attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    private static int ColorRef(byte red, byte green, byte blue) =>
        red | (green << 8) | (blue << 16);
}
