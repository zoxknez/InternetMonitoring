using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using IEM.Core.Speed;

namespace IEM.Windows;

/// <summary>
/// Asks Windows whether a speed measurement will actually travel over the adapter it is
/// about to describe.
/// <para>
/// The rule about what the answer means lives in <see cref="SpeedPath"/>; this is the part
/// that needs the route table. Kept in one place because the console, the window and the
/// service all take the same measurement and must reach the same verdict about it - they
/// each used to record "one path, no VPN" without anybody checking.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class MeasurementPath
{
    /// <summary>
    /// What the route table says about traffic to the measurement host: whether every
    /// resolvable address leaves through this adapter, some of them do, none of them do, or
    /// nothing could be established.
    /// </summary>
    public static MeasurementRoute Resolve(string? interfaceId, string? url = null)
    {
        try
        {
            var host = new Uri(url ?? ThroughputOptions.Default.DownloadUrl).Host;
            var addresses = Dns.GetHostAddresses(host);

            return SpeedPath.ResolveRoutes(new RouteResolver(), addresses, interfaceId);
        }
        catch (Exception ex) when (ex is SocketException or UriFormatException or ArgumentException)
        {
            // Name resolution failing here says nothing about the routing, and the transfer
            // that follows will fail loudly on its own if the host is genuinely unreachable.
            // Unchecked rather than a verdict: the measurement will carry PathUnverified and
            // say so, which is the honest outcome of a check that could not run.
            return MeasurementRoute.Unchecked;
        }
    }
}
