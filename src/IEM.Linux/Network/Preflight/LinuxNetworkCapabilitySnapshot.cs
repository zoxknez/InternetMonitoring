namespace IEM.Linux.Network.Preflight;

/// <summary>
/// Immutable snapshot of kernel and network execution capabilities evaluated at runtime.
/// Prevents coarse-grained global network disables and guarantees that individual probe
/// execution failures are properly scoped.
/// </summary>
public sealed record LinuxNetworkCapabilitySnapshot(
    DateTimeOffset EvaluatedAtUtc,
    LinuxCapabilityObservation IcmpDatagramIPv4,
    LinuxCapabilityObservation IcmpDatagramIPv6,
    LinuxCapabilityObservation NetlinkRouteIPv4,
    LinuxCapabilityObservation NetlinkRouteIPv6,
    LinuxCapabilityObservation SourceBindIPv4,
    LinuxCapabilityObservation SourceBindIPv6,
    LinuxCapabilityObservation TcpConnectIPv4,
    LinuxCapabilityObservation TcpConnectIPv6,
    LinuxCapabilityObservation DnsUdpIPv4,
    LinuxCapabilityObservation DnsUdpIPv6,
    string? PingGroupRangeDiagnostic)
{
    public bool CanUseIcmpV4 => IcmpDatagramIPv4.IsAvailable;
    public bool CanUseIcmpV6 => IcmpDatagramIPv6.IsAvailable;
    public bool CanUseNetlinkRoute => NetlinkRouteIPv4.IsAvailable || NetlinkRouteIPv6.IsAvailable;
    public bool CanBindSourceIPv4 => SourceBindIPv4.IsAvailable;
    public bool CanBindSourceIPv6 => SourceBindIPv6.IsAvailable;
    public bool CanConnectTcpIPv4 => TcpConnectIPv4.IsAvailable;
    public bool CanConnectTcpIPv6 => TcpConnectIPv6.IsAvailable;
    public bool CanQueryDnsIPv4 => DnsUdpIPv4.IsAvailable;
    public bool CanQueryDnsIPv6 => DnsUdpIPv6.IsAvailable;
}
