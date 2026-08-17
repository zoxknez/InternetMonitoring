using IEM.Core.Model;
using IEM.Core.Presentation;

namespace IEM.Core.Tests;

/// <summary>
/// P0-6. The old model asked "whose fault is it" and answered it, which a monitor running on
/// the customer's own computer is in no position to do. These tests hold the line on what
/// the tool may claim: how far along the path the problem was isolated, and nothing beyond.
/// </summary>
public sealed class FaultDomainTests
{
    /// <summary>
    /// The headline contradiction. The comment beside the state already said it did not
    /// prove the operator was at fault, while the report built from it opened with
    /// "confirmed outages on the operator's side".
    /// </summary>
    [Fact]
    public void An_unreachable_upstream_is_isolated_not_blamed()
    {
        var domain = NetworkState.CpeUpstreamUnreachable.DomainOf(LinkMedium.Ethernet);

        Assert.Equal(FaultDomain.UpstreamPath, domain);
        Assert.True(domain.WorthReportingToOperator());
        Assert.DoesNotContain("Potvrđeno", domain.Explain(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("iza vašeg rutera", domain.Explain(), StringComparison.Ordinal);

        // What it does not settle, named rather than gestured at. 2.0 said "ne dokazuje šta
        // se dešava unutar mreže operatera" and then claimed to have excluded the customer's
        // own equipment - which the router's WAN side is part of.
        Assert.Contains("Nisu isključeni", domain.Explain(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing answered <em>and</em> there was no gateway to test against, so the fault could
    /// equally be the customer's own router. Calling that upstream inflates the one figure
    /// an operator checks first.
    /// </summary>
    [Fact]
    public void Everything_failing_with_no_gateway_to_test_isolates_nothing()
    {
        Assert.Equal(FaultDomain.Unknown, NetworkState.InternetDown.DomainOf(LinkMedium.Ethernet));
        Assert.False(NetworkState.InternetDown.IsUpstream());
    }

    /// <summary>
    /// A resolver failing while the path underneath carries traffic is a real fault worth
    /// reporting - but it is not the connection being down, and folding it into the outage
    /// figure would make that figure mean two different things at once.
    /// </summary>
    [Fact]
    public void A_broken_operator_resolver_is_a_dns_fault_not_an_outage()
    {
        var domain = NetworkState.DnsIspFailure.DomainOf(LinkMedium.Ethernet);

        Assert.Equal(FaultDomain.Dns, domain);
        Assert.True(domain.WorthReportingToOperator());
        Assert.False(NetworkState.DnsIspFailure.IsOutage());
        Assert.False(NetworkState.DnsIspFailure.IsUpstream());
    }

    /// <summary>
    /// The same state is a different fault depending on what the link is made of, so the
    /// medium has to be part of the decision.
    /// </summary>
    [Theory]
    [InlineData(LinkMedium.Wireless, FaultDomain.LocalWifi)]
    [InlineData(LinkMedium.Ethernet, FaultDomain.LocalLan)]
    [InlineData(LinkMedium.Unknown, FaultDomain.LocalHost)]
    public void A_down_adapter_is_read_against_the_medium(LinkMedium medium, FaultDomain expected)
    {
        Assert.Equal(expected, NetworkState.AdapterDown.DomainOf(medium));
    }

    [Fact]
    public void A_router_whose_radio_died_is_the_routers_problem()
    {
        var domain = NetworkState.WifiRadioDown.DomainOf(LinkMedium.Wireless);

        Assert.Equal(FaultDomain.Cpe, domain);
        Assert.True(domain.WorthReportingToOperator(), "operator-supplied routers are still reported, as equipment");
    }

    [Fact]
    public void Filtered_ping_is_not_a_fault_anywhere()
    {
        Assert.Equal(FaultDomain.None, NetworkState.IcmpFiltered.DomainOf(LinkMedium.Ethernet));
        Assert.False(FaultDomain.None.WorthReportingToOperator());
    }

    [Fact]
    public void A_local_fault_is_never_put_to_the_operator()
    {
        Assert.False(FaultDomain.LocalHost.WorthReportingToOperator());
        Assert.False(FaultDomain.LocalLan.WorthReportingToOperator());
        Assert.False(FaultDomain.Unknown.WorthReportingToOperator());
    }

    // ---- The verdict the whole application exists to deliver -------------------

    [Fact]
    public void The_session_verdict_states_what_was_measured_not_who_is_to_blame()
    {
        var verdict = SessionVerdict.Evaluate(TimeSpan.FromHours(6), upstreamIncidentCount: 3, TimeSpan.Zero);

        Assert.Equal(VerdictKind.UpstreamFault, verdict.Kind);
        Assert.True(verdict.SupportsComplaint);

        var text = verdict.Headline + " " + verdict.Detail;

        Assert.DoesNotContain("na strani operatera", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("izolovani iza vaše opreme", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_model_version_is_stated_so_a_conclusion_can_be_reproduced()
    {
        Assert.True(EvidenceModelVersion.SchemaVersion >= 2);
        Assert.NotEmpty(EvidenceModelVersion.ClassifierVersion);
        Assert.NotEmpty(EvidenceModelVersion.AttributionModelVersion);
        Assert.NotEmpty(EvidenceModelVersion.ConfidenceModelVersion);
    }
}
