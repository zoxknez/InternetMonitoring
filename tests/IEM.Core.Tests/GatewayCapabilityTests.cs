using IEM.Core.Gateway;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and forensic tests for Phase 3.0-8: Gateway Capability Learning & Behavioral Assessment.
/// Invariants 46-54.
/// </summary>
public sealed class GatewayCapabilityTests
{
    private static GatewayIdentity CreateDefaultGateway(string ip = "192.168.1.1", string iface = "eth0") =>
        new("gw-primary", ip, TargetAddressFamily.IPv4, iface, "192.168.1.100");

    [Fact]
    public void Fresh_gateway_capabilities_start_Unknown()
    {
        var gw = CreateDefaultGateway();
        var evaluator = new GatewayCapabilityEvaluator(gw);
        var profile = evaluator.GetCurrentProfile();

        Assert.All(profile.Capabilities, c => Assert.Equal(CapabilityEvidenceState.Unknown, c.EvidenceState));
        Assert.Empty(evaluator.History);
    }

    [Fact]
    public void No_ICMP_reply_never_becomes_Unsupported_Invariant_46()
    {
        // Invariant 46: ABSENCE_OF_GATEWAY_RESPONSE_NEVER_PROVES_UNSUPPORTED_CAPABILITY
        var gw = CreateDefaultGateway();
        var evaluator = new GatewayCapabilityEvaluator(gw);
        var now = DateTimeOffset.UtcNow;

        // 10 consecutive timeouts
        for (var i = 1; i <= 10; i++)
        {
            evaluator.ProcessObservation(new GatewayCapabilityObservation(
                $"obs-{i}", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
                now.AddSeconds(i), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        }

        var profile = evaluator.GetCurrentProfile();
        var icmp = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho);

        Assert.Equal(CapabilityEvidenceState.ResponseNotYetObserved, icmp.EvidenceState);
        Assert.NotEqual(CapabilityEvidenceState.Unknown, icmp.EvidenceState);
        // Explicitly NOT unsupported
    }

    [Fact]
    public void Positive_ICMP_reply_establishes_observed_capability_Invariant_47()
    {
        // Invariant 47: OBSERVED_GATEWAY_CAPABILITY_IS_ESTABLISHED_ONLY_BY_POSITIVE_EVIDENCE
        var gw = CreateDefaultGateway();
        var evaluator = new GatewayCapabilityEvaluator(gw);
        var now = DateTimeOffset.UtcNow;

        var snap = evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-1", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        var profile = evaluator.GetCurrentProfile();
        var icmp = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho);

        Assert.Equal(CapabilityEvidenceState.ObservedSupported, icmp.EvidenceState);
        Assert.Equal(1, icmp.SuccessfulObservationCount);
        Assert.Equal(now, icmp.FirstObservedUtc);
        Assert.Equal(GatewayBehaviorState.NormallyResponding, snap.BehaviorState);
    }

    [Fact]
    public void Previously_observed_ICMP_then_timeouts_is_stronger_than_never_observed()
    {
        var gw = CreateDefaultGateway();
        var policy = new GatewayCapabilityPolicy { MissingCapabilityConsecutiveWindows = 2 };
        var evaluator = new GatewayCapabilityEvaluator(gw, policy);
        var now = DateTimeOffset.UtcNow;

        // Positive observation initially
        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-1", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        // Consecutive timeouts
        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-2", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now.AddSeconds(1), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        var snap3 = evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-3", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now.AddSeconds(2), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        var profile = evaluator.GetCurrentProfile();
        var icmp = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho);

        Assert.Equal(CapabilityEvidenceState.PreviouslyObserved, icmp.EvidenceState);
        Assert.Equal(GatewayBehaviorState.PreviouslyObservedCapabilityMissing, snap3.BehaviorState);
    }

    [Fact]
    public void Initial_learning_window_expiry_preserves_Unknown_Invariant_52()
    {
        // Invariant 52: INITIAL_LEARNING_WINDOW_NEVER_FREEZES_UNKNOWN_AS_UNSUPPORTED
        var gw = CreateDefaultGateway();
        var policy = new GatewayCapabilityPolicy { InitialLearningWindow = TimeSpan.FromMinutes(5) };
        var evaluator = new GatewayCapabilityEvaluator(gw, policy);

        // No observations during first 10 minutes
        var profile = evaluator.GetCurrentProfile();
        var icmp = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho);

        Assert.Equal(CapabilityEvidenceState.Unknown, icmp.EvidenceState);
    }

    [Fact]
    public void Late_first_reply_can_establish_capability_after_learning_window()
    {
        var gw = CreateDefaultGateway();
        var policy = new GatewayCapabilityPolicy { InitialLearningWindow = TimeSpan.FromMinutes(5) };
        var evaluator = new GatewayCapabilityEvaluator(gw, policy);
        var now = DateTimeOffset.UtcNow;

        // First 5 minutes: timeouts
        for (var i = 1; i <= 5; i++)
        {
            evaluator.ProcessObservation(new GatewayCapabilityObservation(
                $"obs-{i}", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
                now.AddMinutes(i), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        }

        // At minute 40: first positive reply!
        var snapLate = evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-late", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now.AddMinutes(40), ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        var profile = evaluator.GetCurrentProfile();
        var icmp = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho);

        Assert.Equal(CapabilityEvidenceState.ObservedSupported, icmp.EvidenceState);
        Assert.Equal(GatewayBehaviorState.NormallyResponding, snapLate.BehaviorState);
    }

    [Fact]
    public void Current_failure_does_not_rewrite_previous_positive_observation_Invariants_48_and_49()
    {
        // Invariant 48: GATEWAY_CAPABILITY_HISTORY_IS_APPEND_ONLY
        // Invariant 49: CURRENT_GATEWAY_BEHAVIOR_NEVER_REWRITES_PRIOR_CAPABILITY_EVIDENCE
        var gw = CreateDefaultGateway();
        var evaluator = new GatewayCapabilityEvaluator(gw);
        var now = DateTimeOffset.UtcNow;

        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-1", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-2", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now.AddMinutes(1), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-3", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now.AddMinutes(2), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        Assert.Equal(3, evaluator.History.Count);

        // Snapshot 1 retains its original NormallyResponding state and ObservedSupported capability
        Assert.Equal(GatewayBehaviorState.NormallyResponding, evaluator.History[0].BehaviorState);
        Assert.Equal(CapabilityEvidenceState.ObservedSupported, evaluator.History[0].CapabilityStates.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho).EvidenceState);

        // Snapshot 3 records the new state without modifying snapshot 1
        Assert.Equal(GatewayBehaviorState.PreviouslyObservedCapabilityMissing, evaluator.History[2].BehaviorState);
        Assert.Equal(CapabilityEvidenceState.PreviouslyObserved, evaluator.History[2].CapabilityStates.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho).EvidenceState);
    }

    [Fact]
    public void ARP_success_does_not_prove_forwarding_and_Route_presence_does_not_prove_reachability_Invariants_50_and_51()
    {
        // Invariant 50: NEIGHBOR_RESOLUTION_NEVER_PROVES_GATEWAY_FORWARDING
        // Invariant 51: ROUTE_PRESENCE_NEVER_PROVES_GATEWAY_REACHABILITY
        var gw = CreateDefaultGateway();
        var evaluator = new GatewayCapabilityEvaluator(gw);
        var now = DateTimeOffset.UtcNow;

        // ARP success + Route present, but ICMP timing out
        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-arp", gw.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.ArpLookup,
            now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-route", gw.GatewayId, GatewayCapabilityKind.RoutePresence, ObservationMethod.DefaultRouteCheck,
            now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        var profile = evaluator.GetCurrentProfile();
        var arp = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.NeighborResolution);
        var route = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.RoutePresence);
        var icmp = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho);

        Assert.Equal(CapabilityEvidenceState.ObservedSupported, arp.EvidenceState);
        Assert.Equal(CapabilityEvidenceState.ObservedSupported, route.EvidenceState);
        Assert.Equal(CapabilityEvidenceState.Unknown, icmp.EvidenceState);
    }

    [Fact]
    public void IPv4_ARP_and_IPv6_NDP_are_separate_observation_methods()
    {
        var gwV4 = new GatewayIdentity("gw4", "192.168.1.1", TargetAddressFamily.IPv4, "eth0", "192.168.1.50");
        var gwV6 = new GatewayIdentity("gw6", "fe80::1", TargetAddressFamily.IPv6, "eth0", "fe80::50");

        var evalV4 = new GatewayCapabilityEvaluator(gwV4);
        var evalV6 = new GatewayCapabilityEvaluator(gwV6);
        var now = DateTimeOffset.UtcNow;

        evalV4.ProcessObservation(new GatewayCapabilityObservation(
            "obs-v4", gwV4.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.ArpLookup,
            now, ObservationOutcome.Success, gwV4.InterfaceId, gwV4.AddressFamily, Array.Empty<string>()));

        evalV6.ProcessObservation(new GatewayCapabilityObservation(
            "obs-v6", gwV6.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.NdpLookup,
            now, ObservationOutcome.Success, gwV6.InterfaceId, gwV6.AddressFamily, Array.Empty<string>()));

        Assert.Equal(CapabilityEvidenceState.ObservedSupported, evalV4.GetCurrentProfile().Capabilities.First(c => c.Kind == GatewayCapabilityKind.NeighborResolution).EvidenceState);
        Assert.Equal(CapabilityEvidenceState.ObservedSupported, evalV6.GetCurrentProfile().Capabilities.First(c => c.Kind == GatewayCapabilityKind.NeighborResolution).EvidenceState);
    }

    [Fact]
    public void Gateway_change_starts_new_capability_profile_Invariant_53()
    {
        // Invariant 53: GATEWAY_CAPABILITY_IS_SCOPED_TO_GATEWAY_IDENTITY_AND_NETWORK_CONTEXT
        var gwHome = new GatewayIdentity("gw-home", "192.168.1.1", TargetAddressFamily.IPv4, "wlan0", "192.168.1.20");
        var gwOffice = new GatewayIdentity("gw-office", "10.0.0.1", TargetAddressFamily.IPv4, "wlan0", "10.0.0.55");

        var evalHome = new GatewayCapabilityEvaluator(gwHome);
        var evalOffice = new GatewayCapabilityEvaluator(gwOffice);
        var now = DateTimeOffset.UtcNow;

        // Home gateway supports ICMP
        evalHome.ProcessObservation(new GatewayCapabilityObservation(
            "obs-1", gwHome.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now, ObservationOutcome.Success, gwHome.InterfaceId, gwHome.AddressFamily, Array.Empty<string>()));

        // Office gateway starts fresh / Unknown
        Assert.Equal(CapabilityEvidenceState.ObservedSupported, evalHome.GetCurrentProfile().Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho).EvidenceState);
        Assert.Equal(CapabilityEvidenceState.Unknown, evalOffice.GetCurrentProfile().Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho).EvidenceState);
    }

    [Fact]
    public void Previously_supported_ICMP_can_recover()
    {
        var gw = CreateDefaultGateway();
        var policy = new GatewayCapabilityPolicy { MissingCapabilityConsecutiveWindows = 1, RecoveryWindowsRequired = 2 };
        var evaluator = new GatewayCapabilityEvaluator(gw, policy);
        var now = DateTimeOffset.UtcNow;

        // 1. Observed supported
        evaluator.ProcessObservation(new GatewayCapabilityObservation("o1", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        // 2. Missing
        evaluator.ProcessObservation(new GatewayCapabilityObservation("o2", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(1), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        Assert.Equal(GatewayBehaviorState.PreviouslyObservedCapabilityMissing, evaluator.History.Last().BehaviorState);

        // 3. Recovery step 1
        evaluator.ProcessObservation(new GatewayCapabilityObservation("o3", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(2), ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        Assert.Equal(GatewayBehaviorState.Recovering, evaluator.History.Last().BehaviorState);

        // 4. Recovery step 2 -> Fully recovered
        evaluator.ProcessObservation(new GatewayCapabilityObservation("o4", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(3), ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        Assert.Equal(GatewayBehaviorState.NormallyResponding, evaluator.History.Last().BehaviorState);
        Assert.Equal(CapabilityEvidenceState.ObservedSupported, evaluator.GetCurrentProfile().Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho).EvidenceState);
    }

    [Fact]
    public void Local_probe_failure_does_not_count_as_gateway_nonresponse()
    {
        var gw = CreateDefaultGateway();
        var evaluator = new GatewayCapabilityEvaluator(gw);
        var now = DateTimeOffset.UtcNow;

        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-local-err", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing,
            now, ObservationOutcome.LocalExecutionFailure, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>(), DiagnosticMessage: "SocketError"));

        var profile = evaluator.GetCurrentProfile();
        var icmp = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho);

        Assert.Equal(CapabilityEvidenceState.Unknown, icmp.EvidenceState); // Not changed to ResponseNotYetObserved
        Assert.Equal(0, icmp.EligibleAttemptCount);
    }

    [Fact]
    public void Management_probe_not_configured_is_NotAssessed_not_Unsupported()
    {
        var gw = CreateDefaultGateway();
        var evaluator = new GatewayCapabilityEvaluator(gw);
        var now = DateTimeOffset.UtcNow;

        evaluator.ProcessObservation(new GatewayCapabilityObservation(
            "obs-mgmt", gw.GatewayId, GatewayCapabilityKind.ManagementResponse, ObservationMethod.HttpManagementProbe,
            now, ObservationOutcome.NotConfigured, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        var profile = evaluator.GetCurrentProfile();
        var mgmt = profile.Capabilities.First(c => c.Kind == GatewayCapabilityKind.ManagementResponse);

        Assert.Equal(CapabilityEvidenceState.NotAssessed, mgmt.EvidenceState);
    }

    [Fact]
    public void Deleting_derived_gateway_cache_and_rebuilding_produces_identical_profile_Invariant_54()
    {
        // Invariant 54: GATEWAY_CAPABILITY_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE
        var gw = CreateDefaultGateway();
        var policy = GatewayCapabilityPolicy.Default;
        var now = DateTimeOffset.UtcNow;

        var observations = new List<GatewayCapabilityObservation>
        {
            new("o1", gw.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.ArpLookup, now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()),
            new("o2", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(1), ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()),
            new("o3", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(2), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()),
            new("o4", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(3), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()),
        };

        var history1 = GatewayCapabilityEvaluator.RebuildHistory(gw, observations, policy);
        var history2 = GatewayCapabilityEvaluator.RebuildHistory(gw, observations, policy);

        Assert.Equal(history1.Count, history2.Count);
        for (var i = 0; i < history1.Count; i++)
        {
            Assert.Equal(history1[i].BehaviorState, history2[i].BehaviorState);
            Assert.Equal(history1[i].InterpretationRefId, history2[i].InterpretationRefId);
            Assert.Equal(history1[i].CapabilityStates.Count, history2[i].CapabilityStates.Count);
        }
    }

    [Fact]
    public void End_to_end_acceptance_scenario_T0_to_T3()
    {
        // User acceptance scenario:
        // T0: Route = present, ARP = success, ICMP = reply -> ICMP ObservedSupported, NeighborResolution observed
        // T1: Route = present, ARP = success, ICMP = timeout x 2 -> PreviouslyObservedCapabilityMissing(ICMP), NOT GatewayDown
        // T2: Route = present, ARP = failure x 2, ICMP = timeout -> Multiple signals recorded without asserting final root cause
        // T3: ARP = success, ICMP = reply -> Recovery snapshot, prior T1/T2 entries intact.
        var gw = CreateDefaultGateway();
        var policy = new GatewayCapabilityPolicy { MissingCapabilityConsecutiveWindows = 2, RecoveryWindowsRequired = 1 };
        var evaluator = new GatewayCapabilityEvaluator(gw, policy);
        var now = DateTimeOffset.UtcNow;

        // --- T0 ---
        evaluator.ProcessObservation(new GatewayCapabilityObservation("t0-r", gw.GatewayId, GatewayCapabilityKind.RoutePresence, ObservationMethod.DefaultRouteCheck, now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        evaluator.ProcessObservation(new GatewayCapabilityObservation("t0-a", gw.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.ArpLookup, now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        var snapT0 = evaluator.ProcessObservation(new GatewayCapabilityObservation("t0-i", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now, ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        Assert.Equal(GatewayBehaviorState.NormallyResponding, snapT0.BehaviorState);
        var p0 = evaluator.GetCurrentProfile();
        Assert.Equal(CapabilityEvidenceState.ObservedSupported, p0.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho).EvidenceState);
        Assert.Equal(CapabilityEvidenceState.ObservedSupported, p0.Capabilities.First(c => c.Kind == GatewayCapabilityKind.NeighborResolution).EvidenceState);

        // --- T1 ---
        evaluator.ProcessObservation(new GatewayCapabilityObservation("t1-r", gw.GatewayId, GatewayCapabilityKind.RoutePresence, ObservationMethod.DefaultRouteCheck, now.AddMinutes(1), ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        evaluator.ProcessObservation(new GatewayCapabilityObservation("t1-a", gw.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.ArpLookup, now.AddMinutes(1), ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        evaluator.ProcessObservation(new GatewayCapabilityObservation("t1-i1", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(1), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        var snapT1 = evaluator.ProcessObservation(new GatewayCapabilityObservation("t1-i2", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(2), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        Assert.Equal(GatewayBehaviorState.PreviouslyObservedCapabilityMissing, snapT1.BehaviorState);
        Assert.Contains(snapT1.ReasonCodes, r => r.Contains("IcmpEcho"));

        // --- T2 ---
        evaluator.ProcessObservation(new GatewayCapabilityObservation("t2-a1", gw.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.ArpLookup, now.AddMinutes(3), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        var snapT2 = evaluator.ProcessObservation(new GatewayCapabilityObservation("t2-a2", gw.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.ArpLookup, now.AddMinutes(4), ObservationOutcome.Timeout, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        Assert.Equal(GatewayBehaviorState.PreviouslyObservedCapabilityMissing, snapT2.BehaviorState);
        var p2 = evaluator.GetCurrentProfile();
        Assert.Equal(CapabilityEvidenceState.PreviouslyObserved, p2.Capabilities.First(c => c.Kind == GatewayCapabilityKind.IcmpEcho).EvidenceState);
        Assert.Equal(CapabilityEvidenceState.PreviouslyObserved, p2.Capabilities.First(c => c.Kind == GatewayCapabilityKind.NeighborResolution).EvidenceState);

        // --- T3 ---
        evaluator.ProcessObservation(new GatewayCapabilityObservation("t3-a", gw.GatewayId, GatewayCapabilityKind.NeighborResolution, ObservationMethod.ArpLookup, now.AddMinutes(5), ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));
        var snapT3 = evaluator.ProcessObservation(new GatewayCapabilityObservation("t3-i", gw.GatewayId, GatewayCapabilityKind.IcmpEcho, ObservationMethod.IcmpPing, now.AddMinutes(5), ObservationOutcome.Success, gw.InterfaceId, gw.AddressFamily, Array.Empty<string>()));

        Assert.Equal(GatewayBehaviorState.NormallyResponding, snapT3.BehaviorState);

        // Intact prior snapshots
        Assert.Equal(GatewayBehaviorState.NormallyResponding, evaluator.History[2].BehaviorState); // T0
        Assert.Equal(GatewayBehaviorState.PreviouslyObservedCapabilityMissing, evaluator.History[6].BehaviorState); // T1
    }
}
