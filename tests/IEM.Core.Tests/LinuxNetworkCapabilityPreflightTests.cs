using System.Net.Sockets;
using IEM.Linux.Network.Preflight;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and deterministic tests for Linux Network Capability & Probe Preflight.
/// Invariants 271-275 (Phase 3.1-4A).
/// </summary>
public sealed class LinuxNetworkCapabilityPreflightTests
{
    [Fact]
    public void MapSocketErrorToCapabilityState_maps_access_denied_to_unavailable()
    {
        // EPERM / EACCES -> Unavailable (capability denied, but NOT network outage)
        var state = LinuxNetworkCapabilityPreflight.MapSocketErrorToCapabilityState(SocketError.AccessDenied);
        Assert.Equal(LinuxCapabilityState.Unavailable, state);
    }

    [Fact]
    public void MapSocketErrorToCapabilityState_maps_unsupported_protocols_to_unsupported()
    {
        Assert.Equal(
            LinuxCapabilityState.Unsupported,
            LinuxNetworkCapabilityPreflight.MapSocketErrorToCapabilityState(SocketError.AddressFamilyNotSupported));

        Assert.Equal(
            LinuxCapabilityState.Unsupported,
            LinuxNetworkCapabilityPreflight.MapSocketErrorToCapabilityState(SocketError.ProtocolNotSupported));

        Assert.Equal(
            LinuxCapabilityState.Unsupported,
            LinuxNetworkCapabilityPreflight.MapSocketErrorToCapabilityState(SocketError.ProtocolType));

        Assert.Equal(
            LinuxCapabilityState.Unsupported,
            LinuxNetworkCapabilityPreflight.MapSocketErrorToCapabilityState(SocketError.OperationNotSupported));
    }

    [Fact]
    public void MapSocketErrorToCapabilityState_maps_unknown_errors_conservatively_to_unknown()
    {
        var state = LinuxNetworkCapabilityPreflight.MapSocketErrorToCapabilityState(SocketError.Fault);
        Assert.Equal(LinuxCapabilityState.Unknown, state);
    }

    [Fact]
    public void Snapshot_capabilities_are_granular_and_independent()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new LinuxNetworkCapabilitySnapshot(
            EvaluatedAtUtc: now,
            IcmpDatagramIPv4: new LinuxCapabilityObservation(LinuxCapabilityState.Available, NativeError: 0),
            IcmpDatagramIPv6: new LinuxCapabilityObservation(LinuxCapabilityState.Unavailable, NativeError: 1, SocketError: "AccessDenied", Diagnostic: "Permission denied"),
            NetlinkRouteIPv4: new LinuxCapabilityObservation(LinuxCapabilityState.Available),
            NetlinkRouteIPv6: new LinuxCapabilityObservation(LinuxCapabilityState.Available),
            SourceBindIPv4: new LinuxCapabilityObservation(LinuxCapabilityState.Available),
            SourceBindIPv6: new LinuxCapabilityObservation(LinuxCapabilityState.Available),
            TcpConnectIPv4: new LinuxCapabilityObservation(LinuxCapabilityState.Available),
            TcpConnectIPv6: new LinuxCapabilityObservation(LinuxCapabilityState.Available),
            DnsUdpIPv4: new LinuxCapabilityObservation(LinuxCapabilityState.Available),
            DnsUdpIPv6: new LinuxCapabilityObservation(LinuxCapabilityState.Available),
            PingGroupRangeDiagnostic: "0 2147483647");

        Assert.True(snapshot.CanUseIcmpV4);
        Assert.False(snapshot.CanUseIcmpV6);
        Assert.True(snapshot.CanUseNetlinkRoute);
        Assert.True(snapshot.CanBindSourceIPv4);
        Assert.True(snapshot.CanBindSourceIPv6);
        Assert.True(snapshot.CanConnectTcpIPv4);
        Assert.True(snapshot.CanQueryDnsIPv4);
        Assert.Equal("0 2147483647", snapshot.PingGroupRangeDiagnostic);
    }

    [Fact]
    public void Preflight_evaluate_runs_and_returns_complete_snapshot()
    {
        var snapshot = LinuxNetworkCapabilityPreflight.Evaluate();

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.IcmpDatagramIPv4);
        Assert.NotNull(snapshot.IcmpDatagramIPv6);
        Assert.NotNull(snapshot.NetlinkRouteIPv4);
        Assert.NotNull(snapshot.NetlinkRouteIPv6);
        Assert.NotNull(snapshot.SourceBindIPv4);
        Assert.NotNull(snapshot.SourceBindIPv6);
        Assert.True(snapshot.EvaluatedAtUtc <= DateTimeOffset.UtcNow);
    }
}
