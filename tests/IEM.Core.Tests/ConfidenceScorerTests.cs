using IEM.Core.Classification;
using IEM.Core.Model;
using IEM.Core.Presentation;

namespace IEM.Core.Tests;

/// <summary>
/// The scorer answers "how far does this evidence go", and the previous version answered it
/// badly: two supporting signals out of nineteen produced 100 and the label VERY HIGH. An
/// operator only has to point out that seventeen checks never ran for a report built on that
/// to fall apart. Support and coverage are now separate, and coverage caps the band.
/// </summary>
public sealed class ConfidenceScorerTests
{
    private const NetworkState Upstream = NetworkState.CpeUpstreamUnreachable;

    [Fact]
    public void No_checkable_evidence_yields_no_confidence()
    {
        // Claiming certainty from nothing would be an invention, and the one thing a
        // report like this cannot afford is a number nobody can trace.
        var score = ConfidenceScorer.Score(Upstream, new IncidentEvidence());

        Assert.Equal(0, score.Support);
        Assert.Equal(0, score.Coverage);
        Assert.Equal(ConfidenceBand.VeryLow, score.Band);
        Assert.Empty(score.Supporting);
        Assert.Empty(score.Contradicting);
    }

    [Fact]
    public void A_textbook_upstream_outage_scores_very_high()
    {
        var score = ConfidenceScorer.Score(Upstream, new IncidentEvidence
        {
            LinkStayedUp = true,
            WiredConnection = true,
            NoAdapterReset = true,
            NoSystemSleep = true,
            NoClockJump = true,
            NoLocalSaturation = true,
            SsidRemainedVisible = true,
            SignalHealthy = true,
            NoRoaming = true,
            GatewayRemainedReachable = true,
            RouterReportsWanDown = true,
            NoCpeReboot = true,
            AllExternalIcmpFailed = true,
            AllExternalTcpFailed = true,
            TlsFailed = true,
            PublicDnsFailed = true,
            HttpFailed = true,
            TraceLeftHomeNetwork = true,
            PublicAddressChanged = true,
        });

        Assert.Equal(100, score.Support);
        Assert.Equal(100, score.Coverage);
        Assert.Equal(ConfidenceBand.VeryHigh, score.Band);
    }

    // ---- The flaw this rewrite exists to fix ---------------------------------

    /// <summary>
    /// Two clean signals out of nineteen relevant ones. Perfect support, almost no coverage,
    /// and the band must reflect the second - this is precisely the case the old model
    /// reported as VERY HIGH.
    /// </summary>
    [Fact]
    public void Perfect_support_over_a_sliver_of_coverage_is_not_a_strong_conclusion()
    {
        var score = ConfidenceScorer.Score(Upstream, new IncidentEvidence
        {
            LinkStayedUp = true,
            GatewayRemainedReachable = true,
        });

        Assert.Equal(100, score.Support);
        Assert.True(score.Coverage < 25, $"coverage should be small, got {score.Coverage}");
        Assert.NotEqual(ConfidenceBand.VeryHigh, score.Band);
        Assert.True(score.Band <= ConfidenceBand.Low, $"band should be capped low, got {score.Band}");
    }

    [Fact]
    public void Coverage_rises_as_more_of_the_picture_can_be_checked()
    {
        var sparse = ConfidenceScorer.Score(Upstream, new IncidentEvidence
        {
            LinkStayedUp = true,
            GatewayRemainedReachable = true,
        });

        var thorough = ConfidenceScorer.Score(Upstream, new IncidentEvidence
        {
            LinkStayedUp = true,
            WiredConnection = true,
            NoAdapterReset = true,
            NoSystemSleep = true,
            NoClockJump = true,
            NoLocalSaturation = true,
            GatewayRemainedReachable = true,
            NoCpeReboot = true,
            AllExternalIcmpFailed = true,
            AllExternalTcpFailed = true,
            HttpFailed = true,
        });

        Assert.Equal(sparse.Support, thorough.Support);
        Assert.True(thorough.Coverage > sparse.Coverage);
        Assert.True(thorough.Band > sparse.Band);
    }

    // ---- Aware of what it is proving ------------------------------------------

    /// <summary>
    /// External targets failing is the heart of the upstream case and beside the point for a
    /// dead access point - of course nothing external answered, the radio was gone. Counting
    /// it there would inflate the score with a signal that follows from the fault itself.
    /// </summary>
    [Fact]
    public void The_same_evidence_is_weighed_differently_for_different_conclusions()
    {
        var evidence = new IncidentEvidence
        {
            SsidRemainedVisible = true,
            SignalHealthy = true,
            NoRoaming = true,
            AllExternalIcmpFailed = true,
            AllExternalTcpFailed = true,
            HttpFailed = true,
        };

        var asUpstream = ConfidenceScorer.Score(Upstream, evidence);
        var asRadio = ConfidenceScorer.Score(NetworkState.WifiRadioDown, evidence);

        Assert.NotEqual(asUpstream.Coverage, asRadio.Coverage);

        Assert.Contains(asRadio.Evidence, e =>
            e.Key == "upstream.icmpFailed" && e.Outcome == EvidenceOutcome.NotApplicable);

        Assert.Contains(asUpstream.Evidence, e =>
            e.Key == "upstream.icmpFailed" && e.Outcome == EvidenceOutcome.Supports);
    }

    /// <summary>
    /// A signal that was never relevant is not a gap in the evidence. Counting it as one
    /// would penalise every conclusion for checks it had no business running.
    /// </summary>
    [Fact]
    public void Irrelevant_signals_do_not_count_as_missing_evidence()
    {
        var score = ConfidenceScorer.Score(NetworkState.AdapterDown, new IncidentEvidence
        {
            LinkStayedUp = true,
            NoAdapterReset = true,
            NoSystemSleep = true,
            NoClockJump = true,
        });

        Assert.Equal(100, score.Coverage);
        Assert.Empty(score.Missing);
        Assert.Contains(score.Evidence, e => e.Outcome == EvidenceOutcome.NotApplicable);
    }

    // ---- What a pause in monitoring withdraws, and what it does not ------------

    /// <summary>
    /// A pause rules out the claims it actually undermines and no others. Printing "the
    /// system clock moved" against an outage where it demonstrably did not is a false
    /// statement in a document meant for an operator - and one they can disprove.
    /// </summary>
    [Fact]
    public void A_pause_does_not_invent_a_clock_jump_that_did_not_happen()
    {
        var collector = new IncidentEvidenceCollector();
        collector.Begin();
        collector.Observe(CycleBuilder.Wired().AllExternalFail().Build(), new ClassificationContext(), false);

        // The service was restarted. The clock never moved.
        collector.NoteMonitoringPaused(sleep: false, clockAdjusted: false);

        var evidence = collector.Build();

        Assert.True(evidence.NoClockJump);
        Assert.True(evidence.NoSystemSleep);
    }

    [Fact]
    public void A_pause_caused_by_the_clock_being_corrected_is_recorded_as_one()
    {
        var collector = new IncidentEvidenceCollector();
        collector.Begin();
        collector.Observe(CycleBuilder.Wired().AllExternalFail().Build(), new ClassificationContext(), false);

        collector.NoteMonitoringPaused(sleep: false, clockAdjusted: true);

        Assert.False(collector.Build().NoClockJump);
    }

    [Fact]
    public void A_machine_that_slept_during_the_outage_says_so()
    {
        var collector = new IncidentEvidenceCollector();
        collector.Begin();
        collector.Observe(CycleBuilder.Wired().AllExternalFail().Build(), new ClassificationContext(), false);

        collector.NoteMonitoringPaused(sleep: true, clockAdjusted: false);

        var evidence = collector.Build();

        Assert.False(evidence.NoSystemSleep);
        Assert.True(evidence.NoClockJump, "sleeping is not the clock being corrected");
    }

    // ---- Unchanged guarantees --------------------------------------------------

    [Fact]
    public void Weak_signal_and_roaming_pull_confidence_down()
    {
        // Same failing probes, but the Wi-Fi link was suspect. An upstream fault is a much
        // weaker explanation and the score has to say so.
        var score = ConfidenceScorer.Score(Upstream, new IncidentEvidence
        {
            LinkStayedUp = false,
            WiredConnection = false,
            SignalHealthy = false,
            NoRoaming = false,
            SsidRemainedVisible = false,
            AllExternalIcmpFailed = true,
            AllExternalTcpFailed = true,
            HttpFailed = true,
        });

        Assert.True(score.Support < 50, $"expected weak support, got {score.Support}");
        Assert.Contains(score.Contradicting, e => e.Key == "wifi.ssidVisible");
    }

    [Fact]
    public void Unavailable_signals_are_listed_rather_than_assumed()
    {
        // The difference between "we checked and it held" and "we could not check" is
        // the difference between evidence and hand-waving, so it stays visible.
        var score = ConfidenceScorer.Score(Upstream, new IncidentEvidence
        {
            LinkStayedUp = true,
            GatewayRemainedReachable = true,
        });

        Assert.NotEmpty(score.Missing);
        Assert.Contains(score.Missing, e => e.Key == "upstream.traceLeftNetwork");
        Assert.All(score.Missing, e => Assert.Equal("!", e.Marker));
    }

    [Fact]
    public void Unavailable_signals_do_not_shift_the_support_figure()
    {
        var score = ConfidenceScorer.Score(Upstream, new IncidentEvidence
        {
            LinkStayedUp = true,
            GatewayRemainedReachable = false,
        });

        // One supporting at weight 10, one contradicting at weight 12.
        Assert.Equal(45, score.Support);
    }

    /// <summary>
    /// The reader sees a band. A percentage looks like a probability and is not one - it is
    /// a weighted sum of heuristics, and printing it invites an argument about the second
    /// decimal instead of about the evidence.
    /// </summary>
    [Fact]
    public void The_reader_is_shown_a_band_and_the_reason_for_it()
    {
        var score = ConfidenceScorer.Score(Upstream, new IncidentEvidence
        {
            LinkStayedUp = true,
            GatewayRemainedReachable = true,
        });

        // Two signals out of nineteen relevant ones, both clean. The old model called this
        // VRLO VISOKA; on the evidence it is the bottom of the scale.
        Assert.Equal("VRLO NISKA", score.Band.Label());

        var explanation = score.Explain();

        Assert.Contains("2 od 19", explanation, StringComparison.Ordinal);
        Assert.Contains("ograničava", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("%", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_signal_appears_in_the_evidence_list_exactly_once()
    {
        var score = ConfidenceScorer.Score(Upstream, new IncidentEvidence { LinkStayedUp = true });

        Assert.Equal(score.Evidence.Count, score.Evidence.Select(e => e.Key).Distinct().Count());
        Assert.Equal(
            score.Evidence.Count,
            score.Supporting.Count() + score.Contradicting.Count() + score.Missing.Count() +
            score.Evidence.Count(e => e.Outcome == EvidenceOutcome.NotApplicable));
    }
}
