using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IEM.Linux.Network.Preflight;

/// <summary>
/// Preflight execution prober that evaluates actual kernel and socket capabilities.
/// Invariants 271-275:
/// 1. Actual socket creation is the sole authority (never ping_group_range).
/// 2. Granular evaluation per protocol and address family (IPv4 ICMP != IPv6 ICMP != TCP != DNS).
/// 3. Local capability failures NEVER produce network outages.
/// </summary>
public static class LinuxNetworkCapabilityPreflight
{
    private const int AF_NETLINK = 16;
    private const int NETLINK_ROUTE = 0;
    private const string PingGroupRangePath = "/proc/sys/net/ipv4/ping_group_range";

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(10);
    private static LinuxNetworkCapabilitySnapshot? _cachedSnapshot;
    private static long _lastEvaluatedTicks = long.MinValue;
    private static readonly object _syncLock = new();

    public static LinuxNetworkCapabilitySnapshot GetOrEvaluate(bool forceRecheck = false)
    {
        lock (_syncLock)
        {
            var nowTicks = Stopwatch.GetTimestamp();
            if (!forceRecheck && _cachedSnapshot is not null && Stopwatch.GetElapsedTime(_lastEvaluatedTicks, nowTicks) < ReconcileInterval)
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = Evaluate();
            _lastEvaluatedTicks = nowTicks;
            return _cachedSnapshot;
        }
    }

    public static void NotifyCapabilityDenied()
    {
        lock (_syncLock)
        {
            _cachedSnapshot = null;
            _lastEvaluatedTicks = long.MinValue;
        }
    }

    public static LinuxNetworkCapabilitySnapshot Evaluate()
    {
        var evaluatedAt = DateTimeOffset.UtcNow;
        var pingGroupDiag = ReadPingGroupRange();

        var icmpV4 = ProbeIcmpDatagram(AddressFamily.InterNetwork, ProtocolType.Icmp);
        var icmpV6 = ProbeIcmpDatagram(AddressFamily.InterNetworkV6, ProtocolType.IcmpV6);
        var netlinkV4 = ProbeNetlinkRoute(AddressFamily.InterNetwork);
        var netlinkV6 = ProbeNetlinkRoute(AddressFamily.InterNetworkV6);
        var bindV4 = ProbeSourceBind(AddressFamily.InterNetwork, IPAddress.Loopback);
        var bindV6 = ProbeSourceBind(AddressFamily.InterNetworkV6, IPAddress.IPv6Loopback);
        var tcpV4 = ProbeTcpSocket(AddressFamily.InterNetwork);
        var tcpV6 = ProbeTcpSocket(AddressFamily.InterNetworkV6);
        var dnsV4 = ProbeDnsUdpSocket(AddressFamily.InterNetwork);
        var dnsV6 = ProbeDnsUdpSocket(AddressFamily.InterNetworkV6);

        return new LinuxNetworkCapabilitySnapshot(
            evaluatedAt,
            icmpV4,
            icmpV6,
            netlinkV4,
            netlinkV6,
            bindV4,
            bindV6,
            tcpV4,
            tcpV6,
            dnsV4,
            dnsV6,
            pingGroupDiag);
    }

    private static LinuxCapabilityObservation ProbeIcmpDatagram(AddressFamily family, ProtocolType protocol)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Dgram, protocol);
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Available,
                NativeError: 0,
                SocketError: null,
                Diagnostic: $"Unprivileged datagram ICMP socket creation succeeded for {family}.");
        }
        catch (SocketException ex)
        {
            var state = MapSocketErrorToCapabilityState(ex.SocketErrorCode);
            return new LinuxCapabilityObservation(
                state,
                NativeError: ex.ErrorCode,
                SocketError: ex.SocketErrorCode.ToString(),
                Diagnostic: $"Datagram ICMP socket creation failed for {family}: {ex.Message} (ErrorCode: {ex.ErrorCode}, SocketErrorCode: {ex.SocketErrorCode})");
        }
        catch (Exception ex)
        {
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Unknown,
                NativeError: null,
                SocketError: null,
                Diagnostic: $"Unexpected exception during {family} ICMP probe: {ex.Message}");
        }
    }

    private static LinuxCapabilityObservation ProbeNetlinkRoute(AddressFamily family)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Unsupported,
                Diagnostic: "AF_NETLINK is only supported on Linux.");
        }

        try
        {
            using var socket = new Socket((AddressFamily)AF_NETLINK, SocketType.Raw, (ProtocolType)NETLINK_ROUTE);
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Available,
                NativeError: 0,
                SocketError: null,
                Diagnostic: $"AF_NETLINK NETLINK_ROUTE socket creation succeeded for {family}.");
        }
        catch (SocketException ex)
        {
            var state = MapSocketErrorToCapabilityState(ex.SocketErrorCode);
            return new LinuxCapabilityObservation(
                state,
                NativeError: ex.ErrorCode,
                SocketError: ex.SocketErrorCode.ToString(),
                Diagnostic: $"AF_NETLINK socket creation failed: {ex.Message} (ErrorCode: {ex.ErrorCode})");
        }
        catch (Exception ex)
        {
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Unknown,
                Diagnostic: $"Unexpected exception during Netlink probe: {ex.Message}");
        }
    }

    private static LinuxCapabilityObservation ProbeSourceBind(AddressFamily family, IPAddress bindAddress)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(bindAddress, 0));
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Available,
                NativeError: 0,
                SocketError: null,
                Diagnostic: $"Local source address binding succeeded for {family}.");
        }
        catch (SocketException ex)
        {
            var state = MapSocketErrorToCapabilityState(ex.SocketErrorCode);
            return new LinuxCapabilityObservation(
                state,
                NativeError: ex.ErrorCode,
                SocketError: ex.SocketErrorCode.ToString(),
                Diagnostic: $"Source address bind failed for {family}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Unknown,
                Diagnostic: $"Unexpected exception during {family} source bind probe: {ex.Message}");
        }
    }

    private static LinuxCapabilityObservation ProbeTcpSocket(AddressFamily family)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Available,
                NativeError: 0,
                SocketError: null,
                Diagnostic: $"TCP stream socket creation succeeded for {family}.");
        }
        catch (SocketException ex)
        {
            var state = MapSocketErrorToCapabilityState(ex.SocketErrorCode);
            return new LinuxCapabilityObservation(
                state,
                NativeError: ex.ErrorCode,
                SocketError: ex.SocketErrorCode.ToString(),
                Diagnostic: $"TCP socket creation failed for {family}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Unknown,
                Diagnostic: $"Unexpected exception during {family} TCP probe: {ex.Message}");
        }
    }

    private static LinuxCapabilityObservation ProbeDnsUdpSocket(AddressFamily family)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Available,
                NativeError: 0,
                SocketError: null,
                Diagnostic: $"UDP socket creation succeeded for {family}.");
        }
        catch (SocketException ex)
        {
            var state = MapSocketErrorToCapabilityState(ex.SocketErrorCode);
            return new LinuxCapabilityObservation(
                state,
                NativeError: ex.ErrorCode,
                SocketError: ex.SocketErrorCode.ToString(),
                Diagnostic: $"UDP socket creation failed for {family}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new LinuxCapabilityObservation(
                LinuxCapabilityState.Unknown,
                Diagnostic: $"Unexpected exception during {family} UDP probe: {ex.Message}");
        }
    }

    public static LinuxCapabilityState MapSocketErrorToCapabilityState(SocketError error)
    {
        return error switch
        {
            SocketError.Success => LinuxCapabilityState.Available,
            SocketError.AccessDenied => LinuxCapabilityState.Unavailable, // EPERM / EACCES
            SocketError.AddressFamilyNotSupported => LinuxCapabilityState.Unsupported,
            SocketError.ProtocolNotSupported => LinuxCapabilityState.Unsupported,
            SocketError.ProtocolType => LinuxCapabilityState.Unsupported,
            SocketError.OperationNotSupported => LinuxCapabilityState.Unsupported,
            _ => LinuxCapabilityState.Unknown
        };
    }

    private static string? ReadPingGroupRange()
    {
        try
        {
            if (File.Exists(PingGroupRangePath))
            {
                return File.ReadAllText(PingGroupRangePath).Trim();
            }
        }
        catch
        {
            // Non-critical diagnostic read
        }

        return null;
    }
}
