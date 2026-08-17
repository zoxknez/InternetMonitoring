using System.Windows.Media;
using IEM.Core.Model;

namespace IEM.App.Controls;

/// <summary>
/// The one place colours are defined.
/// <para>
/// Shared with the HTML report on purpose: someone who watches a red band appear in this
/// window and then opens the exported report should recognise the same picture. A report
/// that looks like a different tool than the one that produced it invites the question of
/// whether it is describing the same measurements.
/// </para>
/// </summary>
public static class Palette
{
    public static readonly Brush Ok = Frozen("#2E9E5B");
    public static readonly Brush Degraded = Frozen("#E0A800");
    public static readonly Brush Outage = Frozen("#C0392B");
    public static readonly Brush Neutral = Frozen("#9A9AA2");

    public static readonly Brush Grid = Frozen("#ECEEF2");
    public static readonly Brush AxisText = Frozen("#8A90A0");
    public static readonly Brush ChartLine = Frozen("#1F6FB8");
    public static readonly Brush ChartBand = Frozen("#4A90D9", 0.22);
    public static readonly Brush Surface = Frozen("#FFFFFF");

    /// <summary>
    /// The part of the planned test that has not run yet.
    /// <para>
    /// Painted rather than left white for a reason that is about honesty as much as looks.
    /// A chart scaled to a 48-hour plan is nine tenths empty in its first hour, and blank
    /// white there reads as a broken control. Tinted, the same space reads as what it is:
    /// time this test still has to run.
    /// </para>
    /// </summary>
    public static readonly Brush Planned = Frozen("#F1F3F7");

    public static readonly Brush PlannedEdge = Frozen("#E1E5EC");

    public static Brush ForSeverity(Severity severity) => severity switch
    {
        Severity.Outage => Outage,
        Severity.Degraded => Degraded,

        // Info covers roaming, monitoring gaps and our own speed test. None of them is a
        // fault, but none of them is a healthy measurement either - showing them green
        // would claim the link was fine during a stretch nothing was measured.
        Severity.Info => Neutral,
        _ => Ok,
    };

    private static SolidColorBrush Frozen(string hex, double opacity = 1d)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}
