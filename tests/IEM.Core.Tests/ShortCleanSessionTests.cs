using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Evidence;
using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// A short session with nothing wrong in it - the case that exposed the last overclaim in
/// 2.7.0.
/// <para>
/// Two clean minutes were reported as "Veza je bila stabilna", which is a statement about the
/// connection made from a statement about two minutes. The console was corrected and the
/// window and both reports were not, because each surface was checked separately and the
/// verdict itself was never asked. It is asked here, once, for all of them.
/// </para>
/// </summary>
public sealed class ShortCleanSessionTests
{
    private static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(2)]
    [InlineData(60)]
    [InlineData(60 * 72)]
    public void A_session_without_outages_describes_the_period_rather_than_the_connection(int minutes)
    {
        var verdict = SessionVerdict.Evaluate(
            TimeSpan.FromMinutes(minutes), upstreamIncidentCount: 0, localDowntime: TimeSpan.Zero);

        Assert.Equal(VerdictKind.Stable, verdict.Kind);

        // Not a claim about the line.
        Assert.DoesNotContain("stabilna", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stabilna", verdict.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nema osnova", verdict.Detail, StringComparison.OrdinalIgnoreCase);

        // A claim about what was watched, with how long it was watched for.
        Assert.Contains("nije zabeležen", verdict.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("posmatrani period", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains(SerbianText.Duration(TimeSpan.FromMinutes(minutes)), verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it reaches the page. The verdict is shared, but a report that assembled its own
    /// headline would have hidden that - which is exactly how the console and the window came
    /// to disagree in the first place.
    /// </summary>
    [Fact]
    public void The_report_carries_the_same_wording_as_the_verdict()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iem-clean-{Guid.NewGuid():N}.html");

        try
        {
            HtmlReportBuilder.Write(path, Clean(), "abc123", chainValid: true);

            var html = File.ReadAllText(path);
            var verdict = SessionVerdict.Evaluate(TwoMinutes, 0, TimeSpan.Zero);

            Assert.Contains(verdict.Headline, html, StringComparison.Ordinal);
            Assert.DoesNotContain("Veza je bila stabilna", html, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SessionSnapshot Clean() => new(
        "S1",
        new DateTimeOffset(2026, 8, 18, 5, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 18, 5, 2, 0, TimeSpan.Zero),
        "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1",
        MonitoredTime: TwoMinutes,
        GapTime: TimeSpan.Zero,
        UpstreamDowntime: TimeSpan.Zero,
        LocalDowntime: TimeSpan.Zero,
        AvailabilityPercent: 100,
        UpstreamAvailabilityPercent: 100,
        SampleCount: 120,
        Incidents: [],
        Gaps: [],
        Latency: [],
        Traces: []);
}
