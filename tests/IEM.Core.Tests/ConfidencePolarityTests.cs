using IEM.Core.Classification;
using IEM.Core.Model;

namespace IEM.Core.Tests;

/// <summary>
/// Which way each signal has to point for the conclusion being argued.
/// <para>
/// A signal is phrased once - "the SSID stayed visible", "the gateway kept answering" - and
/// the 1.0 model read true as support whatever was being claimed. But those readings mean
/// opposite things depending on the claim: the SSID vanishing is the entire case for a
/// failed access-point radio, and it was scored against it. Every conclusion except the
/// upstream one was penalised by the one fact that established it, and the band printed
/// beside it in the report came out a step low.
/// </para>
/// </summary>
public sealed class ConfidencePolarityTests
{
    /// <summary>
    /// The four conclusions whose defining evidence is an absence: the network gone from the
    /// air, the gateway silent, the router restarted, the adapter down.
    /// </summary>
    [Theory]
    [InlineData(NetworkState.WifiRadioDown, "wifi.ssidVisible")]
    [InlineData(NetworkState.GatewayDown, "cpe.gatewayReachable")]
    [InlineData(NetworkState.CpeReboot, "cpe.noReboot")]
    [InlineData(NetworkState.AdapterDown, "link.stayedUp")]
    public void The_fact_that_establishes_a_conclusion_supports_it(NetworkState state, string key)
    {
        var establishing = Evidence(state, defining: false);
        var contrary = Evidence(state, defining: true);

        Assert.Contains(ConfidenceScorer.Score(state, establishing).Supporting, e => e.Key == key);
        Assert.Contains(ConfidenceScorer.Score(state, contrary).Contradicting, e => e.Key == key);
    }

    /// <summary>
    /// The upstream conclusion is the one the old model happened to get right, and it has to
    /// stay right: there the fault is past the router, so every nearer thing holding up is
    /// what supports it.
    /// </summary>
    [Fact]
    public void For_an_upstream_fault_everything_nearer_holding_up_is_what_supports_it()
    {
        var score = ConfidenceScorer.Score(
            NetworkState.CpeUpstreamUnreachable,
            new IncidentEvidence
            {
                LinkStayedUp = true,
                GatewayRemainedReachable = true,
                SsidRemainedVisible = true,
                AllExternalIcmpFailed = true,
            });

        foreach (var key in new[] { "link.stayedUp", "cpe.gatewayReachable", "wifi.ssidVisible", "upstream.icmpFailed" })
        {
            Assert.Contains(score.Supporting, e => e.Key == key);
        }
    }

    /// <summary>
    /// A genuine router radio failure now scores on its own terms. Before, its defining fact
    /// cost it ten weight of support and dragged the band down a step.
    /// </summary>
    [Fact]
    public void A_genuine_radio_failure_is_not_penalised_by_its_own_evidence()
    {
        var score = ConfidenceScorer.Score(
            NetworkState.WifiRadioDown,
            new IncidentEvidence
            {
                SsidRemainedVisible = false,
                NoAdapterReset = true,
                NoSystemSleep = true,
                NoClockJump = true,
            });

        Assert.Equal(100, score.Support);
        Assert.Empty(score.Contradicting);
    }

    /// <summary>Signals outside a conclusion's set stay out of both numbers, as before.</summary>
    [Fact]
    public void Irrelevant_signals_are_still_neither_support_nor_a_gap()
    {
        var score = ConfidenceScorer.Score(
            NetworkState.AdapterDown,
            new IncidentEvidence { LinkStayedUp = false, AllExternalIcmpFailed = true });

        Assert.Contains(score.Evidence, e =>
            e.Key == "upstream.icmpFailed" && e.Outcome == EvidenceOutcome.NotApplicable);
    }

    /// <summary>
    /// Builds evidence where the conclusion's defining signal points whichever way is asked
    /// for, and everything else is left unchecked so only that one signal is in play.
    /// </summary>
    private static IncidentEvidence Evidence(NetworkState state, bool defining) => state switch
    {
        NetworkState.WifiRadioDown => new IncidentEvidence { SsidRemainedVisible = defining },
        NetworkState.GatewayDown => new IncidentEvidence { GatewayRemainedReachable = defining },
        NetworkState.CpeReboot => new IncidentEvidence { NoCpeReboot = defining },
        NetworkState.AdapterDown => new IncidentEvidence { LinkStayedUp = defining },
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
