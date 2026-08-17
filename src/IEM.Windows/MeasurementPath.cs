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
    /// True when traffic to the measurement host leaves through this adapter, false when it
    /// demonstrably leaves through another, null when it could not be established.
    /// </summary>
    public static bool? LeavesThroughAdapter(string? interfaceId, string? url = null)
    {
        try
        {
            var host = new Uri(url ?? ThroughputOptions.Default.DownloadUrl).Host;
            var addresses = Dns.GetHostAddresses(host);

            return SpeedPath.LeavesThroughAdapter(new RouteResolver(), addresses, interfaceId);
        }
        catch (Exception ex) when (ex is SocketException or UriFormatException or ArgumentException)
        {
            // Name resolution failing here says nothing about the routing, and the transfer
            // that follows will fail loudly on its own if the host is genuinely unreachable.
            return null;
        }
    }
}
