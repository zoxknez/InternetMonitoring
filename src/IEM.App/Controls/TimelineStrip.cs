using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IEM.Core.Model;

namespace IEM.App.Controls;

/// <param name="Severity">Worst severity seen in this slice of time.</param>
public readonly record struct TimelineSlice(Severity Severity);

/// <summary>
/// The outage strip: one column per slice of time, coloured by what happened in it.
/// <para>
/// A table of incidents tells you how many and how long. This tells you the shape -
/// whether failures cluster at the same hour each evening, arrive in bursts, or are
/// scattered at random. That pattern is often the most persuasive thing in the whole
/// report, and no list conveys it.
/// </para>
/// <para>
/// Drawn directly rather than through a charting library. The shape is a row of
/// rectangles; a dependency would add megabytes and a styling language to draw them.
/// </para>
/// </summary>
public sealed class TimelineStrip : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices),
        typeof(IReadOnlyList<TimelineSlice>),
        typeof(TimelineStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How many slices fit before the oldest scrolls off. Fixes the column width.</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity),
        typeof(int),
        typeof(TimelineStrip),
        new FrameworkPropertyMetadata(600, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TimelineSlice>? Slices
    {
        get => (IReadOnlyList<TimelineSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
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

        // Rounded, so the strip reads as one object rather than as a rectangle that ran out
        // of paint part-way across.
        var clip = new RectangleGeometry(new Rect(0, 0, width, height), 7, 7);
        clip.Freeze();

        drawingContext.PushClip(clip);

        try
        {
            // The remainder of the planned test, drawn first and painted over from the left.
            drawingContext.DrawRectangle(Palette.Planned, null, new Rect(0, 0, width, height));

            var slices = Slices;
            if (slices is null || slices.Count == 0)
            {
                DrawPlaceholder(drawingContext, width, height);
                return;
            }

            // Columns are sized to the capacity, not to the current count, so the strip fills
            // left to right as a session runs instead of stretching a handful of samples
            // across the whole width and then visibly rescaling on every new one.
            var columns = Math.Max(slices.Count, Math.Min(Capacity, slices.Count == 0 ? 1 : Capacity));
            var columnWidth = width / columns;

            for (var i = 0; i < slices.Count; i++)
            {
                var x = i * columnWidth;

                // Half a pixel of overlap: adjacent columns would otherwise show hairline
                // seams once the width falls below a device pixel.
                drawingContext.DrawRectangle(
                    Palette.ForSeverity(slices[i].Severity),
                    null,
                    new Rect(x, 0, columnWidth + 0.5, height));
            }

            DrawNowMarker(drawingContext, slices.Count * columnWidth, width, height);
        }
        finally
        {
            drawingContext.Pop();
        }

        var outline = new Pen(Palette.PlannedEdge, 1);
        outline.Freeze();

        drawingContext.DrawRoundedRectangle(
            null, outline, new Rect(0.5, 0.5, width - 1, height - 1), 7, 7);
    }

    /// <summary>
    /// The edge between what has been measured and what has not, so the boundary is a
    /// deliberate line rather than the point where the colour happens to stop.
    /// </summary>
    private static void DrawNowMarker(DrawingContext drawingContext, double x, double width, double height)
    {
        if (x <= 1 || x >= width - 1)
        {
            return;
        }

        var pen = new Pen(Palette.AxisText, 1) { DashStyle = new DashStyle([2, 2], 0) };
        pen.Freeze();

        drawingContext.DrawLine(pen, new Point(Math.Round(x) + 0.5, 0), new Point(Math.Round(x) + 0.5, height));
    }

    private void DrawPlaceholder(DrawingContext drawingContext, double width, double height)
    {
        var text = new FormattedText(
            "Nadzor još nije počeo",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            Palette.AxisText,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }
}
