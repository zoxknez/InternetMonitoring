using System.Net;
using IEM.Core.Model;

namespace IEM.Core.Probes;

/// <summary>
/// Works out which adapter and source address the operating system will use to reach a
/// given destination.
/// <para>
/// Per destination, deliberately. It is tempting to decide once that "the monitored adapter
/// is the Wi-Fi" and assume every probe leaves through it, but that is not how the decision
/// is made: the route table is consulted per destination, so on a machine with Wi-Fi,
/// Ethernet and a VPN up at once, three probes can leave through three different adapters.
/// An outage attributed to the wrong link is not evidence of anything, and worse, it looks
/// exactly like evidence.
/// </para>
/// </summary>
public interface IRouteResolver
{
    /// <summary>
    /// The path traffic to <paramref name="destination"/> will take, or
    /// <see cref="ProbePath.Unresolved"/> when it cannot be determined.
    /// </summary>
    ProbePath Resolve(IPAddress destination);
}

/// <summary>
/// Used where routing cannot be inspected. Every probe reports an unresolved path, which
/// costs the ability to attribute a fault to a specific link and nothing else - the
/// measurements themselves are unaffected.
/// </summary>
public sealed class NullRouteResolver : IRouteResolver
{
    public static readonly NullRouteResolver Instance = new();

    public ProbePath Resolve(IPAddress destination) => ProbePath.Unresolved;
}

/// <param name="Status">Platform status code, carried through for the raw log.</param>
public readonly record struct IcmpEcho(bool Succeeded, bool TimedOut, TimeSpan RoundTrip, uint Status);

/// <summary>
/// Sends an ICMP echo from a chosen source address.
/// <para>
/// Separate from the framework's ping because the framework has no way to say which adapter
/// to leave through. Without that, an outage measured on a laptop with a docking station
/// attached can describe a link that was never under test.
/// </para>
/// </summary>
public interface IBoundIcmp
{
    /// <summary>
    /// Sends one echo request, or returns null when this platform cannot bind the source -
    /// in which case the caller falls back and records the result as unbound rather than
    /// claiming a binding it did not get.
    /// </summary>
    Task<IcmpEcho?> SendAsync(
        IPAddress destination,
        IPAddress source,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
