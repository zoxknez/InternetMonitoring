using IEM.Core.Classification;
using IEM.Core.Incidents;
using IEM.Core.Model;

namespace IEM.Core.Tests;

public sealed class IncidentDetectorTests
{
    private static SampleInstant At(double seconds) => new(
        TimeSpan.FromSeconds(seconds),
        new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero).AddSeconds(seconds));

    private static SampleVerdict Healthy() => new(NetworkState.Ok, "ok");

    private static SampleVerdict Outage() => new(NetworkState.CpeUpstreamUnreachable, "upstream unreachable");

    private static SampleVerdict Gap() => new(NetworkState.MonitoringGap, "gap");

    [Fact]
    public void Healthy_samples_produce_no_incident()
    {
        var detector = new IncidentDetector();

        Assert.Null(detector.Observe(At(0), Healthy()));
        Assert.Null(detector.Observe(At(1), Healthy()));
        Assert.False(detector.HasOpenIncident);
    }

    [Fact]
    public void Incident_closes_on_the_first_healthy_sample()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        Assert.Null(detector.Observe(At(1), Outage()));
        Assert.True(detector.HasOpenIncident);

        var incident = detector.Observe(At(3), Healthy());

        Assert.NotNull(incident);
        Assert.False(incident.IsOpen);
        Assert.Equal(1, incident.Number);
        Assert.Equal(1, detector.ClosedCount);
    }

    /// <summary>
    /// The invariant the whole duration model rests on. Sampling is discrete, so the true
    /// length is only ever known to lie inside a window; the reported figure has to sit
    /// inside that window or the report is claiming something it did not measure.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(50)]
    public void Reported_duration_always_lies_between_the_bounds(int badSampleCount)
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());

        for (var i = 1; i <= badSampleCount; i++)
        {
            detector.Observe(At(i), Outage());
        }

        var incident = detector.Observe(At(badSampleCount + 1), Healthy())!;

        Assert.True(incident.DurationMin <= incident.DurationReported,
            $"min {incident.DurationMin} exceeded reported {incident.DurationReported}");
        Assert.True(incident.DurationReported <= incident.DurationMax,
            $"reported {incident.DurationReported} exceeded max {incident.DurationMax}");
    }

    [Fact]
    public void Bounds_match_the_sampling_window()
    {
        // Healthy at t=0, failing at t=1..3, healthy again at t=4.
        // Definitely down for 2 s; at most down for 4 s.
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.Observe(At(1), Outage());
        detector.Observe(At(2), Outage());
        detector.Observe(At(3), Outage());
        var incident = detector.Observe(At(4), Healthy())!;

        Assert.Equal(TimeSpan.FromSeconds(2), incident.DurationMin);
        Assert.Equal(TimeSpan.FromSeconds(4), incident.DurationMax);
        Assert.Equal(TimeSpan.FromSeconds(3), incident.DurationReported);
        Assert.Equal(TimeSpan.FromSeconds(1), incident.DurationUncertainty);
    }

    [Fact]
    public void Worst_state_wins_when_an_incident_passes_through_several()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.Observe(At(1), Outage());
        detector.Observe(At(2), new SampleVerdict(NetworkState.AdapterDown, "adapter down"));
        var incident = detector.Observe(At(3), Healthy())!;

        Assert.Equal(NetworkState.AdapterDown, incident.WorstState);
        Assert.Equal(2, incident.StatesSeen.Count);
    }

    // ---- P0-4: a pause in monitoring is neither uptime nor downtime -----------

    /// <summary>
    /// The headline bug. A laptop asleep from midnight to six used to produce a single
    /// six-hour outage, because the incident stayed open across the pause and closed on the
    /// first sample after it. Nobody watched those six hours, so nobody may report them.
    /// </summary>
    [Fact]
    public void A_pause_in_monitoring_closes_the_segment_where_watching_stopped()
    {
        const double SixHours = 6 * 60 * 60;

        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.Observe(At(1), Outage());
        detector.Observe(At(2), Outage());

        // Monitoring stopped after the sample at t=2 and resumed six hours later.
        var cut = detector.ObserveGap(gapStartedAt: TimeSpan.FromSeconds(2))!;

        Assert.True(cut.EndedByGap);
        Assert.False(detector.HasOpenIncident);
        Assert.True(cut.DurationMax < TimeSpan.FromSeconds(5), $"ceiling ran to {cut.DurationMax}");

        detector.Observe(At(SixHours), Outage());
        var second = detector.Observe(At(SixHours + 3), Healthy())!;

        Assert.True(second.StartedAfterGap);
        Assert.True(second.DurationMax < TimeSpan.FromSeconds(5), $"ceiling ran to {second.DurationMax}");

        var measured = cut.DurationReported + second.DurationReported;
        Assert.True(measured < TimeSpan.FromMinutes(1), $"the pause leaked in: {measured} measured");
    }

    /// <summary>
    /// The invariant the plan states outright: no measured stretch may overlap a stretch
    /// during which nothing was being measured.
    /// </summary>
    [Fact]
    public void No_measured_interval_ever_overlaps_the_pause()
    {
        var pause = new MonotonicInterval(TimeSpan.FromSeconds(2), TimeSpan.FromHours(6));

        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.Observe(At(1), Outage());
        detector.Observe(At(2), Outage());

        var cut = detector.ObserveGap(pause.Start)!;

        detector.Observe(At(pause.End.TotalSeconds), Outage());
        var second = detector.Observe(At(pause.End.TotalSeconds + 3), Healthy())!;

        Assert.False(cut.MeasuredInterval.Intersects(pause));
        Assert.False(second.MeasuredInterval.Intersects(pause));
    }

    /// <summary>
    /// The two halves are the same event, and shown as one - but only the watched time is
    /// ever added up.
    /// </summary>
    [Fact]
    public void Segments_of_one_event_share_an_identity_across_the_pause()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(1), Outage());
        var first = detector.ObserveGap(TimeSpan.FromSeconds(1))!;

        detector.Observe(At(5000), Outage());
        var second = detector.Observe(At(5002), Healthy())!;

        Assert.Equal(first.CorrelationId, second.CorrelationId);
        Assert.NotEqual(Guid.Empty, first.CorrelationId);
    }

    /// <summary>
    /// If service was back by the time monitoring resumed, the event ended somewhere inside
    /// the pause. A later, unrelated outage must not be filed as its continuation.
    /// </summary>
    [Fact]
    public void An_outage_that_recovered_during_the_pause_does_not_adopt_a_later_one()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(1), Outage());
        var first = detector.ObserveGap(TimeSpan.FromSeconds(1))!;

        detector.Observe(At(5000), Healthy());
        detector.Observe(At(5001), Outage());
        var later = detector.Observe(At(5003), Healthy())!;

        Assert.NotEqual(first.CorrelationId, later.CorrelationId);
        Assert.False(later.StartedAfterGap);
    }

    /// <summary>
    /// A healthy sample taken before a six-hour pause says nothing about the moment an
    /// outage began after it, so it must not be used as the start boundary.
    /// </summary>
    [Fact]
    public void A_healthy_sample_from_before_the_pause_is_not_a_baseline_after_it()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.ObserveGap(TimeSpan.FromSeconds(0));

        detector.Observe(At(21600), Outage());
        var incident = detector.Observe(At(21603), Healthy())!;

        Assert.Null(incident.LastGood);
        Assert.True(incident.DurationMax < TimeSpan.FromSeconds(5), $"ceiling ran to {incident.DurationMax}");
    }

    [Fact]
    public void A_pause_while_everything_is_healthy_produces_no_segment()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());

        Assert.Null(detector.ObserveGap(TimeSpan.FromSeconds(0)));
        Assert.False(detector.HasOpenIncident);
    }

    [Fact]
    public void A_gap_verdict_arriving_as_a_sample_is_not_evidence_of_anything()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());

        Assert.Null(detector.Observe(At(300), Gap()));
        Assert.False(detector.HasOpenIncident);
    }

    // ---- P0-8: traffic changing adapters mid-outage ---------------------------

    /// <summary>
    /// The most dangerous of the eight, because it produces no error anyone would notice -
    /// it produces a short, convincing, entirely false incident. The Wi-Fi dies, Windows
    /// moves traffic onto the Ethernet, probes start succeeding again, and the tool reports
    /// "recovered after two seconds" about a link that never recovered at all.
    /// </summary>
    [Fact]
    public void An_outage_that_ends_on_a_different_adapter_is_not_clean_evidence()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy(), "{WIFI}");
        detector.Observe(At(1), Outage(), "{WIFI}");
        detector.Observe(At(2), Outage(), "{WIFI}");

        // Windows fails over: service "returns", but on the other adapter.
        var incident = detector.Observe(At(3), Healthy(), "{ETHERNET}")!;

        Assert.True(incident.RouteChanged);
        Assert.Equal("{WIFI}", incident.InterfaceAtFirstBad);
        Assert.Equal("{ETHERNET}", incident.InterfaceAtFirstGood);
    }

    [Fact]
    public void An_outage_that_begins_after_a_failover_is_also_flagged()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy(), "{ETHERNET}");
        detector.Observe(At(1), Outage(), "{WIFI}");

        var incident = detector.Observe(At(3), Healthy(), "{WIFI}")!;

        Assert.True(incident.RouteChanged);
    }

    [Fact]
    public void An_outage_measured_entirely_on_one_adapter_is_clean()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy(), "{WIFI}");
        detector.Observe(At(1), Outage(), "{WIFI}");
        var incident = detector.Observe(At(3), Healthy(), "{WIFI}")!;

        Assert.False(incident.RouteChanged);
    }

    /// <summary>
    /// Unknown is not the same as changed. A machine where routing cannot be inspected
    /// records no adapter at all, and inventing a route change from that absence would put
    /// a warning on every incident it ever produced.
    /// </summary>
    [Fact]
    public void An_unknown_adapter_is_not_reported_as_a_route_change()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.Observe(At(1), Outage());
        var incident = detector.Observe(At(3), Healthy())!;

        Assert.False(incident.RouteChanged);
    }

    // ---- P0-7: an outage in progress survives the process dying ---------------

    /// <summary>
    /// The failing samples were written to the chain as they happened, but the segment that
    /// summarises them is only written when it closes. A crash mid-outage therefore left the
    /// evidence in the log and nothing at all in the statistics.
    /// </summary>
    [Fact]
    public void An_outage_reconstructed_after_a_crash_is_closed_and_counted()
    {
        var detector = new IncidentDetector(alreadyClosed: 4);

        detector.RestoreOpenIncident(
            lastGood: At(100),
            firstBad: At(101),
            lastBad: At(112),
            statesSeen: [NetworkState.CpeUpstreamUnreachable],
            sampleCount: 12,
            technicalDetail: "upstream unreachable");

        Assert.True(detector.HasOpenIncident);

        var restored = detector.ObserveGap(gapStartedAt: TimeSpan.FromSeconds(112))!;

        Assert.Equal(5, restored.Number);
        Assert.Equal(TimeSpan.FromSeconds(11), restored.DurationMin);
        Assert.Equal(12, restored.SampleCount);
        Assert.True(restored.EndedByGap);
    }

    [Fact]
    public void An_incident_still_running_at_shutdown_is_marked_open()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.Observe(At(1), Outage());

        var incident = detector.CloseOpenIncident()!;

        Assert.True(incident.IsOpen);
        Assert.Null(incident.FirstGood);
    }

    [Fact]
    public void Closing_when_nothing_is_open_returns_nothing()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());

        Assert.Null(detector.CloseOpenIncident());
    }

    [Fact]
    public void An_incident_starting_on_the_very_first_sample_has_no_prior_baseline()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Outage());
        var incident = detector.Observe(At(2), Healthy())!;

        Assert.Null(incident.LastGood);
        Assert.True(incident.DurationMin <= incident.DurationReported);
        Assert.True(incident.DurationReported <= incident.DurationMax);
    }

    [Fact]
    public void Consecutive_incidents_are_numbered_in_order()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.Observe(At(1), Outage());
        var first = detector.Observe(At(2), Healthy())!;
        detector.Observe(At(3), Outage());
        var second = detector.Observe(At(4), Healthy())!;

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }
}
