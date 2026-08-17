using IEM.Core.Classification;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// The ten-point gate, in one place.
/// <para>
/// Each of these stands for a way the tool could produce a confident, plausible, wrong
/// answer - and every one of them was real code at some point. The detailed tests live
/// beside the classes they cover; these are the single-sentence statements of what must
/// remain true, so a later change that quietly undoes one of them fails here with the
/// reason attached rather than somewhere that reads as an unrelated assertion.
/// </para>
/// <para>
/// The guiding rule behind all ten: better to report fewer outages with strong evidence than
/// one more that an operator can dismiss, because a single figure they can pick apart
/// discredits the ones that were sound.
/// </para>
/// </summary>
public sealed class EvidenceSafetyGateTests
{
    private static SampleInstant At(double seconds) => new(
        TimeSpan.FromSeconds(seconds),
        new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero).AddSeconds(seconds));

    private static SampleVerdict Outage() => new(NetworkState.CpeUpstreamUnreachable, "upstream unreachable");

    private static SampleVerdict Healthy() => new(NetworkState.Ok, "ok");

    /// <summary>1. A single slow probe never sets the pace for the whole engine.</summary>
    [Fact]
    public void Reading_the_observations_does_not_wait_on_any_probe()
    {
        var store = new ObservationStore(new ManualClock());

        // Nothing has answered yet. The tick still returns, with nothing rather than a wait.
        Assert.Empty(store.Snapshot());
    }

    /// <summary>2. A cached success cannot conceal an outage that started after it.</summary>
    [Fact]
    public void A_success_from_before_the_trouble_cannot_prove_the_line_works_now()
    {
        var cycle = CycleBuilder.Wired()
            .MeasuredBeforeTrouble().TlsSucceeds().HttpSucceeds().DnsAllSucceed()
            .MeasuredNow().ExternalIcmpAllFail().ExternalTcpAllFail().GatewayReachable()
            .Build();

        Assert.False(cycle.AnyExternalReachability);
        Assert.True(new StateClassifier().Classify(cycle).IsOutage);
    }

    /// <summary>3. Every relevant probe records which adapter and route it left by.</summary>
    [Fact]
    public void Measurements_carry_the_path_they_took()
    {
        var cycle = CycleBuilder.Wired().Build();

        Assert.NotNull(cycle.AgreedInterfaceId);
        Assert.All(
            cycle.Results.Where(r => r.WasAttempted),
            r => Assert.True(r.Path.Resolved, $"{r.Kind} {r.Target} recorded no path"));
    }

    /// <summary>4. Time nobody was watching never enters a measured duration.</summary>
    [Fact]
    public void A_pause_in_monitoring_never_becomes_reported_downtime()
    {
        var pause = new MonotonicInterval(TimeSpan.FromSeconds(2), TimeSpan.FromHours(6));

        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy());
        detector.Observe(At(1), Outage());
        detector.Observe(At(2), Outage());

        var cut = detector.ObserveGap(pause.Start)!;

        detector.Observe(At(pause.End.TotalSeconds), Outage());
        var resumed = detector.Observe(At(pause.End.TotalSeconds + 2), Healthy())!;

        Assert.False(cut.MeasuredInterval.Intersects(pause));
        Assert.False(resumed.MeasuredInterval.Intersects(pause));
        Assert.Equal(cut.CorrelationId, resumed.CorrelationId);
    }

    /// <summary>5. An outage in progress survives the process being killed.</summary>
    [Fact]
    public void An_outage_running_at_the_moment_of_a_crash_can_be_restored()
    {
        var detector = new IncidentDetector();

        detector.RestoreOpenIncident(
            lastGood: At(100),
            firstBad: At(101),
            lastBad: At(112),
            statesSeen: [NetworkState.CpeUpstreamUnreachable],
            sampleCount: 12,
            technicalDetail: "upstream unreachable");

        var restored = detector.ObserveGap(TimeSpan.FromSeconds(112))!;

        Assert.Equal(TimeSpan.FromSeconds(11), restored.DurationMin);
        Assert.True(restored.EndedByGap);
    }

    /// <summary>6. Everything in a report can be rebuilt from the raw chain alone.</summary>
    [Fact]
    public void Every_recorded_fact_round_trips_through_the_chain()
    {
        var payload = new GapPayload(
            new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(5), GapCause.Sleep);

        var read = PayloadReader.Gap(EvidenceRoundTrip.Through(payload));

        Assert.NotNull(read);
        Assert.Equal(payload.Duration, read.Duration);
        Assert.Equal(payload.Cause, read.Cause);
    }

    /// <summary>
    /// A pause is described by what actually caused it.
    /// <para>
    /// Every cause except a reboot and a clock change used to be folded into "probably the
    /// computer sleeping" - so a service restart, which the engine knows about precisely,
    /// reached the operator as a guess about something that did not happen. A statement the
    /// operator can disprove from their own records takes the rest of the evidence with it.
    /// </para>
    /// </summary>
    [Fact]
    public void A_pause_is_described_by_what_actually_caused_it()
    {
        Assert.Equal("nadzor nije bio pokrenut", GapCause.MonitorNotRunning.Label());
        Assert.Equal("računar je bio u stanju spavanja", GapCause.Sleep.Label());
        Assert.Equal("restart računara", GapCause.Reboot.Label());
        Assert.Equal("pomeranje sistemskog sata", GapCause.ClockAdjustment.Label());

        // Genuinely unknown says so, rather than guessing at sleep.
        Assert.Equal("uzrok nije utvrđen", GapCause.Unknown.Label());

        Assert.DoesNotContain("spavanje", GapCause.MonitorNotRunning.Label(), StringComparison.Ordinal);
        Assert.DoesNotContain("spavanje", GapCause.Unknown.Label(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cause_reads_the_same_from_the_raw_log_as_from_the_engine()
    {
        foreach (var cause in Enum.GetValues<GapCause>())
        {
            Assert.Equal(cause.Label(), SerbianText.GapCauseLabel(cause.ToString()));
        }
    }

    /// <summary>7. Without the raw chain there is no report at all.</summary>
    [Fact]
    public void A_missing_chain_does_not_verify()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"iem-absent-{Guid.NewGuid():N}.jsonl");

        Assert.False(ChainVerifier.Verify(absent).Valid);
    }

    /// <summary>8. No state, on its own, accuses the operator prematurely.</summary>
    [Fact]
    public void No_state_claims_the_operator_is_at_fault()
    {
        foreach (var state in Enum.GetValues<NetworkState>())
        {
            var explanation = state.DomainOf(LinkMedium.Ethernet).Explain();

            Assert.DoesNotContain("krivic", explanation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("potvrđeno kod operatera", explanation, StringComparison.OrdinalIgnoreCase);
        }

        // The strongest available reading isolates the problem; it does not assign blame.
        Assert.Equal(FaultDomain.UpstreamPath, NetworkState.CpeUpstreamUnreachable.DomainOf(LinkMedium.Ethernet));
        Assert.Equal(FaultDomain.Unknown, NetworkState.InternetDown.DomainOf(LinkMedium.Ethernet));
    }

    /// <summary>9. Confidence requires both support and coverage.</summary>
    [Fact]
    public void A_sliver_of_the_picture_cannot_produce_a_strong_conclusion()
    {
        var score = ConfidenceScorer.Score(NetworkState.CpeUpstreamUnreachable, new IncidentEvidence
        {
            LinkStayedUp = true,
            GatewayRemainedReachable = true,
        });

        Assert.Equal(100, score.Support);
        Assert.NotEqual(ConfidenceBand.VeryHigh, score.Band);
    }

    /// <summary>10. Traffic changing adapters mid-outage is never a clean recovery.</summary>
    [Fact]
    public void An_outage_spanning_two_adapters_is_flagged_rather_than_reported_as_recovered()
    {
        var detector = new IncidentDetector();
        detector.Observe(At(0), Healthy(), "{WIFI}");
        detector.Observe(At(1), Outage(), "{WIFI}");

        var incident = detector.Observe(At(3), Healthy(), "{ETHERNET}")!;

        Assert.True(incident.RouteChanged);
    }
}
