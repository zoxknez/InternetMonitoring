using IEM.Core.Probes;
using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// The gating is the point of the measurement, not a refinement of it.
/// <para>
/// A speed test run while the customer is in a video call measures whatever bandwidth was
/// left over. A figure like that in a complaint is worse than no figure: the operator only
/// has to ask what else was running, and the rest of the report goes with it. So this
/// refuses rather than measures, and says why - a refusal someone can act on beats a number
/// they cannot defend.
/// </para>
/// </summary>
public sealed class ThroughputGatingTests
{
    private static ThroughputOptions Options() => ThroughputOptions.Default with
    {
        RequiredIdlePeriod = TimeSpan.FromSeconds(10),
    };

    private static ThroughputMeasurement Build(ManualClock clock) =>
        new(new HttpClient(), new LinkActivityMonitor(clock), Options(), clock);

    private static LinkActivity Quiet() => new(1_000, IsIdle: true);

    private static LinkActivity Busy() => new(5_000_000, IsIdle: false);

    // ---- How long the link has to have been quiet -----------------------------

    /// <summary>
    /// A single quiet instant means nothing - a download pauses between chunks. Requiring a
    /// stretch of quiet is what separates "nothing is running" from "nothing is running at
    /// this exact millisecond".
    /// </summary>
    [Fact]
    public void One_quiet_reading_is_not_enough()
    {
        var clock = new ManualClock();
        var measurement = Build(clock);

        measurement.Observe(Quiet());

        Assert.NotNull(measurement.IdleFor);
        Assert.False(measurement.ReadyToMeasure);
    }

    [Fact]
    public void The_link_becomes_ready_once_it_has_been_quiet_long_enough()
    {
        var clock = new ManualClock();
        var measurement = Build(clock);

        measurement.Observe(Quiet());
        clock.Advance(TimeSpan.FromSeconds(11));
        measurement.Observe(Quiet());

        Assert.True(measurement.ReadyToMeasure);
    }

    /// <summary>
    /// Any traffic at all restarts the clock. Averaging over a window would let a steady
    /// trickle pass as quiet, which is exactly the case this exists to catch.
    /// </summary>
    [Fact]
    public void A_burst_of_traffic_restarts_the_wait()
    {
        var clock = new ManualClock();
        var measurement = Build(clock);

        measurement.Observe(Quiet());
        clock.Advance(TimeSpan.FromSeconds(9));

        measurement.Observe(Busy());

        Assert.Null(measurement.IdleFor);
        Assert.False(measurement.ReadyToMeasure);

        // And the wait starts over rather than resuming where it left off.
        measurement.Observe(Quiet());
        clock.Advance(TimeSpan.FromSeconds(9));
        measurement.Observe(Quiet());

        Assert.False(measurement.ReadyToMeasure);
    }

    [Fact]
    public async Task A_link_quiet_but_not_for_long_enough_is_refused_with_that_reason()
    {
        var clock = new ManualClock();
        var measurement = Build(clock);

        measurement.Observe(Quiet());

        var result = await measurement.MeasureAsync(connectionHealthy: true, CancellationToken.None);

        Assert.Equal(ThroughputRefusal.NotIdleLongEnough, result.Refusal);
    }

    [Fact]
    public void A_link_that_has_never_been_quiet_is_not_ready()
    {
        var measurement = new ThroughputMeasurement(
            new HttpClient(), new LinkActivityMonitor(new ManualClock()), Options(), new ManualClock());

        Assert.Null(measurement.IdleFor);
        Assert.False(measurement.ReadyToMeasure);
    }

    [Fact]
    public async Task Measuring_a_busy_link_is_refused_rather_than_attempted()
    {
        var measurement = new ThroughputMeasurement(
            new HttpClient(), new LinkActivityMonitor(new ManualClock()), Options(), new ManualClock());

        var result = await measurement.MeasureAsync(connectionHealthy: true, CancellationToken.None);

        Assert.False(result.Ran);
        Assert.Equal(ThroughputRefusal.LinkBusy, result.Refusal);
        Assert.Equal(0, result.BytesTransferred);
    }

    /// <summary>
    /// Measuring a connection that is failing would describe the fault, not the speed - and
    /// a low figure taken during an outage is the most misleading number this tool could
    /// possibly produce.
    /// </summary>
    [Fact]
    public async Task Measuring_a_failing_connection_is_refused()
    {
        var measurement = new ThroughputMeasurement(
            new HttpClient(), new LinkActivityMonitor(new ManualClock()), Options(), new ManualClock());

        var result = await measurement.MeasureAsync(connectionHealthy: false, CancellationToken.None);

        Assert.Equal(ThroughputRefusal.ConnectionUnhealthy, result.Refusal);
    }

    [Fact]
    public void A_refusal_carries_no_speed_figure_at_all()
    {
        foreach (var reason in Enum.GetValues<ThroughputRefusal>().Where(r => r != ThroughputRefusal.None))
        {
            var refused = ThroughputResult.Refused(reason);

            Assert.False(refused.Ran);
            Assert.Equal(0, refused.DownloadMbps);
            Assert.Equal(TimeSpan.Zero, refused.Duration);
        }
    }
}
