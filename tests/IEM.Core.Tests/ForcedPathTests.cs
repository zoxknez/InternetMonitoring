using System.Net;
using System.Net.Sockets;
using IEM.Core.Model;
using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// Verifies the 3.0-1b forced measurement path invariants and contracts.
/// <para>
/// 1. MeasurementIntent separates ObserveSystemPath from MeasureRequestedInterface.
/// 2. Forced connection without route yields NotExecuted / NoRouteFromRequestedInterface, never 0 Mbps.
/// 3. TunnelIndication is an inference with signals and detector version, separate from observation.
/// 4. ActualMeasurementPathConfirmed is established only when all observed connections match without unresolved remainder.
/// </para>
/// </summary>
public sealed class ForcedPathTests
{
    private const string Ethernet = "{ETH-0}";
    private const string Wireless = "{WLAN-1}";
    private const string WireGuard = "{WG-0}";

    private static ConnectionAttempt MakeAttempt(
        string? interfaceId,
        MeasurementIntent intent = MeasurementIntent.ObserveSystemPath) =>
        new(
            IPAddress.Parse("192.168.1.10"),
            51_000,
            IPAddress.Parse("93.184.216.34"),
            443,
            DateTimeOffset.UnixEpoch)
        {
            Intent = intent,
            Observed = interfaceId is null ? null : new NetworkInterfaceIdentity(interfaceId, interfaceId),
        };

    [Fact]
    public void ObserveSystemPath_and_MeasureRequestedInterface_are_distinct_intents()
    {
        var observedAttempt = MakeAttempt(Ethernet, MeasurementIntent.ObserveSystemPath);
        var forcedAttempt = MakeAttempt(Ethernet, MeasurementIntent.MeasureRequestedInterface);

        Assert.Equal(MeasurementIntent.ObserveSystemPath, observedAttempt.Intent);
        Assert.Equal(MeasurementIntent.MeasureRequestedInterface, forcedAttempt.Intent);
        Assert.NotEqual(observedAttempt.Intent, forcedAttempt.Intent);

        Assert.Equal("posmatranje sistemske putanje", MeasurementIntent.ObserveSystemPath.Label());
        Assert.Equal("nametnuta putanja kroz izabrani adapter", MeasurementIntent.MeasureRequestedInterface.Label());
    }

    [Fact]
    public void Forced_measurement_with_no_route_yields_NoRouteFromRequestedInterface_refusal_never_zero_mbps()
    {
        // When intent is MeasureRequestedInterface and no bytes could be transferred,
        // it must refuse with NoRouteFromRequestedInterface, never claim Ran = true with 0 Mbps.
        var refusal = ThroughputRefusal.NoRouteFromRequestedInterface;
        var result = ThroughputResult.Refused(refusal, MeasurementIntent.MeasureRequestedInterface);

        Assert.False(result.Ran);
        Assert.Equal(0, result.DownloadMbps);
        Assert.Equal(ThroughputRefusal.NoRouteFromRequestedInterface, result.Refusal);
        Assert.Equal(MeasurementIntent.MeasureRequestedInterface, result.Intent);
        Assert.Contains("nema rute sa izabranog mrežnog adaptera", refusal.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void ActualMeasurementPathConfirmed_is_true_only_when_all_connections_match_with_no_unresolved()
    {
        var perfectAgreement = PathAgreement.Of(
            Ethernet,
            [MakeAttempt(Ethernet, MeasurementIntent.MeasureRequestedInterface), MakeAttempt(Ethernet, MeasurementIntent.MeasureRequestedInterface)],
            MeasurementIntent.MeasureRequestedInterface);

        Assert.True(perfectAgreement.IsConfirmed);
        Assert.True(perfectAgreement.ActualMeasurementPathConfirmed);
        Assert.Equal(PathAgreementState.Match, perfectAgreement.State);
        Assert.Equal(0, perfectAgreement.UnresolvedCount);

        // One unresolved connection prevents confirmation
        var withUnresolved = PathAgreement.Of(
            Ethernet,
            [MakeAttempt(Ethernet), MakeAttempt(null)],
            MeasurementIntent.MeasureRequestedInterface);

        Assert.False(withUnresolved.IsConfirmed);
        Assert.False(withUnresolved.ActualMeasurementPathConfirmed);

        // Disagreement prevents confirmation
        var withMismatch = PathAgreement.Of(
            Ethernet,
            [MakeAttempt(Ethernet), MakeAttempt(Wireless)],
            MeasurementIntent.MeasureRequestedInterface);

        Assert.False(withMismatch.IsConfirmed);
        Assert.False(withMismatch.ActualMeasurementPathConfirmed);
    }

    [Fact]
    public void TunnelIndication_records_state_signals_and_detector_version_as_inference()
    {
        var tunnelSignals = new[] { "linkinfo.kind = wireguard", "DeviceType = WireGuard" };
        var tunnel = TunnelIndication.FromSignals(tunnelSignals, "Wireguard tunnel device detected");

        Assert.Equal(TunnelState.Detected, tunnel.State);
        Assert.Equal(2, tunnel.Signals.Count);
        Assert.Equal(TunnelIndication.CurrentDetectorVersion, tunnel.DetectorVersion);
        Assert.Contains("detektovan tunel/VPN", tunnel.Describe(), StringComparison.Ordinal);

        var clean = TunnelIndication.NotDetected;
        Assert.Equal(TunnelState.NotDetected, clean.State);
        Assert.Empty(clean.Signals);
        Assert.Equal("tunel/VPN nije detektovan", clean.Describe());

        var unknown = TunnelIndication.Unknown;
        Assert.Equal(TunnelState.Unknown, unknown.State);
        Assert.Equal("status tunela nije proveravan", unknown.Describe());
    }

    [Fact]
    public void SpeedMeasurementNote_records_intent_and_tunnel_beside_actual_path()
    {
        var tunnel = TunnelIndication.FromSignals(["linkinfo.kind = wireguard"]);
        var agreement = PathAgreement.Of(
            Ethernet,
            [MakeAttempt(Ethernet, MeasurementIntent.MeasureRequestedInterface)],
            MeasurementIntent.MeasureRequestedInterface,
            tunnel);

        var conditions = new SpeedMeasurementConditions(LinkMedium.Ethernet, 1_000_000_000, 100, 95)
        {
            LinkWasIdle = true,
            ConnectionHealthy = true,
            RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
            ActualPath = agreement,
        };

        var result = new ThroughputResult(95, 100_000_000, TimeSpan.FromSeconds(8), ThroughputRefusal.None)
        {
            Intent = MeasurementIntent.MeasureRequestedInterface,
            Tunnel = tunnel,
        };

        var note = SpeedMeasurementNote.From(
            DateTimeOffset.UnixEpoch,
            LinkMedium.Ethernet,
            1_000,
            conditions,
            result);

        Assert.Equal(MeasurementIntent.MeasureRequestedInterface, note.Intent);
        Assert.Equal(TunnelState.Detected, note.Tunnel.State);
        Assert.Equal(PathAgreementState.Match, note.ActualPathState);
        Assert.Single(note.Tunnel.Signals);
    }

    [Fact]
    public void AddressMap_can_find_local_address_for_interface()
    {
        var stubMap = new StubAddressLookup(Ethernet, IPAddress.Parse("192.168.1.50"));

        var found = stubMap.FindAddressForInterface(Ethernet, AddressFamily.InterNetwork);
        var notFound = stubMap.FindAddressForInterface(Wireless, AddressFamily.InterNetwork);

        Assert.NotNull(found);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), found);
        Assert.Null(notFound);
    }

    private sealed class StubAddressLookup(string interfaceId, IPAddress address) : ILocalAddressMap
    {
        public NetworkInterfaceIdentity? For(IPAddress localAddress) =>
            localAddress.Equals(address) ? new NetworkInterfaceIdentity(interfaceId, interfaceId) : null;

        public IPAddress? FindAddressForInterface(string requestedId, AddressFamily family = AddressFamily.InterNetwork) =>
            string.Equals(requestedId, interfaceId, StringComparison.OrdinalIgnoreCase) && address.AddressFamily == family
                ? address
                : null;
    }
}
