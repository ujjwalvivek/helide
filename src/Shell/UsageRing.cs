using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Helide.Shell;

/// <summary>
/// A small ring that fills clockwise from twelve o'clock: how much of a usage limit is gone.
/// </summary>
/// <remarks>
/// The agent header has room for a mark, not a number, so the share is the ring and the
/// figures are in the flyout it opens. Drawn rather than taken from the icon font, which
/// has no glyph for an arbitrary fraction.
/// </remarks>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much of the ring is filled, 0 to 100.</summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Stroke
    {
        get => (double)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Width is > 0 ? Width : 12, Height is > 0 ? Height : 12);

    protected override void OnRender(DrawingContext drawing)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Stroke) / 2;

        drawing.DrawEllipse(null, new Pen(TrackBrush, Stroke), center, radius, radius);

        var share = Math.Clamp(Percent, 0, 100) / 100;
        if (share <= 0)
            return;

        var pen = new Pen(RingBrush, Stroke) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };

        // A full ring as an arc would start and end on the same point and draw nothing.
        if (share >= 0.999)
        {
            drawing.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = share * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, share > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }
}
