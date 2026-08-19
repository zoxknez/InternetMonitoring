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

    public LinuxRtnetlinkObserver()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _isLive = false;
            return;
        }

        try
        {
            _socket = new Socket(
                (AddressFamily)NetlinkConstants.AF_NETLINK,
                SocketType.Raw,
                (ProtocolType)NetlinkConstants.NETLINK_ROUTE);

            // Subscribe to rtnetlink multicast groups
            SubscribeGroup(NetlinkConstants.RTNLGRP_LINK);
            SubscribeGroup(NetlinkConstants.RTNLGRP_IPV4_IFADDR);
            SubscribeGroup(NetlinkConstants.RTNLGRP_IPV4_ROUTE);
            SubscribeGroup(NetlinkConstants.RTNLGRP_IPV6_IFADDR);
            SubscribeGroup(NetlinkConstants.RTNLGRP_IPV6_ROUTE);

            _isLive = true;
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), CancellationToken.None);
        }
        catch
        {
            // Graceful fallback to polling/unknown continuity if unprivileged or unsupported
            _isLive = false;
            _socket?.Dispose();
            _socket = null;
        }
    }

    private void SubscribeGroup(int group)
    {
        if (_socket is null) return;
        try
        {
            var optVal = BitConverter.GetBytes(group);
            _socket.SetSocketOption(
                (SocketOptionLevel)NetlinkConstants.SOL_NETLINK,
                (SocketOptionName)NetlinkConstants.NETLINK_ADD_MEMBERSHIP,
                optVal);
        }
        catch
        {
            // If individual group subscription is denied, the observer degrades safely
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
                // Unreliable stream or socket closed
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
                RecordChangeEvent(msgType, nowTicks);
            }

            offset += (int)((len + 3) & ~3U);
        }
    }

    internal void RecordChangeEvent(ushort msgType, long timestampTicks)
    {
        var newGen = Interlocked.Increment(ref _routeGeneration);

        var record = new NetlinkEventRecord(timestampTicks, newGen, msgType);
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

        // Scan ring buffer for events during [startedTicks, completedTicks]
        foreach (var evt in _eventLog)
        {
            if (evt.TimestampTicks >= startedTicks && evt.TimestampTicks <= completedTicks)
            {
                // A route/link/addr change event occurred during this probe's execution window
                return PathContinuity.ChangedDuringExecution;
            }
        }

        // No change events occurred during execution window on a live observer
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
        ushort MsgType);
}
