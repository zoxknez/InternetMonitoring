using System.Net;
using System.Net.Sockets;

namespace IEM.Core.Model;

/// <param name="Attempted">Probes of this family that were actually sent.</param>
/// <param name="Succeeded">How many answered, regardless of how long ago.</param>
/// <param name="FreshSucceeded">
/// How many answered <em>and</em> can still be relied on. This is the only count that may
/// be used as proof that the connection works now.
/// </param>
public readonly record struct ProbeTally(int Attempted, int Succeeded, int FreshSucceeded = 0)
{
    public int Failed => Attempted - Succeeded;

    /// <summary>Nothing was attempted, so this family says nothing either way.</summary>
    public bool IsSilent => Attempted == 0;

    public bool AllFailed => Attempted > 0 && Succeeded == 0;

    public bool AllSucceeded => Attempted > 0 && Succeeded == Attempted;

    public bool AnySucceeded => Succeeded > 0;

    /// <summary>At least one answer that still describes the present.</summary>
    public bool AnyFreshSuccess => FreshSucceeded > 0;

    public bool SomeFailed => Attempted > 0 && Succeeded < Attempted;

    /// <summary>
    /// The share of this family's targets that did not answer.
    /// <para>
    /// Not a packet loss figure, and named so it cannot be mistaken for one. The external
    /// ICMP family is three <em>different destinations</em> with one probe each, so two
    /// silent ones give 66.7 % - a number that says two of three operators' networks did not
    /// answer a ping, not that two thirds of packets were dropped. Reported as loss, it put
    /// "66,7 % gubitka paketa" into complaints when the truth was often one resolver that
    /// filters ICMP by policy.
    /// </para>
    /// <para>
    /// A real loss figure needs many probes to the same destination. That is a 3.0 change.
    /// </para>
    /// </summary>
    public double UnreachableShare => Attempted == 0 ? 0d : 100d * Failed / Attempted;
}

/// <summary>
/// Everything one sampling tick observed. This is the sole input to the classifier,
/// which is why the classifier can be tested exhaustively without touching a network.
/// </summary>
public sealed record ProbeCycle(
    long Sequence,
    DateTimeOffset WallUtc,
    long MonotonicTicks,
    LinkSnapshot Link,
    IReadOnlyList<ProbeResult> Results,
    TimeSpan CycleDuration)
{
    /// <summary>True when the aggregator tick took longer than its allotted interval.</summary>
    public bool Overran { get; init; }

    /// <summary>Set while the application's own speed test is running.</summary>
    public bool SelfTestRunning { get; init; }

    /// <summary>
    /// How much traffic this machine itself was putting through the link, in bytes per
    /// second, or null when it could not be read.
    /// <para>
    /// Recorded because the connection being slow and the computer using all of it look
    /// identical from here. A download running in the background fills the buffers on the
    /// path, probes queue behind it, and what lands in the evidence is delay and loss that
    /// the operator did not cause. Measured live 17.08.: seventy seconds of an unrelated
    /// 25 MB/s download produced eighteen short "outages", some of them attributed upstream.
    /// </para>
    /// <para>
    /// Null rather than zero when the counters could not be read. The confidence score
    /// treats "we could not check" very differently from "we checked and the line was
    /// quiet", and this is exactly the reading that must not be invented.
    /// </para>
    /// </summary>
    public long? LocalTrafficBytesPerSecond { get; init; }

    /// <summary>
    /// The machine was using the connection itself heavily enough that it cannot be ruled
    /// out as a cause of the delay or loss measured in this cycle.
    /// <para>
    /// Deliberately a low bar, because of what it is used for. It does not claim the line was
    /// saturated - nothing here knows the subscribed rate, only the negotiated port speed,
    /// which on a gigabit port says nothing about a 50 Mbit service. It claims only that our
    /// own traffic is a competing explanation, and the consequence is correspondingly mild:
    /// one confidence signal stops counting as ruled out, so the report says the line was
    /// busy instead of quietly presenting the incident as clean.
    /// </para>
    /// </summary>
    public bool LocalTrafficHeavy => LocalTrafficBytesPerSecond >= HeavyLocalTrafficBytesPerSecond;

    /// <summary>
    /// Two megabytes a second - about 16 Mbit/s. Above a stream or a sync and well below
    /// what a domestic line carries, so it marks "the computer was pulling something
    /// substantial" rather than "the line was full".
    /// </summary>
    public const long HeavyLocalTrafficBytesPerSecond = 2_000_000;

    public ProbeTally Gateway => Tally(r => r.Scope == ProbeScope.Gateway && r.Kind == ProbeKind.Icmp);

    public ProbeTally ExternalIcmp => Tally(r => r.Scope == ProbeScope.External && r.Kind == ProbeKind.Icmp);

    /// <summary>
    /// Which external destinations did not answer ICMP, by name.
    /// <para>
    /// Because the count alone is misleading: "2 of 3 failed" over three different operators'
    /// resolvers is a very different finding depending on which two, and one of them
    /// answering nothing on every sample of a healthy session is a target that filters ICMP
    /// rather than a connection that drops packets.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> UnansweredExternalIcmpTargets =>
    [
        .. Results
            .Where(r => r.Scope == ProbeScope.External &&
                        r.Kind == ProbeKind.Icmp &&
                        r.Outcome != ProbeOutcome.Success)
            .Select(r => r.Target),
    ];

    public ProbeTally ExternalTcp => Tally(r => r.Scope == ProbeScope.External && r.Kind == ProbeKind.TcpConnect);

    public ProbeTally ExternalTls => Tally(r => r.Scope == ProbeScope.External && r.Kind == ProbeKind.TlsHandshake);

    public ProbeTally Http => Tally(r => r.Kind == ProbeKind.Http);

    public ProbeTally DnsIsp => Tally(r => r.Kind == ProbeKind.Dns && r.DnsRole == DnsResolverRole.IspAssigned);

    public ProbeTally DnsPublic => Tally(r => r.Kind == ProbeKind.Dns && r.DnsRole == DnsResolverRole.Public);

    public ProbeTally DnsSystem => Tally(r => r.Kind == ProbeKind.Dns && r.DnsRole == DnsResolverRole.System);

    /// <summary>
    /// The assigned resolvers of one address family, counted on their own.
    /// <para>
    /// Needed because "the assigned resolver failed while a public one answered" only means
    /// something when both questions went over the same stack. A tester on an IPv6 network
    /// had the router's own IPv6 address as his first resolver, compared against a public
    /// IPv4 resolver - two different stacks, and the difference between them was filed as a
    /// fault of the first one, permanently, on a connection that worked perfectly.
    /// </para>
    /// </summary>
    public ProbeTally DnsAssignedOf(AddressFamily family) => Tally(r =>
        r.Kind == ProbeKind.Dns && r.DnsRole == DnsResolverRole.IspAssigned && FamilyOf(r.Target) == family);

    /// <summary>The public resolver of one address family - the only fair comparison.</summary>
    public ProbeTally DnsPublicOf(AddressFamily family) => Tally(r =>
        r.Kind == ProbeKind.Dns && r.DnsRole == DnsResolverRole.Public && FamilyOf(r.Target) == family);

    /// <summary>Which families the machine's assigned resolvers actually cover.</summary>
    public IReadOnlyList<AddressFamily> AssignedResolverFamilies =>
    [
        .. Results
            .Where(r => r.Kind == ProbeKind.Dns && r.DnsRole == DnsResolverRole.IspAssigned && r.WasAttempted)
            .Select(r => FamilyOf(r.Target))
            .Where(f => f is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct(),
    ];

    private static AddressFamily FamilyOf(string target) =>
        IPAddress.TryParse(target, out var address) ? address.AddressFamily : AddressFamily.Unknown;

    /// <summary>
    /// Whether anything proved, <em>right now</em>, that packets are reaching the internet.
    /// <para>
    /// Counts only fresh successes. A handshake that succeeded before the trouble started
    /// is not evidence that the trouble is not happening - and letting it count is how a
    /// short outage vanishes from the record, reclassified as harmless filtering.
    /// </para>
    /// <para>
    /// The system resolver is excluded on purpose. It picks its own path and cannot be
    /// bound to the monitored adapter, so its success can never prove that <em>this</em>
    /// link is carrying traffic.
    /// </para>
    /// </summary>
    public bool AnyExternalReachability =>
        ExternalIcmp.AnyFreshSuccess ||
        ExternalTcp.AnyFreshSuccess ||
        ExternalTls.AnyFreshSuccess ||
        Http.AnyFreshSuccess ||
        DnsPublic.AnyFreshSuccess;

    public bool CaptivePortalSuspected => Results.Any(r => r.CaptivePortalSuspected && r.Freshness == Freshness.Fresh);

    /// <summary>
    /// The interface every resolved probe agreed it left through, or null when they
    /// disagree or none could be resolved.
    /// <para>
    /// Disagreement means traffic is leaving by more than one path, and an outage measured
    /// that way cannot be attributed to any single link.
    /// </para>
    /// </summary>
    public string? AgreedInterfaceId
    {
        get
        {
            var interfaces = Results
                .Where(r => r.WasAttempted && r.Path.Resolved && r.Path.InterfaceId is not null)
                .Select(r => r.Path.InterfaceId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

            return interfaces.Count == 1 ? interfaces[0] : null;
        }
    }

    /// <summary>
    /// The source address every resolved probe agreed on, or null when they disagree.
    /// Recorded so a reader can check the interface claim against something concrete.
    /// </summary>
    public string? AgreedSourceAddress
    {
        get
        {
            var addresses = Results
                .Where(r => r.WasAttempted && r.Path.Resolved && r.Path.SourceAddress is not null)
                .Select(r => r.Path.SourceAddress!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();

            return addresses.Count == 1 ? addresses[0] : null;
        }
    }

    /// <summary>How many probes in this cycle went out pinned to the resolved source.</summary>
    public int BoundProbeCount => Results.Count(r => r.WasAttempted && r.Path.Bound);

    /// <summary>
    /// True when attempted probes left through more than one adapter. The measurement is
    /// still recorded; it just cannot support a claim about which link failed.
    /// </summary>
    public bool MultiplePathsInUse =>
        Results.Count(r => r.WasAttempted && r.Path.Resolved && r.Path.InterfaceId is not null) > 0 &&
        AgreedInterfaceId is null;

    /// <summary>Mean round trip across fresh, successful external ICMP probes.</summary>
    public TimeSpan? AverageExternalRoundTrip
    {
        get
        {
            var samples = Results
                .Where(r => r.Scope == ProbeScope.External &&
                            r.Kind == ProbeKind.Icmp &&
                            r.ProvesReachability &&
                            r.RoundTrip.HasValue)
                .Select(r => r.RoundTrip!.Value.TotalMilliseconds)
                .ToList();

            return samples.Count == 0 ? null : TimeSpan.FromMilliseconds(samples.Average());
        }
    }

    private ProbeTally Tally(Func<ProbeResult, bool> selector)
    {
        var attempted = 0;
        var succeeded = 0;
        var fresh = 0;

        foreach (var result in Results)
        {
            if (!selector(result) || !result.WasAttempted)
            {
                continue;
            }

            attempted++;

            if (!result.Succeeded)
            {
                continue;
            }

            succeeded++;

            if (result.Freshness == Freshness.Fresh)
            {
                fresh++;
            }
        }

        return new ProbeTally(attempted, succeeded, fresh);
    }
}
