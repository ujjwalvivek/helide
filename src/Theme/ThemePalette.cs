using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using SystemFonts = System.Windows.SystemFonts;

namespace Helide.Theme;

// Code-behind reads the same tokens the XAML uses instead of hardcoding RGB.
// Everything resolves through the merged Theme.xaml dictionary, so a value can
// only ever be defined in one place.
internal static class ThemePalette
{
    public const string WindowBrush = nameof(WindowBrush);
    public const string SurfaceBrush = nameof(SurfaceBrush);
    public const string RaisedBrush = nameof(RaisedBrush);
    public const string TerminalBrush = nameof(TerminalBrush);
    public const string PaneHeaderBrush = nameof(PaneHeaderBrush);
    public const string PopoverBrush = nameof(PopoverBrush);
    public const string InteractiveBrush = nameof(InteractiveBrush);
    public const string PressedBrush = nameof(PressedBrush);
    public const string PressedStrongBrush = nameof(PressedStrongBrush);
    public const string TabActiveBackgroundBrush = nameof(TabActiveBackgroundBrush);
    public const string BorderBrush = nameof(BorderBrush);
    public const string TextBrush = nameof(TextBrush);
    public const string TextStrongBrush = nameof(TextStrongBrush);
    public const string TextMutedBrush = nameof(TextMutedBrush);
    public const string TextSubtleBrush = nameof(TextSubtleBrush);
    public const string TextFaintBrush = nameof(TextFaintBrush);
    public const string TextIdleBrush = nameof(TextIdleBrush);
    public const string TextPaneTitleBrush = nameof(TextPaneTitleBrush);
    public const string TextOnAccentBrush = nameof(TextOnAccentBrush);
    public const string TextOnCaptionCloseBrush = nameof(TextOnCaptionCloseBrush);
    public const string TerminalHintBrush = nameof(TerminalHintBrush);
    public const string AccentBrush = nameof(AccentBrush);
    public const string DangerBrush = nameof(DangerBrush);
    public const string SuccessBrush = nameof(SuccessBrush);
    public const string CaptionCloseBrush = nameof(CaptionCloseBrush);
    public const string CaptionClosePressedBrush = nameof(CaptionClosePressedBrush);
    public const string TerminalBackgroundBrush = nameof(TerminalBackgroundBrush);
    public const string TerminalForegroundBrush = nameof(TerminalForegroundBrush);
    public const string TerminalSelectionBrush = nameof(TerminalSelectionBrush);
    public const string TerminalAnsi0Brush = nameof(TerminalAnsi0Brush);
    public const string TerminalAnsi1Brush = nameof(TerminalAnsi1Brush);
    public const string TerminalAnsi2Brush = nameof(TerminalAnsi2Brush);
    public const string TerminalAnsi3Brush = nameof(TerminalAnsi3Brush);
    public const string TerminalAnsi4Brush = nameof(TerminalAnsi4Brush);
    public const string TerminalAnsi5Brush = nameof(TerminalAnsi5Brush);
    public const string TerminalAnsi6Brush = nameof(TerminalAnsi6Brush);
    public const string TerminalAnsi7Brush = nameof(TerminalAnsi7Brush);
    public const string TerminalAnsi8Brush = nameof(TerminalAnsi8Brush);
    public const string TerminalAnsi9Brush = nameof(TerminalAnsi9Brush);
    public const string TerminalAnsi10Brush = nameof(TerminalAnsi10Brush);
    public const string TerminalAnsi11Brush = nameof(TerminalAnsi11Brush);
    public const string TerminalAnsi12Brush = nameof(TerminalAnsi12Brush);
    public const string TerminalAnsi13Brush = nameof(TerminalAnsi13Brush);
    public const string TerminalAnsi14Brush = nameof(TerminalAnsi14Brush);
    public const string TerminalAnsi15Brush = nameof(TerminalAnsi15Brush);
public const string UiFontFamily = nameof(UiFontFamily);
    public const string UiMonoFontFamily = nameof(UiMonoFontFamily);
    public const string TerminalFontFamily = nameof(TerminalFontFamily);
    public const string FontSizeSmall = nameof(FontSizeSmall);

    public static readonly string[] AnsiBrushKeys =
    [
        nameof(TerminalAnsi0Brush), nameof(TerminalAnsi1Brush),
        nameof(TerminalAnsi2Brush), nameof(TerminalAnsi3Brush),
        nameof(TerminalAnsi4Brush), nameof(TerminalAnsi5Brush),
        nameof(TerminalAnsi6Brush), nameof(TerminalAnsi7Brush),
        nameof(TerminalAnsi8Brush), nameof(TerminalAnsi9Brush),
        nameof(TerminalAnsi10Brush), nameof(TerminalAnsi11Brush),
        nameof(TerminalAnsi12Brush), nameof(TerminalAnsi13Brush),
        nameof(TerminalAnsi14Brush), nameof(TerminalAnsi15Brush),
    ];

    public static SolidColorBrush Brush(string key) =>
        Lookup(key) as SolidColorBrush
        ?? throw new InvalidOperationException($"Theme resource '{key}' is not a SolidColorBrush.");

    public static Color Color(string key) => Brush(key).Color;

    public static FontFamily Font(string key) =>
        Lookup(key) as FontFamily
        ?? throw new InvalidOperationException($"Theme resource '{key}' is not a FontFamily.");

public static int FontSize(string key) =>
        Lookup(key) is double size
            ? (int)size
            : throw new InvalidOperationException($"Theme resource '{key}' is not a size.");

    // MergedDictionaries are not reachable through the indexer on the application
    // dictionary alone, so the theme file is searched explicitly.
    private static object Lookup(string key)
    {
        var resources = Application.Current?.Resources
            ?? throw new InvalidOperationException("No application resources to read a theme from.");

        if (resources[key] is object direct)
            return direct;

        foreach (var merged in resources.MergedDictionaries)
        {
            if (merged[key] is object found)
                return found;
        }

        throw new KeyNotFoundException($"Theme resource '{key}' is not defined in Theme.xaml.");
    }

// Resolved once at startup and written back into the dictionary, so every
    // {DynamicResource UiFontFamily} reference picks up the resolved family
    // rather than whatever the placeholder in Theme.xaml says. The chrome
    // monospace is deliberately not resolved through the Nerd Font chain -- see
    // the note in Theme.xaml.
    public static void ApplyFonts()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
            return;

        resources[UiFontFamily] = ResolveUiFamily();
        resources[UiMonoFontFamily] = new FontFamily("Cascadia Mono");
        resources[TerminalFontFamily] = ResolveTerminalFamily();
    }

    // Segoe UI Variable Text ships with Windows 11 but not every Windows 10 build,
    // so it gets the same treatment as the mono face rather than being assumed.
    private static FontFamily ResolveUiFamily()
    {
        var installed = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            if (installed.Contains(candidate))
                return new FontFamily(candidate);
        }

        return SystemFonts.MessageFontFamily;
    }

    // yazi, lazygit and helix all draw icons that only exist in a Nerd Font, so a
    // plain Cascadia Mono renders them as missing-glyph boxes. Prefer whichever
    // Nerd Font is installed, and fall back rather than fail.
    private static FontFamily ResolveTerminalFamily()
    {
        string[] preferred =
        [
            "DepartureMono Nerd Font Mono",
            "CaskaydiaCove Nerd Font Mono",
            "JetBrainsMono Nerd Font Mono",
        ];

        var installed = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .ToArray();

        foreach (var candidate in preferred)
        {
            var match = installed.FirstOrDefault(font =>
                string.Equals(font, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return new FontFamily(match);
        }

        var anyNerdMono = installed.FirstOrDefault(font =>
            font.Contains("Nerd Font Mono", StringComparison.OrdinalIgnoreCase) ||
            font.Contains("NerdFontMono", StringComparison.OrdinalIgnoreCase));

        return new FontFamily(anyNerdMono ?? "Cascadia Mono");
    }
}