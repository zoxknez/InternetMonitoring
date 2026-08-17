using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace IEM.App.Controls;

/// <param name="Minimum">Fastest response in this slice, or null if nothing answered.</param>
public readonly record struct LatencyPoint(double? Minimum, double? Average, double? Maximum);

/// <summary>
/// Latency over time, drawn as a band between the fastest and slowest response with the
/// mean through it.
/// <para>
/// A single mean line would smooth away exactly what makes a connection unusable. A link
/// averaging thirty milliseconds while spiking to two seconds every few minutes ruins
/// calls and video, and on a mean-only chart it looks perfectly healthy. The band shows
/// the spread, so the spikes are visible rather than averaged out of existence.
/// </para>
/// </summary>
public sealed class LatencyChart : FrameworkElement
{
    private const double PaddingTop = 10;
    private const double PaddingBottom = 14;
    private const double LabelWidth = 44;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<LatencyPoint>),
        typeof(LatencyChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity),
        typeof(int),
        typeof(LatencyChart),
        new FrameworkPropertyMetadata(600, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<LatencyPoint>? Points
    {
        get => (IReadOnlyList<LatencyPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        drawingContext.DrawRectangle(Palette.Surface, null, new Rect(0, 0, width, height));

        var points = Points;
        var measured = points?.Where(p => p.Average.HasValue).ToList() ?? [];

        var peak = measured.Count == 0 ? 0 : measured.Max(p => p.Maximum ?? 0);
        var scaleMax = NiceCeiling(Math.Max(peak, 20));

        var plotLeft = LabelWidth;
        var plotWidth = width - LabelWidth;

        // Scaled to the samples actually collected, not to the whole planned duration.
        //
        // Keyed to the plan, an hour into a two-day test the data occupied a sliver at the
        // left and 82% of the plot was empty tint - measured, not guessed. The reason for
        // the old behaviour was to avoid the chart rescaling on every sample, but the
        // rescale is one part in N and after the first minute nobody can see it, while
        // four fifths of a blank chart is the first thing anyone sees.
        //
        // The outage strip beside it stays keyed to the plan: showing progress through the
        // test is that control's whole job.
        var columns = Math.Max(points?.Count ?? 0, 2);
        var columnWidth = plotWidth / columns;

        DrawGrid(drawingContext, width, height, scaleMax);

        if (measured.Count < 2 || points is null)
        {
            return;
        }

        var upper = new List<Point>(points.Count);
        var lower = new List<Point>(points.Count);
        var mean = new List<Point>(points.Count);

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.Average is not { } average)
            {
                continue;
            }

            var x = plotLeft + (i * columnWidth) + (columnWidth / 2);

            upper.Add(new Point(x, Y(point.Maximum ?? average)));
            lower.Add(new Point(x, Y(point.Minimum ?? average)));
            mean.Add(new Point(x, Y(average)));
        }

        DrawBand(drawingContext, upper, lower);
        DrawLine(drawingContext, mean);

        double Y(double value) => height - PaddingBottom - ((value / scaleMax) * (height - PaddingTop - PaddingBottom));
    }

    private void DrawGrid(DrawingContext drawingContext, double width, double height, double scaleMax)
    {
        var pen = new Pen(Palette.Grid, 1);
        pen.Freeze();

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var step = 0; step <= 4; step++)
        {
            var value = scaleMax * step / 4d;
            var y = height - PaddingBottom - ((value / scaleMax) * (height - PaddingTop - PaddingBottom));

            // Snapped to whole pixels so the gridlines stay crisp instead of smearing
            // across two rows on a scaled display.
            var snapped = Math.Round(y) + 0.5;
            drawingContext.DrawLine(pen, new Point(LabelWidth, snapped), new Point(width, snapped));

            var label = new FormattedText(
                $"{value:F0} ms",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10.5,
                Palette.AxisText,
                dpi);

            drawingContext.DrawText(label, new Point(LabelWidth - label.Width - 6, snapped - (label.Height / 2)));
        }
    }

    private static void DrawBand(DrawingContext drawingContext, List<Point> upper, List<Point> lower)
    {
        if (upper.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(upper[0], isFilled: true, isClosed: true);
            context.PolyLineTo(upper.Skip(1).ToList(), isStroked: false, isSmoothJoin: false);

            // Back along the lower edge, so the two edges close into a single filled shape.
            lower.Reverse();
            context.PolyLineTo(lower, isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(Palette.ChartBand, null, geometry);
    }

    private static void DrawLine(DrawingContext drawingContext, List<Point> points)
    {
        if (points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            context.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();

        var pen = new Pen(Palette.ChartLine, 1.6);
        pen.Freeze();

        drawingContext.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    /// Rounds the axis maximum up so that every gridline lands on a round number.
    /// <para>
    /// The maxima are divisible by four because the axis is drawn in quarters. Picking 50
    /// gave gridlines at 12.5, 25 and 37.5, printed as "12 ms" and "38 ms" - an axis whose
    /// own labels are wrong by half a millisecond, which is not a good look on a document
    /// about measurement.
    /// </para>
    /// </summary>
    private static double NiceCeiling(double value)
    {
        double[] steps = [20, 40, 80, 100, 200, 400, 800, 1000, 2000, 4000];

        foreach (var step in steps)
        {
            if (value <= step)
            {
                return step;
            }
        }

        return Math.Ceiling(value / 4000d) * 4000d;
    }
}
