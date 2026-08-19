using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Linux.Network.Netlink;

/// <summary>
/// Dedicated Linux Netlink socket asynchronously observing kernel link, address, and routing changes.
/// Subscribes to RTNLGRP_LINK, RTNLGRP_IPV4_IFADDR, RTNLGRP_IPV4_ROUTE, RTNLGRP_IPV6_IFADDR, RTNLGRP_IPV6_ROUTE.
/// Maintains RouteGeneration and provides TOCTOU path continuity evaluation.
/// Invariants 247 &amp; §5.4, §5.7, §5.8 (Phase 3.1-4G).
/// </summary>
public sealed class LinuxRtnetlinkObserver : INetworkChangeObserver
{
    private readonly Socket? _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _listenTask;
    private readonly ConcurrentQueue<NetlinkEventRecord> _eventLog = new();
    private const int MaxEventLogCapacity = 256;

    private ulong _routeGeneration = 1;
    private volatile bool _isLive;

    public ulong RouteGeneration => Interlocked.Read(ref _routeGeneration);
    public bool IsLive => _isLive;

    public event Action<ulong>? RouteChanged;

    public LinuxRtnetlinkObserver() : this(RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
    }

    internal LinuxRtnetlinkObserver(bool isLive)
    {
        if (!isLive || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _isLive = isLive;
            return;
        }

        try
        {
            _socket = new Socket(
                (AddressFamily)NetlinkConstants.AF_NETLINK,
                SocketType.Raw,
                (ProtocolType)NetlinkConstants.NETLINK_ROUTE);

            // Subscribe to rtnetlink multicast groups
            var okLink = SubscribeGroup(NetlinkConstants.RTNLGRP_LINK);
            var okV4Addr = SubscribeGroup(NetlinkConstants.RTNLGRP_IPV4_IFADDR);
            var okV4Route = SubscribeGroup(NetlinkConstants.RTNLGRP_IPV4_ROUTE);
            var okV6Addr = SubscribeGroup(NetlinkConstants.RTNLGRP_IPV6_IFADDR);
            var okV6Route = SubscribeGroup(NetlinkConstants.RTNLGRP_IPV6_ROUTE);

            // Invariant 248: NETLINK_SUBSCRIPTION_FAILURE_NEVER_SYNTHESIZES_PATH_HELD.
            // Observer is ONLY Live if ALL 5 critical route/link/address memberships actually succeeded.
            if (okLink && okV4Route && okV4Addr && okV6Route && okV6Addr)
            {
                _isLive = true;
                _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
            }
            else
            {
                _isLive = false;
                _socket.Dispose();
                _socket = null;
            }
        }
        catch
        {
            // Graceful fallback to polling/unknown continuity if unprivileged or unsupported
            _isLive = false;
            _socket?.Dispose();
            _socket = null;
        }
    }

    private bool SubscribeGroup(int group)
    {
        if (_socket is null) return false;
        try
        {
            var optVal = BitConverter.GetBytes(group);
            _socket.SetSocketOption(
                (SocketOptionLevel)NetlinkConstants.SOL_NETLINK,
                (SocketOptionName)NetlinkConstants.NETLINK_ADD_MEMBERSHIP,
                optVal);
            return true;
        }
        catch
        {
            // If individual group subscription is denied, return false
            return false;
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        var buffer = new byte[8192];

        while (!token.IsCancellationRequested && _socket is not null)
        {
            try
            {
                var received = await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, token).ConfigureAwait(false);
                if (received < 16) continue;

                ProcessNetlinkMulticastMessage(buffer.AsSpan(0, received));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Unreliable stream or socket closed: immediately drop Live status (Invariant 248)
                _isLive = false;
                break;
            }
        }
    }

    private void ProcessNetlinkMulticastMessage(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var nowTicks = Stopwatch.GetTimestamp();

        while (offset + 16 <= data.Length)
        {
            var len = BitConverter.ToUInt32(data.Slice(offset, 4));
            if (len < 16 || offset + len > data.Length) break;

            var msgType = BitConverter.ToUInt16(data.Slice(offset + 4, 2));

            // Relevant kernel route/link/addr change events
            if (msgType is NetlinkConstants.RTM_NEWLINK or NetlinkConstants.RTM_DELLINK
                or NetlinkConstants.RTM_NEWADDR or NetlinkConstants.RTM_DELADDR
                or NetlinkConstants.RTM_NEWROUTE or NetlinkConstants.RTM_DELROUTE)
            {
                byte family = 0;
                int ifindex = 0;

                if (offset + 20 <= data.Length)
                {
                    family = data[offset + 16];
                }
                if (offset + 24 <= data.Length && msgType is NetlinkConstants.RTM_NEWLINK or NetlinkConstants.RTM_DELLINK or NetlinkConstants.RTM_NEWADDR or NetlinkConstants.RTM_DELADDR)
                {
                    ifindex = BitConverter.ToInt32(data.Slice(offset + 20, 4));
                }

                RecordChangeEvent(msgType, nowTicks, family, ifindex);
            }

            offset += (int)((len + 3) & ~3U);
        }
    }

    internal void RecordChangeEvent(ushort msgType, long timestampTicks, byte family = 0, int ifindex = 0)
    {
        var newGen = Interlocked.Increment(ref _routeGeneration);

        var record = new NetlinkEventRecord(timestampTicks, newGen, msgType, family, ifindex);
        _eventLog.Enqueue(record);

        while (_eventLog.Count > MaxEventLogCapacity && _eventLog.TryDequeue(out _)) { }

        try
        {
            RouteChanged?.Invoke(newGen);
        }
        catch
        {
            // Listener exceptions must not crash observation stream
        }
    }

    public PathContinuity EvaluateContinuity(
        long startedTicks,
        long completedTicks,
        ProbePath path,
        IPAddress? destination = null)
    {
        if (!_isLive)
        {
            return PathContinuity.Unknown;
        }

        byte targetFamily = 0;
        if (destination is not null)
        {
            targetFamily = destination.AddressFamily == AddressFamily.InterNetwork ? (byte)2 :
                           destination.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)10 : (byte)0;
        }
        else if (path.SourceAddress is not null && IPAddress.TryParse(path.SourceAddress, out var srcIp))
        {
            targetFamily = srcIp.AddressFamily == AddressFamily.InterNetwork ? (byte)2 :
                           srcIp.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)10 : (byte)0;
        }

        int.TryParse(path.InterfaceId, out var targetIfIndex);

        // Scan ring buffer for matching events during [startedTicks, completedTicks]
        foreach (var evt in _eventLog)
        {
            if (evt.TimestampTicks >= startedTicks && evt.TimestampTicks <= completedTicks)
            {
                // Family filtering: if event specifies a family, it must match the path/destination family
                if (targetFamily != 0 && evt.Family != 0 && evt.Family != targetFamily)
                {
                    continue; // Unrelated address family event (e.g. IPv6 event during IPv4 probe)
                }

                // Interface filtering: if event specifies an ifindex, and path has a specific ifindex, they must match
                if (targetIfIndex != 0 && evt.IfIndex != 0 && evt.IfIndex != targetIfIndex)
                {
                    continue; // Unrelated interface event
                }

                // A relevant route/link/addr change event occurred during this probe's execution window
                return PathContinuity.ChangedDuringExecution;
            }
        }

        // No matching change events occurred during execution window on a live observer
        return PathContinuity.Held;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _isLive = false;

        _socket?.Dispose();

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore task cancellation
            }
        }

        _cts.Dispose();
    }

    internal readonly record struct NetlinkEventRecord(
        long TimestampTicks,
        ulong Generation,
        ushort MsgType,
        byte Family = 0,
        int IfIndex = 0);
}
