using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tommy.App.Controls;

/// <summary>
/// A small circular progress ring: a full track circle with a foreground arc that
/// sweeps clockwise from the top in proportion to <see cref="Percentage"/> (0–100).
/// Drawn directly so there's no fragile dash-offset math.
/// </summary>
public sealed class RingProgress : Control
{
    public static readonly StyledProperty<double> PercentageProperty =
        AvaloniaProperty.Register<RingProgress, double>(nameof(Percentage));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<RingProgress, double>(nameof(StrokeThickness), 2.5);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<RingProgress, IBrush?>(nameof(Foreground), Brushes.SteelBlue);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingProgress, IBrush?>(nameof(TrackBrush), Brushes.Gainsboro);

    static RingProgress()
    {
        AffectsRender<RingProgress>(PercentageProperty, StrokeThicknessProperty, ForegroundProperty, TrackBrushProperty);
    }

    public double Percentage
    {
        get => GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var thickness = StrokeThickness;
        var radius = (Math.Min(Bounds.Width, Bounds.Height) - thickness) / 2;
        if (radius <= 0) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (TrackBrush is { } track)
            context.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);

        var pct = Math.Clamp(Percentage, 0, 100);
        if (pct <= 0 || Foreground is not { } fg) return;

        var pen = new Pen(fg, thickness) { LineCap = PenLineCap.Round };

        if (pct >= 100)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var sweep = pct / 100.0 * 360.0;
        var start = PointOnCircle(center, radius, -90);
        var end = PointOnCircle(center, radius, -90 + sweep);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, isLargeArc: sweep > 180, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
