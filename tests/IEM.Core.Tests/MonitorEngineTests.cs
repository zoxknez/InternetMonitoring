using IEM.Core;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Core.Scheduling;

namespace IEM.Core.Tests;

/// <summary>
/// Feeds the engine a scripted sequence of cycles, advancing a hand-driven clock by a
/// fixed step per sample.
/// <para>
/// Because the clock jumps a full second per sample while the cadence intervals are set
/// to a millisecond, the engine always finds its budget already spent and never actually
/// waits - so a scenario spanning minutes of simulated time runs instantly and produces
/// the same numbers every time.
/// </para>
/// </summary>
internal sealed class ScriptedProbeSource(ManualClock clock, IReadOnlyList<ProbeCycle> script, TimeSpan step)
    : IProbeSource
{
    private int _index;

    public int Consumed => _index;

    public Task<ProbeCycle> SampleAsync(long sequence, TimeSpan budget, CancellationToken cancellationToken)
    {
        clock.Advance(step);

        // Hold the final state once the script runs out, so the engine can be stopped by
        // its duration rather than by an exception.
        var template = script[Math.Min(_index, script.Count - 1)];
        _index++;

        return Task.FromResult(template with
        {
            Sequence = sequence,
            WallUtc = clock.UtcNow,
            MonotonicTicks = clock.MonotonicTicks,
        });
    }
}

public sealed class MonitorEngineTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    private static MonitorOptions FastOptions() => MonitorOptions.Default with
    {
        Cadence = new CadenceOptions
        {
            StableInterval = TimeSpan.FromMilliseconds(1),
            SuspectInterval = TimeSpan.FromMilliseconds(1),
            BurstInterval = TimeSpan.FromMilliseconds(1),
            IncidentInterval = TimeSpan.FromMilliseconds(1),
            RecoveryInterval = TimeSpan.FromMilliseconds(1),
            RecoveryHold = TimeSpan.FromSeconds(2),
        },
    };

    private static async Task<(MonitorEngine Engine, List<IncidentRecord> Incidents, List<MonitorSample> Samples)>
        RunAsync(IReadOnlyList<ProbeCycle> script, int sampleCount)
    {
        var clock = new ManualClock();
        var source = new ScriptedProbeSource(clock, script, Step);
        var engine = new MonitorEngine(source, FastOptions(), clock);

        var incidents = new List<IncidentRecord>();
        var samples = new List<MonitorSample>();
        engine.IncidentClosed += incidents.Add;
        engine.SampleRecorded += samples.Add;

        await engine.RunAsync(Step * sampleCount, CancellationToken.None);

        return (engine, incidents, samples);
    }

    // ---- Confidence, which used to be computed nowhere at all -----------------

    /// <summary>
    /// The scorer existed and was never called. A report that never states how far its
    /// evidence goes leaves the reader to assume it goes all the way - which is the one
    /// assumption this tool must not invite.
    /// </summary>
    [Fact]
    public async Task A_closed_outage_carries_how_far_its_evidence_goes()
    {
        var healthy = CycleBuilder.Wired().Build();
        var outage = CycleBuilder.Wired().AllExternalFail().Build();

        var (_, incidents, _) = await RunAsync([healthy, outage, outage, outage, healthy], sampleCount: 8);

        var incident = Assert.Single(incidents);

        Assert.NotNull(incident.Confidence);
        Assert.NotEmpty(incident.Confidence.Supporting);

        // The router answered throughout while nothing beyond it did: the central fact of
        // this reading, and it has to appear as a supporting signal rather than be assumed.
        Assert.Contains(incident.Confidence.Supporting, e => e.Key == "cpe.gatewayReachable");
        Assert.Contains(incident.Confidence.Supporting, e => e.Key == "upstream.icmpFailed");
    }

    /// <summary>
    /// Evidence has to be gathered while the outage is running. Signals of the form "did
    /// this hold throughout" cannot be reconstructed afterwards from a duration and a name.
    /// </summary>
    [Fact]
    public async Task Evidence_reflects_what_happened_during_the_outage_not_after_it()
    {
        var healthy = CycleBuilder.Wired().Build();

        // The adapter drops part-way through, which rules out the upstream reading entirely.
        var dropped = CycleBuilder.Wired().AdapterDown().AllExternalFail().Build();
        var outage = CycleBuilder.Wired().AllExternalFail().Build();

        var (_, incidents, _) = await RunAsync([healthy, outage, dropped, outage, healthy], sampleCount: 8);

        var incident = Assert.Single(incidents);

        // The drop was seen while the outage ran, so the signal was actually checked rather
        // than left unavailable - which is what the test is about.
        Assert.DoesNotContain(incident.Confidence!.Missing, e => e.Key == "link.stayedUp");

        // The adapter dropping outranks the upstream reading, so the incident is scored as
        // AdapterDown - and for that conclusion the link not staying up is the evidence for
        // it, not against it. Under the 1.0 confidence model this read as contradicting,
        // which had every conclusion penalised by the fact that established it.
        Assert.Equal(NetworkState.AdapterDown, incident.WorstState);
        Assert.Contains(incident.Confidence.Supporting, e => e.Key == "link.stayedUp");
    }

    [Fact]
    public async Task Each_outage_is_scored_on_its_own_evidence()
    {
        var healthy = CycleBuilder.Wired().Build();
        var outage = CycleBuilder.Wired().AllExternalFail().Build();

        var (_, incidents, _) = await RunAsync(
            [healthy, outage, healthy, healthy, outage, healthy], sampleCount: 10);

        Assert.Equal(2, incidents.Count);

        // The second must not inherit the first's evidence, which is what a collector that
        // was never reset would produce.
        Assert.All(incidents, i => Assert.NotNull(i.Confidence));
        Assert.All(incidents, i => Assert.NotEmpty(i.Confidence!.Supporting));
    }

    [Fact]
    public async Task A_healthy_session_records_no_incidents_and_full_availability()
    {
        var healthy = CycleBuilder.Wired().Build();

        var (engine, incidents, samples) = await RunAsync([healthy], sampleCount: 10);

        Assert.Empty(incidents);
        Assert.Equal(100d, engine.Statistics.AvailabilityPercent);
        Assert.All(samples, s => Assert.Equal(NetworkState.Ok, s.Verdict.State));
    }

    [Fact]
    public async Task An_outage_is_detected_bounded_and_attributed_upstream()
    {
        // Healthy, then three seconds where the router answers but nothing beyond it does,
        // then healthy again.
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();

        var (engine, incidents, _) = await RunAsync(
            [healthy, down, down, down, healthy, healthy, healthy, healthy], sampleCount: 8);

        var incident = Assert.Single(incidents);

        Assert.Equal(NetworkState.CpeUpstreamUnreachable, incident.WorstState);
        Assert.True(incident.IsUpstream);
        Assert.Equal(TimeSpan.FromSeconds(2), incident.DurationMin);
        Assert.Equal(TimeSpan.FromSeconds(4), incident.DurationMax);
        Assert.Equal(TimeSpan.FromSeconds(3), incident.DurationReported);
        Assert.Equal(1, engine.Statistics.UpstreamIncidentCount);
    }

    [Fact]
    public async Task Sampling_escalates_during_an_outage_and_stands_down_afterwards()
    {
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();

        var (_, _, samples) = await RunAsync(
            [healthy, down, down, healthy, healthy, healthy, healthy, healthy], sampleCount: 8);

        Assert.Equal(CadencePhase.Stable, samples[0].Phase);
        Assert.Equal(CadencePhase.Incident, samples[1].Phase);
        Assert.Equal(CadencePhase.Recovery, samples[3].Phase);
        Assert.Equal(CadencePhase.Stable, samples[^1].Phase);
    }

    [Fact]
    public async Task A_router_wifi_failure_is_not_counted_against_the_operator()
    {
        // The fault this whole tool exists to tell apart: the router stops broadcasting
        // while its uplink is perfectly healthy. Filing that as an operator outage would
        // be a complaint the customer loses.
        var healthy = CycleBuilder.Wireless().Build();
        var radioDown = CycleBuilder.Wireless().AdapterDown().SsidNotVisible().AllExternalFail().Build();

        var (engine, incidents, _) = await RunAsync(
            [healthy, radioDown, radioDown, healthy, healthy], sampleCount: 5);

        var incident = Assert.Single(incidents);

        Assert.Equal(NetworkState.WifiRadioDown, incident.WorstState);
        Assert.False(incident.IsUpstream);
        Assert.Equal(0, engine.Statistics.UpstreamIncidentCount);
        Assert.Equal(TimeSpan.Zero, engine.Statistics.UpstreamDowntime);
        Assert.Equal(100d, engine.Statistics.UpstreamAvailabilityPercent);
    }

    [Fact]
    public async Task Filtered_ping_never_becomes_an_incident()
    {
        var filtered = CycleBuilder.Wired().ExternalIcmpAllFail().Build();

        var (engine, incidents, samples) = await RunAsync([filtered], sampleCount: 6);

        Assert.Empty(incidents);
        Assert.Equal(TimeSpan.Zero, engine.Statistics.TotalDowntime);
        Assert.All(samples, s => Assert.Equal(NetworkState.IcmpFiltered, s.Verdict.State));
    }

    [Fact]
    public async Task Operator_dns_failure_is_degradation_rather_than_downtime()
    {
        var brokenDns = CycleBuilder.Wired().DnsIspFailsOnly().Build();

        var (engine, incidents, _) = await RunAsync([brokenDns], sampleCount: 6);

        Assert.Empty(incidents);
        Assert.Equal(TimeSpan.Zero, engine.Statistics.TotalDowntime);
        Assert.True(engine.Statistics.DegradedTime > TimeSpan.Zero);
    }

    [Fact]
    public async Task An_outage_still_running_at_shutdown_is_recorded()
    {
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();

        var (_, incidents, _) = await RunAsync([healthy, down], sampleCount: 5);

        var incident = Assert.Single(incidents);
        Assert.True(incident.IsOpen);
    }

    [Fact]
    public async Task Downtime_totals_agree_with_the_incident_table()
    {
        // If the headline percentage and the incident list could disagree, an operator
        // would only have to point at the discrepancy to dismiss the whole report.
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();

        var (engine, incidents, _) = await RunAsync(
            [healthy, down, down, healthy, healthy, down, down, healthy, healthy], sampleCount: 9);

        var summed = incidents.Aggregate(TimeSpan.Zero, (total, i) => total + i.DurationReported);

        Assert.Equal(2, incidents.Count);
        Assert.Equal(summed, engine.Statistics.TotalDowntime);
    }
}
