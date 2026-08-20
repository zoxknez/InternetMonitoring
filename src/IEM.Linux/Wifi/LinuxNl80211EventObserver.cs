using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using IEM.Linux.Network.Netlink;
using IEM.Linux.Time;

namespace IEM.Linux.Wifi;

public interface ILinuxNl80211EventObserver : IDisposable, IAsyncDisposable
{
    bool IsRunning { get; }
    bool IsListening { get; }
    uint? ScanMulticastGroupId { get; }
    ushort? Nl80211FamilyId { get; }
    void Start();
    void Stop();
}

/// <summary>
/// Dedicated Generic Netlink event observer listening to the nl80211 "scan" multicast group.
/// Receives kernel NL80211_CMD_NEW_SCAN_RESULTS and NL80211_CMD_SCAN_ABORTED notifications
/// and updates the shared <see cref="ILinuxWifiScanCompletionTracker"/> with affirmative,
/// adapter-scoped scan completion provenance.
/// Invariants 249-254, 258.
/// </summary>
public sealed class LinuxNl80211EventObserver : ILinuxNl80211EventObserver
{
    private readonly ILinuxWifiScanCompletionTracker _tracker;
    private readonly ILinuxNl80211Socket _querySocket;
    private readonly bool _ownsQuerySocket;
    private readonly Func<LinuxNativeNetlinkSocket>? _eventSocketFactory;
    private readonly ILinuxNativeClock? _clock;
    private readonly CancellationTokenSource _cts = new();

    private LinuxNativeNetlinkSocket? _eventSocket;
    private Task? _receiveTask;
    private ushort? _familyId;
    private uint? _scanGroupId;
    private volatile bool _isListening;
    private volatile bool _isRunning;
    private bool _disposed;

    public LinuxNl80211EventObserver(
        ILinuxWifiScanCompletionTracker tracker,
        ILinuxNl80211Socket? querySocket = null,
        Func<LinuxNativeNetlinkSocket>? eventSocketFactory = null)
        : this(tracker, querySocket, eventSocketFactory, null)
    {
    }

    internal LinuxNl80211EventObserver(
        ILinuxWifiScanCompletionTracker tracker,
        ILinuxNativeClock? clock)
        : this(tracker, null, null, clock)
    {
    }

    internal LinuxNl80211EventObserver(
        ILinuxWifiScanCompletionTracker tracker,
        ILinuxNl80211Socket? querySocket,
        Func<LinuxNativeNetlinkSocket>? eventSocketFactory,
        ILinuxNativeClock? clock)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _ownsQuerySocket = querySocket == null;
        _querySocket = querySocket ?? LinuxNl80211Socket.Create();
        _eventSocketFactory = eventSocketFactory;
        _clock = clock;
    }

    public bool IsRunning => _isRunning;
    public bool IsListening => _isListening;
    public uint? ScanMulticastGroupId => _scanGroupId;
    public ushort? Nl80211FamilyId => _familyId;

    public void Start()
    {
        if (_disposed || _isRunning) return;

        _isRunning = true;
        _receiveTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _isListening = false;
        _cts.Cancel();

        try
        {
            _eventSocket?.Close();
        }
        catch
        {
            // Best effort close
        }

        try
        {
            _receiveTask?.GetAwaiter().GetResult();
        }
        catch
        {
            // Suppress task cancellation exceptions
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && _eventSocketFactory == null)
        {
            _isRunning = false;
            return;
        }

        try
        {
            // 1. Resolve nl80211 family and "scan" multicast group ID dynamically
            var family = await _querySocket.GetFamilyAsync("nl80211", cancellationToken).ConfigureAwait(false);
            if (family == null || family.FamilyId == 0)
            {
                _isRunning = false;
                return;
            }

            _familyId = family.FamilyId;

            if (!family.MulticastGroups.TryGetValue("scan", out var scanGroupId) || scanGroupId == 0)
            {
                // Kernel driver does not support "scan" multicast group
                _isRunning = false;
                return;
            }

            _scanGroupId = scanGroupId;

            // 2. Open dedicated event fd
            _eventSocket = _eventSocketFactory != null
                ? _eventSocketFactory()
                : LinuxNativeNetlinkSocket.Open(LinuxGenlProtocol.NETLINK_GENERIC);

            // 3. Join "scan" multicast group
            _eventSocket.JoinMulticastGroup(scanGroupId);
            _isListening = true;

            // 4. Multicast receive loop
            var buffer = new byte[8192];
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                int bytesRead;
                try
                {
                    bytesRead = _eventSocket.Receive(buffer, timeoutMs: 1000);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception)
                {
                    if (cancellationToken.IsCancellationRequested || !_isRunning) break;
                    // On socket error, retry or break gracefully
                    break;
                }

                if (bytesRead <= 0)
                {
                    continue;
                }

                ProcessEventPayload(buffer.AsSpan(0, bytesRead), _familyId.Value);
            }
        }
        catch
        {
            // Fail-closed: observer failure leaves tracker opportunistic (no false negative proof)
        }
        finally
        {
            _isListening = false;
            _isRunning = false;
        }
    }

    public void ProcessEventPayload(ReadOnlySpan<byte> buffer, ushort nl80211FamilyId)
    {
        int offset = 0;
        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                break; // Corrupted or truncated message
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));

            // Only process messages matching the resolved nl80211 family ID
            if (nlmsgType == nl80211FamilyId)
            {
                if (nlmsgLen >= LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize)
                {
                    byte genlCmd = buffer[offset + LinuxGenlProtocol.NlmsgHeaderSize];
                    var payload = buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize,
                                               nlmsgLen - (LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize));

                    if (genlCmd is LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS or LinuxNl80211Protocol.NL80211_CMD_SCAN_ABORTED)
                    {
                        var status = genlCmd == LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS
                            ? LinuxWifiScanEventStatus.Completed
                            : LinuxWifiScanEventStatus.Aborted;

                        if (TryParseScanAttributes(payload, out int ifIndex, out ulong? wdev))
                        {
                            var bootNs = LinuxWifiScanCache.TryGetCurrentBootTimeNs(_clock);
                            _tracker.RecordScanEvent(ifIndex, wdev, status, bootNs);
                        }
                    }
                }
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }
    }

    /// <summary>
    /// Parses scan event attributes strictly extracting NL80211_ATTR_IFINDEX and NL80211_ATTR_WDEV.
    /// </summary>
    public static bool TryParseScanAttributes(ReadOnlySpan<byte> payload, out int ifIndex, out ulong? wdev)
    {
        ifIndex = 0;
        wdev = null;

        var attrs = LinuxGenlProtocol.EnumerateAttributes(payload);
        foreach (var (type, val) in attrs)
        {
            switch (type)
            {
                case LinuxNl80211Protocol.NL80211_ATTR_IFINDEX:
                    if (val.Length >= 4)
                    {
                        ifIndex = MemoryMarshal.Read<int>(val);
                    }
                    break;

                case LinuxNl80211Protocol.NL80211_ATTR_WDEV:
                    if (val.Length >= 8)
                    {
                        wdev = MemoryMarshal.Read<ulong>(val);
                    }
                    break;
            }
        }

        return ifIndex > 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _cts.Dispose();
        _eventSocket?.Dispose();
        if (_ownsQuerySocket)
        {
            _querySocket.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }
}
