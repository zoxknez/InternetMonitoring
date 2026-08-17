using IEM.Core.Classification;
using IEM.Core.Model;

namespace IEM.Core.Tests;

/// <summary>
/// The connection being slow and the computer using all of it look identical from here, and
/// only one of the two is the operator's fault.
/// <para>
/// Measured live 17.08.: seventy seconds of an unrelated 25 MB/s download produced eighteen
/// short "outages", several of them attributed upstream. The confidence model always had a
/// signal for this - "the line was not busy with your own traffic" - but nothing ever
/// measured it, so every incident was recorded as having ruled it out. These tests are about
/// that signal telling the truth: ruled out, not ruled out, or never checked.
/// </para>
/// </summary>
public sealed class LocalTrafficTests
{
    private static IncidentEvidence Collect(params ProbeCycle[] cycles)
    {
        var collector = new IncidentEvidenceCollector();
        collector.Begin();

        foreach (var cycle in cycles)
        {
            collector.Observe(cycle, ClassificationContext.Empty, clockAnomalous: false);
        }

        return collector.Build();
    }

    // ---- What counts as busy ------------------------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(4_000, false)]
    [InlineData(ProbeCycle.HeavyLocalTrafficBytesPerSecond - 1, false)]
    [InlineData(ProbeCycle.HeavyLocalTrafficBytesPerSecond, true)]
    [InlineData(25_000_000, true)]
    public void Heavy_is_decided_by_one_stated_threshold(long bytesPerSecond, bool expected)
    {
        var cycle = CycleBuilder.Wired().WithLocalTraffic(bytesPerSecond).Build();

        Assert.Equal(expected, cycle.LocalTrafficHeavy);
    }

    /// <summary>
    /// Without a reading it is not "quiet" - it is unknown, and a computed flag that answered
    /// false would be the same mistake as the signal it feeds used to make.
    /// </summary>
    [Fact]
    public void Without_a_reading_nothing_is_claimed_about_our_own_traffic()
    {
        var cycle = CycleBuilder.Wired().WithoutLocalTrafficReading().Build();

        Assert.Null(cycle.LocalTrafficBytesPerSecond);
        Assert.False(cycle.LocalTrafficHeavy);
    }

    // ---- What reaches the confidence score ----------------------------------------

    [Fact]
    public void A_quiet_line_throughout_rules_our_own_traffic_out()
    {
        var evidence = Collect(
            CycleBuilder.Wired().AllExternalFail().Build(1),
            CycleBuilder.Wired().AllExternalFail().Build(2));

        Assert.True(evidence.NoLocalSaturation);
    }

    /// <summary>
    /// One busy sample is enough to stop the claim: the whole point of the signal is whether
    /// our own traffic can be excluded as an explanation, and once it cannot, it cannot.
    /// </summary>
    [Fact]
    public void One_busy_sample_stops_it_being_ruled_out()
    {
        var evidence = Collect(
            CycleBuilder.Wired().AllExternalFail().Build(1),
            CycleBuilder.Wired().AllExternalFail().WithLocalTraffic().Build(2),
            CycleBuilder.Wired().AllExternalFail().Build(3));

        Assert.False(evidence.NoLocalSaturation);
    }

    /// <summary>
    /// The failure this fixes: with nothing measured, the signal used to narrow to true on
    /// every cycle and every incident was recorded as having excluded the machine's own
    /// traffic - a check nobody had ever made.
    /// </summary>
    [Fact]
    public void With_no_readings_at_all_the_signal_stays_unanswered()
    {
        var evidence = Collect(
            CycleBuilder.Wired().AllExternalFail().WithoutLocalTrafficReading().Build(1),
            CycleBuilder.Wired().AllExternalFail().WithoutLocalTrafficReading().Build(2));

        Assert.Null(evidence.NoLocalSaturation);
    }

    [Fact]
    public void Our_own_speed_measurement_is_known_saturation()
    {
        var evidence = Collect(CycleBuilder.Wired().AllExternalFail().DuringSelfTest().Build());

        Assert.False(evidence.NoLocalSaturation);
    }

    /// <summary>
    /// And it costs the case something, which is the point: an incident that cannot exclude
    /// the customer's own download is weaker evidence against the operator than one that can,
    /// and the report has to show that rather than smooth it over.
    /// </summary>
    [Fact]
    public void An_incident_during_a_download_scores_lower_than_the_same_one_on_a_quiet_line()
    {
        var quiet = ConfidenceScorer.Score(
            NetworkState.CpeUpstreamUnreachable,
            Collect(CycleBuilder.Wired().AllExternalFail().Build()));

        var busy = ConfidenceScorer.Score(
            NetworkState.CpeUpstreamUnreachable,
            Collect(CycleBuilder.Wired().AllExternalFail().WithLocalTraffic().Build()));

        Assert.True(busy.Support < quiet.Support);
    }

    /// <summary>
    /// Named in Serbian wherever it shows up, so the reader of a report learns what was not
    /// excluded rather than reading a key out of the source code.
    /// </summary>
    [Fact]
    public void The_signal_has_a_name_a_reader_understands()
    {
        Assert.Equal(
            "Veza nije bila zauzeta vašim saobraćajem",
            IEM.Core.Presentation.SerbianText.EvidenceLabel("device.noSaturation"));
    }

    /// <summary>
    /// A measurement in progress is not a fault and not availability either: the period is
    /// excluded from assessment, and nobody is blamed for the load we caused ourselves.
    /// </summary>
    [Fact]
    public void A_sample_taken_during_our_measurement_blames_nobody()
    {
        var verdict = new StateClassifier().Classify(
            CycleBuilder.Wired().AllExternalFail().DuringSelfTest().Build());

        Assert.Equal(NetworkState.SelfTest, verdict.State);
        Assert.Equal(Severity.Info, verdict.State.SeverityOf());
        Assert.Equal(FaultAttribution.None, verdict.State.AttributionOf());
    }

    // ---- What lands in the raw chain ----------------------------------------------

    /// <summary>
    /// On every sample rather than summarised per incident: whoever checks the package later
    /// has to be able to see the figure for the exact second in question rather than take a
    /// verdict's word for it.
    /// </summary>
    [Fact]
    public void The_rate_is_written_into_the_chain_for_each_sample()
    {
        var payload = IEM.Storage.Evidence.SamplePayload.From(
            Sample(CycleBuilder.Wired().AllExternalFail().WithLocalTraffic(25_000_000).Build()));

        var json = EvidenceRoundTrip.Through(payload);

        Assert.Equal(25_000_000, json.GetProperty("localBps").GetInt64());

        var read = IEM.Storage.Evidence.PayloadReader.Sample(json);

        Assert.NotNull(read);
        Assert.Equal(25_000_000, read.LocalTrafficBytesPerSecond);
    }

    /// <summary>
    /// Absent means "not known", and it has to stay distinguishable from a quiet line in a
    /// record somebody may read years later - including one written before the field existed.
    /// </summary>
    [Fact]
    public void An_unread_counter_leaves_the_field_out_rather_than_writing_zero()
    {
        var payload = IEM.Storage.Evidence.SamplePayload.From(
            Sample(CycleBuilder.Wired().AllExternalFail().WithoutLocalTrafficReading().Build()));

        var json = EvidenceRoundTrip.Through(payload);

        Assert.False(json.TryGetProperty("localBps", out _));
        Assert.Null(IEM.Storage.Evidence.PayloadReader.Sample(json)?.LocalTrafficBytesPerSecond);
    }

    /// <summary>
    /// The chain layout changed, so the version it declares changed with it - that is what
    /// lets a reader years later know which fields to expect.
    /// </summary>
    [Fact]
    public void The_chain_declares_the_layout_that_carries_this_field()
    {
        Assert.True(EvidenceModelVersion.SchemaVersion >= 3);
    }

    /// <summary>
    /// The busiest second, not an average: the question is whether our own traffic could
    /// explain the outage at all, and the peak is what answers it.
    /// </summary>
    [Fact]
    public void The_busiest_second_of_our_own_traffic_is_kept_for_the_report()
    {
        var evidence = Collect(
            CycleBuilder.Wired().AllExternalFail().WithLocalTraffic(3_000_000).Build(1),
            CycleBuilder.Wired().AllExternalFail().WithLocalTraffic(25_000_000).Build(2),
            CycleBuilder.Wired().AllExternalFail().WithLocalTraffic(1_000).Build(3));

        Assert.Equal(25_000_000, evidence.PeakLocalTrafficBytesPerSecond);
    }

    [Fact]
    public void With_nothing_measured_there_is_no_peak_to_report()
    {
        var evidence = Collect(
            CycleBuilder.Wired().AllExternalFail().WithoutLocalTrafficReading().Build(1));

        Assert.Null(evidence.PeakLocalTrafficBytesPerSecond);
    }

    /// <summary>The figure travels with the incident, so it survives into the package.</summary>
    [Fact]
    public void The_peak_reaches_the_chain_with_the_incident()
    {
        var incident = new IEM.Core.Incidents.IncidentRecord
        {
            Number = 1,
            CorrelationId = Guid.NewGuid(),
            FirstBad = new IEM.Core.Incidents.SampleInstant(TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow),
            LastBad = new IEM.Core.Incidents.SampleInstant(TimeSpan.FromSeconds(14), DateTimeOffset.UtcNow),
            WorstState = NetworkState.CpeUpstreamUnreachable,
            SampleCount = 5,
            TechnicalDetail = "test",
            StatesSeen = [NetworkState.CpeUpstreamUnreachable],
            PeakLocalTrafficBytesPerSecond = 25_000_000,
        };

        var json = EvidenceRoundTrip.Through(IEM.Storage.Evidence.IncidentPayload.From(incident));

        Assert.Equal(25_000_000, json.GetProperty("localBpsPeak").GetInt64());
        Assert.Equal(
            25_000_000,
            IEM.Storage.Evidence.PayloadReader.Incident(json)?.PeakLocalTrafficBytesPerSecond);
    }

    private static IEM.Core.MonitorSample Sample(ProbeCycle cycle) => new(
        cycle.Sequence,
        new IEM.Core.Incidents.SampleInstant(TimeSpan.FromSeconds(cycle.Sequence), cycle.WallUtc),
        cycle,
        new StateClassifier().Classify(cycle),
        IEM.Core.Scheduling.CadencePhase.Stable,
        Clock: null);
}
