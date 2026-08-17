using System.Net.Sockets;

namespace IEM.Core.Model;

public enum ProbeKind
{
    Icmp,
    TcpConnect,
    TlsHandshake,
    Dns,
    Http,
}

/// <summary>Where the probe was aimed, relative to the customer's router.</summary>
public enum ProbeScope
{
    /// <summary>The local default gateway, i.e. the router itself.</summary>
    Gateway,

    /// <summary>A target beyond the gateway, out on the internet.</summary>
    External,
}

public enum ProbeOutcome
{
    Success,
    Failed,
    TimedOut,

    /// <summary>Not attempted this cycle - no IPv6, no gateway configured, and so on.</summary>
    Skipped,
}

/// <summary>
/// Which resolver answered. Splitting these apart is what separates
/// "the operator's DNS is broken" from "DNS is broken everywhere".
/// </summary>
public enum DnsResolverRole
{
    /// <summary>
    /// Whatever Windows is configured to use.
    /// <para>
    /// A diagnostic signal only. The system resolver picks its own path and cannot be bound
    /// to the monitored adapter, so it can never prove anything about which link carried
    /// the query.
    /// </para>
    /// </summary>
    System,

    /// <summary>The resolver handed out by DHCP, queried directly and bound to the monitored source.</summary>
    IspAssigned,

    /// <summary>A public resolver queried directly over the same bound source, bypassing the operator.</summary>
    Public,
}

/// <summary>
/// How much a recorded result can still be relied on.
/// <para>
/// The distinction exists because of a specific way this tool could lie. A TLS handshake
/// that succeeded five seconds ago says nothing about whether the connection works now -
/// and if it is allowed to count, a short outage disappears entirely, reclassified as
/// harmless filtering. Age alone is not enough to catch that: five seconds is well within
/// any sensible lifetime. What matters is whether the measurement predates the trouble.
/// </para>
/// </summary>
public enum Freshness
{
    /// <summary>Measured after the current suspicion began, and within its family's lifetime.</summary>
    Fresh,

    /// <summary>
    /// Validly measured, but <em>before</em> the current trouble started. Cannot be used as
    /// evidence that the connection works now.
    /// </summary>
    Stale,

    /// <summary>Older than its family's lifetime even in normal operation.</summary>
    Expired,

    /// <summary>Never measured, or not attempted.</summary>
    Unknown,
}

/// <summary>
/// Groups probes that share a measurement lifetime.
/// <para>
/// A single universal maximum age is wrong: half a second is already ancient for a gateway
/// ping and perfectly current for an HTTP fetch that costs a round trip and a handshake.
/// </para>
/// </summary>
public enum ProbeFamily
{
    GatewayIcmp,
    ExternalIcmp,
    Tcp,
    Tls,
    Dns,
    Http,
    RouterStatus,
}

public static class ProbeFamilyInfo
{
    /// <summary>
    /// How long a result of this family still describes the present.
    /// <para>
    /// A lifetime has to exceed the probe's own timeout plus its loop interval, or it fails
    /// in two ways at once. Shorter than the timeout, and every failure is marked expired
    /// the instant it is filed - a gateway ping that gives up after a second cannot be
    /// judged against half a second. Shorter than the loop interval, and perfectly healthy
    /// results spend most of their life expired between rounds.
    /// </para>
    /// <para>
    /// So these are deliberately generous. They are the coarse backstop against a
    /// measurement nobody refreshed. The sharp instrument is the suspicion rule in
    /// <c>ObservationStore</c>, which discards anything measured before the trouble began
    /// no matter how recent it is.
    /// </para>
    /// </summary>
    public static TimeSpan Lifetime(this ProbeFamily family) => family switch
    {
        // 1 s timeout, loop up to 1 s while healthy.
        ProbeFamily.GatewayIcmp => TimeSpan.FromSeconds(3),
        ProbeFamily.ExternalIcmp => TimeSpan.FromSeconds(3),

        // 2 s timeout, and during a full outage the loop period is the timeout itself.
        ProbeFamily.Tcp => TimeSpan.FromSeconds(6),

        // Expensive, run rarely, and what they measure does not change moment to moment.
        ProbeFamily.Tls => TimeSpan.FromSeconds(45),
        ProbeFamily.Dns => TimeSpan.FromSeconds(45),
        ProbeFamily.Http => TimeSpan.FromSeconds(45),

        ProbeFamily.RouterStatus => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromSeconds(10),
    };

    /// <summary>Which family a probe belongs to.</summary>
    public static ProbeFamily FamilyOf(ProbeKind kind, ProbeScope scope) => kind switch
    {
        ProbeKind.Icmp when scope == ProbeScope.Gateway => ProbeFamily.GatewayIcmp,
        ProbeKind.Icmp => ProbeFamily.ExternalIcmp,
        ProbeKind.TcpConnect => ProbeFamily.Tcp,
        ProbeKind.TlsHandshake => ProbeFamily.Tls,
        ProbeKind.Dns => ProbeFamily.Dns,
        ProbeKind.Http => ProbeFamily.Http,
        _ => ProbeFamily.ExternalIcmp,
    };
}

/// <summary>
/// Which way out of the machine a probe actually took.
/// <para>
/// Recorded per probe rather than assumed once for the session, because Windows decides
/// this per destination: destination, then route, then interface, then source address. On
/// a machine with Wi-Fi, Ethernet and a VPN, two probes aimed at different addresses can
/// leave through different adapters - and an outage attributed to the wrong link is not
/// evidence of anything.
/// </para>
/// </summary>
/// <param name="Resolved">
/// The route was determined: this is the interface and source address Windows itself picks
/// for this destination. False when the lookup failed, in which case the measurement still
/// counts but cannot support an attribution.
/// </param>
/// <param name="Bound">
/// The probe was forced onto that source address rather than merely predicted to use it.
/// A resolved-but-unbound probe is a good prediction; a bound one is a fact.
/// </param>
public readonly record struct ProbePath(string? InterfaceId, string? SourceAddress, bool Resolved, bool Bound = false)
{
    public static readonly ProbePath Unresolved = new(null, null, false);

    /// <summary>Strong enough to attribute a fault to one link.</summary>
    public bool ProvesLink => Resolved && InterfaceId is not null;
}

/// <summary>Outcome of a single probe.</summary>
public sealed record ProbeResult(
    ProbeKind Kind,
    ProbeScope Scope,
    string Target,
    ProbeOutcome Outcome,
    TimeSpan? RoundTrip,
    string? Detail = null)
{
    public bool Succeeded => Outcome == ProbeOutcome.Success;

    /// <summary>A skipped probe is neither evidence of failure nor of success.</summary>
    public bool WasAttempted => Outcome != ProbeOutcome.Skipped;

    public DnsResolverRole? DnsRole { get; init; }

    public AddressFamily? Family { get; init; }

    /// <summary>
    /// Set by the HTTP probe when the response was reachable but not what the
    /// connectivity endpoint should return, which is the signature of an intercepting portal.
    /// </summary>
    public bool CaptivePortalSuspected { get; init; }

    // ---- Timing and trust ---------------------------------------------------

    /// <summary>Monotonic tick at which the probe was sent.</summary>
    public long StartedAtTicks { get; init; }

    /// <summary>Monotonic tick at which the answer arrived or the attempt gave up.</summary>
    public long CompletedAtTicks { get; init; }

    /// <summary>How old the result was when it was read.</summary>
    public TimeSpan Age { get; init; }

    /// <summary>
    /// Whether this result can still be relied on. Defaults to <see cref="Freshness.Unknown"/>
    /// so a result constructed without going through the store never silently counts as proof.
    /// </summary>
    public Freshness Freshness { get; init; } = Freshness.Unknown;

    /// <summary>Only a fresh success proves the connection works right now.</summary>
    public bool ProvesReachability => Succeeded && Freshness == Freshness.Fresh;

    /// <summary>Which way out of the machine this probe went.</summary>
    public ProbePath Path { get; init; } = ProbePath.Unresolved;

    public ProbeFamily ProbeFamily => ProbeFamilyInfo.FamilyOf(Kind, Scope);

    public static ProbeResult Skip(ProbeKind kind, ProbeScope scope, string target, string reason) =>
        new(kind, scope, target, ProbeOutcome.Skipped, null, reason);
}
