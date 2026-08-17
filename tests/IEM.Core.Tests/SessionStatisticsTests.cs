using IEM.Core.Classification;
using IEM.Core.Incidents;
using IEM.Core.Model;

namespace IEM.Core.Tests;

public sealed class SessionStatisticsTests
{
    private static SampleInstant At(double seconds) => new(
        TimeSpan.FromSeconds(seconds),
        new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero).AddSeconds(seconds));

    private static IncidentRecord Incident(double from, double to, NetworkState state) => new()
    {
        Number = 1,
        CorrelationId = Guid.NewGuid(),
        LastGood = At(from - 1),
        FirstBad = At(from),
        LastBad = At(to),
        FirstGood = At(to + 1),
        WorstState = state,
        StatesSeen = [state],
        SampleCount = 1,
    };

    [Fact]
    public void Fresh_session_reports_full_availability()
    {
        var stats = new SessionStatistics();

        Assert.Equal(100d, stats.AvailabilityPercent);
    }

    /// <summary>
    /// The correction that matters most. Counting sleep as uptime inflates availability
    /// in the customer's favour, which is exactly the flaw that would let an operator
    /// dismiss an otherwise sound report.
    /// </summary>
    [Fact]
    public void Monitoring_gaps_are_excluded_from_the_availability_denominator()
    {
        var stats = new SessionStatistics();
        stats.RecordInterval(TimeSpan.FromHours(1), isGap: false, Severity.Ok);
        stats.RecordInterval(TimeSpan.FromHours(6), isGap: true, Severity.Ok);
        stats.RecordInterval(TimeSpan.FromHours(1), isGap: false, Severity.Ok);

        Assert.Equal(TimeSpan.FromHours(2), stats.MonitoredTime);
        Assert.Equal(TimeSpan.FromHours(6), stats.GapTime);
        Assert.Equal(TimeSpan.FromHours(8), stats.WallClockTime);
        Assert.Equal(100d, stats.AvailabilityPercent);
    }

    [Fact]
    public void A_gap_is_counted_as_neither_uptime_nor_downtime()
    {
        var stats = new SessionStatistics();
        stats.RecordInterval(TimeSpan.FromHours(6), isGap: true, Severity.Ok);

        Assert.Equal(TimeSpan.Zero, stats.MonitoredTime);
        Assert.Equal(TimeSpan.Zero, stats.TotalDowntime);
    }

    [Fact]
    public void Availability_is_measured_against_monitored_time()
    {
        var stats = new SessionStatistics();
        stats.RecordInterval(TimeSpan.FromSeconds(1000), isGap: false, Severity.Ok);
        stats.RecordIncident(Incident(10, 19, NetworkState.CpeUpstreamUnreachable));

        // Reported duration for that window is 10 s, so 99% of 1000 s monitored.
        Assert.Equal(TimeSpan.FromSeconds(10), stats.TotalDowntime);
        Assert.Equal(99d, stats.AvailabilityPercent, 6);
    }

    [Fact]
    public void Local_faults_are_kept_out_of_the_operator_facing_figure()
    {
        // A dying router or a sleeping adapter is not the operator's downtime, and mixing
        // the two would put an indefensible number in a complaint.
        var stats = new SessionStatistics();
        stats.RecordInterval(TimeSpan.FromSeconds(1000), isGap: false, Severity.Ok);
        stats.RecordIncident(Incident(10, 19, NetworkState.CpeUpstreamUnreachable));
        stats.RecordIncident(Incident(100, 129, NetworkState.WifiRadioDown));

        Assert.Equal(TimeSpan.FromSeconds(10), stats.UpstreamDowntime);
        Assert.Equal(TimeSpan.FromSeconds(30), stats.LocalDowntime);
        Assert.Equal(TimeSpan.FromSeconds(40), stats.TotalDowntime);

        Assert.Equal(99d, stats.UpstreamAvailabilityPercent, 6);
        Assert.Equal(96d, stats.AvailabilityPercent, 6);
    }

    /// <summary>
    /// The headline figure counts only outages during which the customer's own network was
    /// observed working. <see cref="NetworkState.InternetDown"/> means nothing answered and
    /// the router could not be tested either, so it could equally be the customer's own
    /// equipment - and it is the first number an operator will check.
    /// </summary>
    [Fact]
    public void Only_outages_that_rule_out_local_equipment_are_counted_for_a_complaint()
    {
        var stats = new SessionStatistics();
        stats.RecordInterval(TimeSpan.FromSeconds(1000), isGap: false, Severity.Ok);
        stats.RecordIncident(Incident(10, 19, NetworkState.CpeUpstreamUnreachable));
        stats.RecordIncident(Incident(50, 59, NetworkState.AdapterDown));
        stats.RecordIncident(Incident(80, 119, NetworkState.InternetDown));

        // All three are recorded in full; only one of them is claimed.
        Assert.Equal(3, stats.Incidents.Count);
        Assert.Equal(1, stats.UpstreamIncidentCount);
        Assert.Equal(TimeSpan.FromSeconds(10), stats.LongestUpstreamOutage);
    }

    [Fact]
    public void Degraded_time_is_tracked_without_counting_as_downtime()
    {
        var stats = new SessionStatistics();
        stats.RecordInterval(TimeSpan.FromSeconds(100), isGap: false, Severity.Ok);
        stats.RecordInterval(TimeSpan.FromSeconds(50), isGap: false, Severity.Degraded);

        Assert.Equal(TimeSpan.FromSeconds(50), stats.DegradedTime);
        Assert.Equal(TimeSpan.Zero, stats.TotalDowntime);
        Assert.Equal(100d, stats.AvailabilityPercent);
    }

    [Fact]
    public void Availability_never_reports_below_zero()
    {
        var stats = new SessionStatistics();
        stats.RecordInterval(TimeSpan.FromSeconds(10), isGap: false, Severity.Ok);
        stats.RecordIncident(Incident(0, 999, NetworkState.InternetDown));

        Assert.Equal(0d, stats.AvailabilityPercent);
    }
}
