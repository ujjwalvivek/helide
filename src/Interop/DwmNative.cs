using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Helide.Theme;

namespace Helide.Interop;

internal static class DwmNative
{
    private const uint DwmwaUseImmersiveDarkMode = 20;
    private const uint DwmwaWindowCornerPreference = 33;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmwaCaptionColor = 35;
    private const uint DwmwaTextColor = 36;
    private const uint DwmwaSystemBackdropType = 38;
    // Square, like everything inside it. Windows 11 rounds a window's corners itself
    // unless told not to, whatever the window draws.
    private const int DwmwcpDoNotRound = 1;
    private const int DwmsbtTransientWindow = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd, uint attribute, ref int value, int valueSize);

    // Called again on every theme change: DWM keeps the colours it was given, so the
    // frame is the one part of the window a palette swap cannot reach by itself.
    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0)
                return;

            Set(hwnd, DwmwaUseImmersiveDarkMode, 1);
            Set(hwnd, DwmwaWindowCornerPreference, DwmwcpDoNotRound);
            Set(hwnd, DwmwaSystemBackdropType, DwmsbtTransientWindow);
            Set(hwnd, DwmwaBorderColor, ColorRef(ThemePalette.BorderBrush));
            Set(hwnd, DwmwaCaptionColor, ColorRef(ThemePalette.PaneHeaderBrush));
            Set(hwnd, DwmwaTextColor, ColorRef(ThemePalette.TextBrush));
        }
        catch
        {
            // The opaque theme remains available on older Windows builds.
        }
    }

    private static void Set(nint hwnd, uint attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    // A COLORREF is 0x00BBGGRR and has no alpha, so a translucent brush goes over opaque.
    private static int ColorRef(string brushKey)
    {
        var color = ThemePalette.Color(brushKey);
        return color.R | (color.G << 8) | (color.B << 16);
    }
}
