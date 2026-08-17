using System.Net;
using System.Net.Sockets;
using IEM.Core.Probes;

namespace IEM.Core.Speed;

/// <summary>
/// How the route table answers about the measurement's own traffic.
/// <para>
/// Four states rather than a yes and a no, because the two ways of not being sure are
/// different findings and lead to different advice. <see cref="Unknown"/> is deliberately the
/// default value: a structure built without anybody consulting the route table describes an
/// unchecked measurement, and that has to be its resting state rather than a quiet pass.
/// </para>
/// </summary>
public enum MeasurementRouteState
{
    /// <summary>Nothing could be resolved, so nothing is known about the path.</summary>
    Unknown = 0,

    /// <summary>
    /// Every candidate address that could be resolved routes through the monitored adapter.
    /// <para>
    /// The strongest thing the route table can say, and still short of proof: it describes
    /// what the operating system would choose, not what the transfer's socket did. The
    /// wording that reaches the user says "the route table agrees with the chosen adapter"
    /// for exactly that reason.
    /// </para>
    /// </summary>
    AllResolvedRoutesMatch,

    /// <summary>Some candidates route through the monitored adapter and some through another.</summary>
    MixedRoutes,

    /// <summary>Every candidate that resolved leaves through a different adapter.</summary>
    OtherRouteOnly,
}

/// <summary>One candidate address and where the route table says traffic to it would go.</summary>
/// <param name="InterfaceId">The adapter chosen for it, or null when the route did not resolve.</param>
public sealed record RouteCandidate(IPAddress Destination, string? InterfaceId)
{
    public AddressFamily Family => Destination.AddressFamily;

    public bool Resolved => InterfaceId is not null;

    /// <summary>Whether this candidate leaves through the adapter the measurement claims.</summary>
    public bool LeavesThroughMonitoredAdapter { get; init; }
}

/// <summary>
/// What was established about the measurement's path, and from what.
/// <para>
/// The candidates are carried alongside the verdict rather than folded away, so a mixed
/// result can say which address family left through something else. "Your IPv6 route goes
/// through the VPN" is actionable; "the path is ambiguous" is not.
/// </para>
/// </summary>
public sealed record MeasurementRoute
{
    /// <summary>Nothing was checked, or nothing could be resolved.</summary>
    public static readonly MeasurementRoute Unchecked = new();

    public MeasurementRouteState State { get; init; } = MeasurementRouteState.Unknown;

    public IReadOnlyList<RouteCandidate> Candidates { get; init; } = [];

    /// <summary>Candidates whose route resolved to an adapter other than the monitored one.</summary>
    public IEnumerable<RouteCandidate> Elsewhere =>
        Candidates.Where(candidate => candidate.Resolved && !candidate.LeavesThroughMonitoredAdapter);

    /// <summary>Candidates the route table had no answer for.</summary>
    public int UnresolvedCount => Candidates.Count(candidate => !candidate.Resolved);
}

/// <summary>
/// Whether the measurement's own traffic leaves through the adapter it claims to describe.
/// <para>
/// The monitoring probes have been pinned to the monitored link since v2.2, because an
/// outage attributed to the wrong adapter is not evidence of anything - and worse, it looks
/// exactly like evidence. The speed measurement was never held to the same rule: it read
/// the named adapter's medium and port speed, then let the operating system route the
/// transfer wherever it liked, and recorded "one path, no VPN" without checking.
/// </para>
/// <para>
/// Answering this needs the route table, which is a platform matter; the rule about what
/// the answer means is here, where it can be tested.
/// </para>
/// </summary>
public static class SpeedPath
{
    /// <summary>
    /// Resolves every candidate address for the measurement host and says what they add up to.
    /// </summary>
    /// <remarks>
    /// Every candidate is resolved, with no early exit. Until 2.7 the first matching route
    /// ended the loop and the answer was "yes", on the reasoning that a host with both an
    /// IPv4 and an IPv6 address may have one of them routed through a tunnel this machine
    /// never uses. The reasoning was wrong in the case that matters: nothing stops the
    /// transfer from choosing the family that leaves through the tunnel, so a figure measured
    /// over a VPN could be filed against the Ethernet link with a clean bill of health. A
    /// split answer is now <see cref="MeasurementRouteState.MixedRoutes"/>, which is not
    /// usable for a complaint and says which family went the other way.
    /// </remarks>
    public static MeasurementRoute ResolveRoutes(
        IRouteResolver resolver,
        IReadOnlyList<IPAddress> destinations,
        string? interfaceId)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(destinations);

        if (string.IsNullOrWhiteSpace(interfaceId) || destinations.Count == 0)
        {
            return MeasurementRoute.Unchecked;
        }

        var candidates = new List<RouteCandidate>(destinations.Count);

        foreach (var destination in destinations)
        {
            var path = resolver.Resolve(destination);
            var through = path.Resolved ? path.InterfaceId : null;

            candidates.Add(new RouteCandidate(destination, through)
            {
                LeavesThroughMonitoredAdapter =
                    through is not null &&
                    string.Equals(through, interfaceId, StringComparison.OrdinalIgnoreCase),
            });
        }

        var resolved = candidates.Count(candidate => candidate.Resolved);

        if (resolved == 0)
        {
            // Nothing to reason from. Carrying the candidates anyway, so a report can say the
            // check was attempted and came back empty rather than leaving it to be assumed.
            return new MeasurementRoute { Candidates = candidates };
        }

        var matching = candidates.Count(candidate => candidate.LeavesThroughMonitoredAdapter);

        return new MeasurementRoute
        {
            State = matching == resolved
                ? MeasurementRouteState.AllResolvedRoutesMatch
                : matching == 0
                    ? MeasurementRouteState.OtherRouteOnly
                    : MeasurementRouteState.MixedRoutes,
            Candidates = candidates,
        };
    }
}
