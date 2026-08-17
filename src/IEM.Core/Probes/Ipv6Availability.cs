using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IEM.Core.Time;

namespace IEM.Core.Probes;

/// <summary>
/// Whether this machine has a routable IPv6 address, answered from a short-lived cache.
/// <para>
/// Checked rather than assumed, because most Serbian home connections are IPv4-only and
/// probing IPv6 there produces a steady stream of failures indistinguishable from real
/// packet loss - manufacturing incidents out of a network working exactly as configured.
/// </para>
/// <para>
/// Cached because the answer changes rarely and finding it does not: it enumerates every
/// adapter and every address on the machine. Asked once per sample at a tenth of a second,
/// that is ten full enumerations a second inside the hot path.
/// </para>
/// </summary>
public static class Ipv6Availability
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly Lock Gate = new();

    private static bool _available;
    private static long _checkedAtTicks;
    private static bool _everChecked;

    public static bool IsAvailable(IClock? clock = null)
    {
        var resolved = clock ?? SystemClock.Instance;

        lock (Gate)
        {
            if (_everChecked && resolved.MonotonicElapsedSince(_checkedAtTicks) < CacheLifetime)
            {
                return _available;
            }

            _available = Detect();
            _checkedAtTicks = resolved.MonotonicTicks;
            _everChecked = true;
            return _available;
        }
    }

    /// <summary>Forgets the cached answer. For tests, and for when the link changes.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _everChecked = false;
        }
    }

    private static bool Detect()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                          !a.Address.IsIPv6LinkLocal &&
                          !a.Address.IsIPv6SiteLocal &&
                          !IPAddress.IsLoopback(a.Address));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
