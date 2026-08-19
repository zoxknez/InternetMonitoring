namespace IEM.Core.Probes;

public enum ProbeOutcomeType
{
    /// <summary>Expected reply was received within timeout (e.g. ICMP Echo Reply, TCP SYN/ACK).</summary>
    ReplyReceived,

    /// <summary>No reply was received before the specified timeout elapsed (silent drop or filtered).</summary>
    NoReplyBeforeTimeout,

    /// <summary>An explicit network error reply was received (e.g. ICMP Destination Unreachable, TTL Expired).</summary>
    DestinationUnreachable,

    /// <summary>
    /// Local execution/stack failure (socket error, permissions, out of buffers).
    /// Invariant 33: LOCAL_PROBE_FAILURE_IS_NEVER_NETWORK_LOSS.
    /// </summary>
    LocalExecutionFailure,

    /// <summary>The probe was cancelled before it could complete.</summary>
    Cancelled,
}

public enum TargetProbeType
{
    IcmpEcho,
    TcpSyn,
    UdpEcho,
    DnsQuery,
}

public enum TargetAddressFamily
{
    IPv4,
    IPv6,
}

/// <summary>
/// Individual target probe observation (Fact in Raw Evidence).
/// Invariant 38: ICMP_NO_REPLY_DOES_NOT_PROVE_PACKET_DROP_LOCATION.
/// </summary>
public sealed record TargetProbeAttempt(
    string AttemptId,
    string TargetId,
    string TargetAddress,
    TargetAddressFamily AddressFamily,
    TargetProbeType ProbeType,
    int Sequence,
    int PayloadBytes,
    DateTimeOffset StartedUtc,
    int TimeoutMs,
    ProbeOutcomeType Outcome,
    string? ReplyAddress = null,
    double? RoundTripTimeMs = null,
    string? DiagnosticMessage = null)
{
    public bool IsSuccessfulReply => Outcome == ProbeOutcomeType.ReplyReceived && RoundTripTimeMs.HasValue;
}
