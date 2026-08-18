using IEM.Core.Classification;
using IEM.Core.Model;
using IEM.Core.Presentation;

namespace IEM.Core.Tests;

/// <summary>
/// One test per entry in the fault catalogue. These are the tests that decide whether a
/// user files a complaint they can win, so each one names the real-world fault it stands for.
/// </summary>
public sealed class StateClassifierTests
{
    private readonly StateClassifier _classifier = new();

    [Fact]
    public void Healthy_wired_cycle_is_ok()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().Build());

        Assert.Equal(NetworkState.Ok, verdict.State);
        Assert.Equal(Severity.Ok, verdict.Severity);
    }

    // ---- Local device -----------------------------------------------------

    [Fact]
    public void Adapter_down_is_a_local_fault_not_the_operators()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().AdapterDown().AllExternalFail().Build());

        Assert.Equal(NetworkState.AdapterDown, verdict.State);
        Assert.True(verdict.IsOutage);
        Assert.False(verdict.State.IsUpstream());
    }

    [Fact]
    public void Speed_test_suspends_assessment_entirely()
    {
        // Our own test saturates the link, so anything measured during it describes the
        // test rather than the service and must never become an incident.
        var verdict = _classifier.Classify(CycleBuilder.Wired().DuringSelfTest().AllExternalFail().Build());

        Assert.Equal(NetworkState.SelfTest, verdict.State);
        Assert.False(verdict.IsOutage);
    }

    // ---- Wi-Fi link -------------------------------------------------------

    [Fact]
    public void Router_wifi_radio_failure_is_blamed_on_the_router()
    {
        // The headline home fault: the router keeps its WAN link but stops broadcasting.
        // The adapter drops, which looks exactly like walking out of range - the SSID
        // vanishing from the scan is what separates the two.
        var verdict = _classifier.Classify(
            CycleBuilder.Wireless().AdapterDown().SsidNotVisible().AllExternalFail().Build());

        Assert.Equal(NetworkState.WifiRadioDown, verdict.State);
        Assert.False(verdict.State.IsUpstream());
    }

    [Fact]
    public void Adapter_down_with_ssid_still_visible_is_not_a_radio_failure()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wireless().AdapterDown().AllExternalFail().Build());

        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    /// <summary>
    /// Switching Wi-Fi off makes every network vanish from the scan. Reading that as the
    /// router having failed would blame equipment for something the customer did with a
    /// keypress - and would put a fabricated incident into a complaint.
    /// </summary>
    [Fact]
    public void Turning_the_radio_off_is_not_a_router_failure()
    {
        var verdict = _classifier.Classify(
            CycleBuilder.Wireless().AdapterDown().RadioOff().AllExternalFail().Build());

        Assert.Equal(NetworkState.AdapterDown, verdict.State);
        Assert.Equal(FaultAttribution.LocalDevice, verdict.State.AttributionOf());
    }

    /// <summary>
    /// The same refusal, one step further back: the SSID really is gone from the scan, but
    /// whether the radio is on could not be established.
    /// <para>
    /// Until 2.3 the condition read <c>RadioOn != false</c>, so an unreadable radio counted
    /// as one that was on and the vanished network was reported as the access point having
    /// stopped broadcasting. That is a finding of equipment failure assembled out of a
    /// failed query - the substitution this tool exists to refuse, on the most consequential
    /// judgement it makes.
    /// </para>
    /// </summary>
    [Fact]
    public void Adapter_down_with_an_unreadable_radio_is_not_a_router_failure()
    {
        var verdict = _classifier.Classify(
            CycleBuilder.Wireless().AdapterDown().SsidNotVisible().RadioStateUnreadable()
                .AllExternalFail().Build());

        Assert.Equal(NetworkState.AdapterDown, verdict.State);
        Assert.DoesNotContain("radio", verdict.TechnicalDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adapter_down_without_scan_data_does_not_guess_at_a_radio_failure()
    {
        var verdict = _classifier.Classify(
            CycleBuilder.Wireless().AdapterDown().SsidScanUnavailable().AllExternalFail().Build());

        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    [Fact]
    public void Roaming_is_reported_only_when_nothing_else_is_wrong()
    {
        var context = new ClassificationContext { BssidChanged = true };
        var verdict = _classifier.Classify(CycleBuilder.Wireless().Build(), context);

        Assert.Equal(NetworkState.WifiRoaming, verdict.State);
        Assert.Equal(Severity.Info, verdict.Severity);
    }

    // ---- Router / upstream ------------------------------------------------

    [Fact]
    public void Gateway_up_and_nothing_beyond_is_upstream_unreachable()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().AllExternalFail().Build());

        Assert.Equal(NetworkState.CpeUpstreamUnreachable, verdict.State);
        Assert.True(verdict.State.IsUpstream());
    }

    [Fact]
    public void Gateway_down_with_nothing_beyond_is_a_gateway_fault()
    {
        var verdict = _classifier.Classify(
            CycleBuilder.Wired().GatewayUnreachable().AllExternalFail().Build());

        Assert.Equal(NetworkState.GatewayDown, verdict.State);
        Assert.False(verdict.State.IsUpstream());
    }

    [Fact]
    public void Router_reboot_outranks_a_plain_upstream_verdict()
    {
        var context = new ClassificationContext { CpeRebootDetected = true };
        var verdict = _classifier.Classify(CycleBuilder.Wired().AllExternalFail().Build(), context);

        Assert.Equal(NetworkState.CpeReboot, verdict.State);
        Assert.False(verdict.State.IsUpstream());
    }

    /// <summary>
    /// A router whose WAN uptime resets during an outage reconnected on its own. Charging
    /// that to the operator would blame a line fault for the customer's own equipment
    /// restarting - and only the router itself can tell the two apart.
    /// </summary>
    [Fact]
    public void A_router_that_reconnected_is_not_charged_to_the_operator()
    {
        var cycle = CycleBuilder.Wired().RouterReconnected().AllExternalFail().Build();
        var context = new ClassificationContext { CpeRebootDetected = cycle.Link.RouterReconnected };

        var verdict = _classifier.Classify(cycle, context);

        Assert.Equal(NetworkState.CpeReboot, verdict.State);
        Assert.Equal(FaultAttribution.Router, verdict.State.AttributionOf());
    }

    [Fact]
    public void Router_reporting_its_own_wan_down_is_recorded_on_the_cycle()
    {
        // The classification is unchanged - the computer sees the same thing either way -
        // but the router's own word is carried through so the confidence score can use it.
        var cycle = CycleBuilder.Wired().RouterReportsWanDown().AllExternalFail().Build();

        var verdict = _classifier.Classify(cycle);

        Assert.Equal(NetworkState.CpeUpstreamUnreachable, verdict.State);
        Assert.True(cycle.Link.Router?.IsDisconnected);
    }

    [Fact]
    public void Unreachable_internet_without_gateway_information_stays_unattributed()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().NoGateway().AllExternalFail().Build());

        Assert.Equal(NetworkState.InternetDown, verdict.State);
    }

    [Fact]
    public void Silent_gateway_is_ignored_while_the_internet_works()
    {
        // Plenty of routers simply do not answer ICMP. That is a preference, not a fault.
        var verdict = _classifier.Classify(CycleBuilder.Wired().GatewayUnreachable().Build());

        Assert.Equal(NetworkState.Ok, verdict.State);
    }

    // ---- DNS --------------------------------------------------------------

    [Fact]
    public void Operator_resolver_failure_is_separated_from_a_global_one()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().DnsIspFailsOnly().Build());

        Assert.Equal(NetworkState.DnsIspFailure, verdict.State);
        Assert.Equal(Severity.Degraded, verdict.Severity);
    }

    [Fact]
    public void All_resolvers_failing_while_ip_works_is_a_dns_fault_not_an_outage()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().DnsAllFail().Build());

        Assert.Equal(NetworkState.DnsGlobalFailure, verdict.State);
        Assert.False(verdict.IsOutage);
    }

    /// <summary>
    /// An IPv6 resolver that stays silent, with only an IPv4 public resolver to compare it
    /// against, is not a finding about that resolver.
    /// <para>
    /// Reported by a tester on 18.08.2026: his router was reached over IPv6 link-local and was
    /// also his first DNS server. The only public resolver was 1.1.1.1, over IPv4. Every cycle
    /// compared a query on one stack against a query on the other and filed the difference as
    /// a fault of the first, for a whole session, while his connection worked perfectly -
    /// system DNS, HTTP, TCP and ping all fine.
    /// </para>
    /// </summary>
    [Fact]
    public void A_silent_ipv6_resolver_is_not_judged_against_an_ipv4_one()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired()
            .WithResolvers(
                (DnsResolverRole.IspAssigned, "fe80::8229:94ff:fe1d:d063%10", false),
                (DnsResolverRole.Public, "1.1.1.1", true),
                (DnsResolverRole.System, "system", true))
            .Build());

        Assert.NotEqual(NetworkState.DnsIspFailure, verdict.State);
    }

    /// <summary>The same silence, once there is a fair comparison, is a finding.</summary>
    [Fact]
    public void A_silent_ipv6_resolver_is_judged_against_the_ipv6_public_one()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired()
            .WithResolvers(
                (DnsResolverRole.IspAssigned, "fe80::8229:94ff:fe1d:d063%10", false),
                (DnsResolverRole.Public, "2606:4700:4700::1111", true),
                (DnsResolverRole.Public, "1.1.1.1", true),
                (DnsResolverRole.System, "system", true))
            .Build());

        Assert.Equal(NetworkState.DnsIspFailure, verdict.State);
    }

    /// <summary>
    /// One resolver of several does not speak for the rest. Only the first was ever queried
    /// before, so a single unreachable entry reported every assigned resolver as failed.
    /// </summary>
    [Fact]
    public void One_silent_resolver_out_of_several_is_not_all_of_them()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired()
            .WithResolvers(
                (DnsResolverRole.IspAssigned, "192.168.1.1", false),
                (DnsResolverRole.IspAssigned, "192.168.1.2", true),
                (DnsResolverRole.Public, "1.1.1.1", true),
                (DnsResolverRole.System, "system", true))
            .Build());

        Assert.NotEqual(NetworkState.DnsIspFailure, verdict.State);
        Assert.Equal(NetworkState.Ok, verdict.State);
    }

    /// <summary>Every assigned resolver of that family silent is still the finding it was.</summary>
    [Fact]
    public void Every_assigned_resolver_silent_is_the_finding()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired()
            .WithResolvers(
                (DnsResolverRole.IspAssigned, "192.168.1.1", false),
                (DnsResolverRole.IspAssigned, "192.168.1.2", false),
                (DnsResolverRole.Public, "1.1.1.1", true),
                (DnsResolverRole.System, "system", true))
            .Build());

        Assert.Equal(NetworkState.DnsIspFailure, verdict.State);
    }

    /// <summary>The finding never names the operator: that address is normally the router.</summary>
    [Fact]
    public void The_finding_does_not_blame_the_operator()
    {
        var text = NetworkState.DnsIspFailure.Label() + " " + NetworkState.DnsIspFailure.Explanation();

        Assert.DoesNotContain("operatera", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ruter", text, StringComparison.OrdinalIgnoreCase);
    }


    // ---- False positives that must not become incidents --------------------

    [Fact]
    public void Filtered_icmp_is_never_reported_as_an_outage()
    {
        // Blocking ping while the web works is common. Reporting it as downtime would
        // manufacture incidents and discredit the whole report.
        var verdict = _classifier.Classify(CycleBuilder.Wired().ExternalIcmpAllFail().Build());

        Assert.Equal(NetworkState.IcmpFiltered, verdict.State);
        Assert.False(verdict.IsOutage);
    }

    /// <summary>
    /// P0-2 at the decision level. A handshake that succeeded before the trouble started
    /// used to keep the classifier from ever calling this an outage - it saw TLS and HTTP
    /// "succeeding" and settled on harmless filtering. A short outage disappeared entirely.
    /// </summary>
    [Fact]
    public void A_success_measured_before_the_trouble_cannot_hide_an_outage()
    {
        var cycle = CycleBuilder.Wired()
            .MeasuredBeforeTrouble().TlsSucceeds().HttpSucceeds().DnsAllSucceed()
            .MeasuredNow().ExternalIcmpAllFail().ExternalTcpAllFail().GatewayReachable()
            .Build();

        var verdict = _classifier.Classify(cycle);

        Assert.Equal(NetworkState.CpeUpstreamUnreachable, verdict.State);
        Assert.True(verdict.IsOutage);
    }

    [Fact]
    public void A_success_measured_too_long_ago_cannot_hide_an_outage_either()
    {
        var cycle = CycleBuilder.Wired()
            .MeasuredTooLongAgo().TlsSucceeds().HttpSucceeds().DnsAllSucceed()
            .MeasuredNow().ExternalIcmpAllFail().ExternalTcpAllFail().GatewayReachable()
            .Build();

        Assert.True(_classifier.Classify(cycle).IsOutage);
    }

    /// <summary>
    /// The mirror image: filtering is still filtering, as long as something proves right
    /// now that traffic is getting through.
    /// </summary>
    [Fact]
    public void A_fresh_success_still_distinguishes_filtering_from_an_outage()
    {
        var cycle = CycleBuilder.Wired().ExternalIcmpAllFail().Build();

        var verdict = _classifier.Classify(cycle);

        Assert.Equal(NetworkState.IcmpFiltered, verdict.State);
        Assert.False(verdict.IsOutage);
    }

    /// <summary>
    /// The Windows resolver chooses its own path and cannot be bound to the monitored
    /// adapter, so its answer can never prove that <em>this</em> link is carrying traffic.
    /// </summary>
    [Fact]
    public void The_system_resolver_alone_does_not_prove_the_link_works()
    {
        var cycle = CycleBuilder.Wired()
            .ExternalIcmpAllFail().ExternalTcpAllFail().TlsFails().HttpFails()
            .OnlySystemDnsAnswers()
            .Build();

        Assert.True(_classifier.Classify(cycle).IsOutage);
    }

    [Fact]
    public void Captive_portal_is_flagged_as_suspected_rather_than_confirmed()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().CaptivePortal().Build());

        Assert.Equal(NetworkState.CaptivePortalSuspected, verdict.State);
    }

    [Fact]
    public void Some_external_targets_falling_silent_is_degradation_not_downtime()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().ExternalIcmpPartialLoss().Build());

        Assert.Equal(NetworkState.PacketLoss, verdict.State);
        Assert.False(verdict.IsOutage);
    }

    /// <summary>
    /// What this state actually observes: one probe each to three <em>different</em>
    /// destinations. Reported as a loss percentage it moved in thirds, so a single resolver
    /// that filters ICMP by policy put "33,3 % gubitka paketa" into a complaint - a figure
    /// the operator can dismiss in one sentence, and should.
    /// </summary>
    [Fact]
    public void One_silent_target_is_not_reported_as_a_third_of_packets_lost()
    {
        var verdict = _classifier.Classify(CycleBuilder.Wired().ExternalIcmpPartialLoss().Build());

        Assert.DoesNotContain("% loss", verdict.TechnicalDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("33", verdict.TechnicalDetail, StringComparison.Ordinal);

        // And it names the destination that went quiet, because which one it was is the
        // finding: the same target every sample is a policy, a different one each time is a
        // connection.
        Assert.Contains("9.9.9.9", verdict.TechnicalDetail, StringComparison.Ordinal);
        Assert.Contains("did not answer", verdict.TechnicalDetail, StringComparison.Ordinal);
    }

    /// <summary>The Serbian a reader sees has to make the same distinction, in their words.</summary>
    [Fact]
    public void The_serbian_wording_says_it_is_not_a_packet_loss_measurement()
    {
        var label = NetworkState.PacketLoss.Label();
        var explanation = NetworkState.PacketLoss.Explanation();

        Assert.DoesNotContain("gubitak", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NIJE merenje gubitka paketa", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Latency_above_the_threshold_is_reported()
    {
        var verdict = _classifier.Classify(
            CycleBuilder.Wired().ExternalIcmpLatency(TimeSpan.FromMilliseconds(400)).Build());

        Assert.Equal(NetworkState.HighLatency, verdict.State);
    }

    [Fact]
    public void Jitter_above_the_threshold_outranks_latency()
    {
        var context = new ClassificationContext { Jitter = TimeSpan.FromMilliseconds(120) };
        var verdict = _classifier.Classify(
            CycleBuilder.Wired().ExternalIcmpLatency(TimeSpan.FromMilliseconds(400)).Build(), context);

        Assert.Equal(NetworkState.HighJitter, verdict.State);
    }

    [Fact]
    public void Classification_is_deterministic()
    {
        var cycle = CycleBuilder.Wired().AllExternalFail().Build();

        var first = _classifier.Classify(cycle);
        var second = _classifier.Classify(cycle);

        Assert.Equal(first, second);
    }
}
