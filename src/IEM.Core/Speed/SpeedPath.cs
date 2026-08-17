using System.Net;
using IEM.Core.Probes;

namespace IEM.Core.Speed;

/// <summary>
/// Whether the measurement's own traffic leaves through the adapter it claims to describe.
/// <para>
/// The monitoring probes have been pinned to the monitored link since v2.2, because an
/// outage attributed to the wrong adapter is not evidence of anything - and worse, it looks
/// exactly like evidence. The speed measurement was never held to the same rule: it read
/// the named adapter's medium and port speed, then let the operating system route the
/// transfer wherever it liked, and recorded "one path, no VPN" without checking. On a
/// laptop with Wi-Fi, a docking station and a VPN all up at once, that produces a figure
/// labelled with one link and measured over another.
/// </para>
/// <para>
/// Answering this needs the route table, which is a platform matter; the rule about what
/// the answer means is here, where it can be tested.
/// </para>
/// </summary>
public static class SpeedPath
{
    /// <summary>
    /// Whether traffic to the measurement host would leave through <paramref name="interfaceId"/>.
    /// </summary>
    /// <returns>
    /// True when at least one resolved route leaves through that adapter, false when every
    /// resolved route leaves through another, and null when nothing could be resolved.
    /// <para>
    /// Null rather than either answer, because "we could not check" must not become "we
    /// checked and it was fine" - that is the substitution this whole tool exists to refuse.
    /// A single matching route is enough: a host with both an IPv4 and an IPv6 address may
    /// well have one of them routed through a tunnel adapter this machine never uses, and
    /// treating that as a defect would refuse perfectly good measurements.
    /// </para>
    /// </returns>
    public static bool? LeavesThroughAdapter(
        IRouteResolver resolver,
        IReadOnlyList<IPAddress> destinations,
        string? interfaceId)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(destinations);

        if (string.IsNullOrWhiteSpace(interfaceId) || destinations.Count == 0)
        {
            return null;
        }

        var resolvedAny = false;

        foreach (var destination in destinations)
        {
            var path = resolver.Resolve(destination);

            if (!path.Resolved || path.InterfaceId is not { } through)
            {
                continue;
            }

            resolvedAny = true;

            if (string.Equals(through, interfaceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return resolvedAny ? false : null;
    }
}
