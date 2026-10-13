using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Helide.Shell;

/// <summary>
/// One glyph from the icon font, drawn as an outline that can be made heavier than the
/// font draws it.
/// </summary>
/// <remarks>
/// Segoe Fluent Icons is a hairline set. Set as text it gets bigger or it stays thin,
/// and a small thin glyph is what made the strip buttons look faint. Drawing the glyph's
/// outline and stroking it as well as filling it adds weight without changing the shape,
/// so a smaller icon can also be a bolder one.
///
/// The colour is the inherited text foreground, so it follows the button's hover colour
/// the way a text glyph would.
/// </remarks>
public sealed class GlyphIcon : FrameworkElement
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            Rebuild));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize),
        typeof(double),
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(
            10.5,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            Rebuild));

    /// <summary>How much heavier than the font's own stroke to draw, in device-independent pixels.</summary>
    public static readonly DependencyProperty WeightProperty = DependencyProperty.Register(
        nameof(Weight),
        typeof(double),
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(0.8, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(
            Brushes.White,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    private Geometry? _geometry;

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public double Weight
    {
        get => (double)GetValue(WeightProperty);
        set => SetValue(WeightProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private static void Rebuild(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((GlyphIcon)sender)._geometry = null;

    // A square a little larger than the glyph, so the heavier outline is not clipped and
    // every icon in a row takes the same room whatever its own shape.
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Ceiling(GlyphSize + Weight * 2);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (string.IsNullOrEmpty(Glyph))
            return;

        _geometry ??= Build();
        if (_geometry is null || _geometry.Bounds.IsEmpty)
            return;

        var bounds = _geometry.Bounds;
        var brush = Foreground;
        var pen = new Pen(brush, Weight)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        // Centred on the glyph's own ink rather than its em box, which carries side
        // bearings that differ from one icon to the next.
        var dx = (ActualWidth - bounds.Width) / 2 - bounds.X;
        var dy = (ActualHeight - bounds.Height) / 2 - bounds.Y;

        drawingContext.PushTransform(new TranslateTransform(dx, dy));
        drawingContext.DrawGeometry(brush, pen, _geometry);
        drawingContext.Pop();
    }

    private Geometry? Build()
    {
        var text = new FormattedText(
            Glyph,
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            GlyphSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var geometry = text.BuildGeometry(new Point(0, 0));
        geometry?.Freeze();
        return geometry;
    }
}
