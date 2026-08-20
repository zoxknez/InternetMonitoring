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
        Func<LinuxNativeNetlinkSocket>? eventSocketFactory = null,
        bool? ownsQuerySocket = null)
        : this(tracker, querySocket, eventSocketFactory, ownsQuerySocket, null)
    {
    }

    internal LinuxNl80211EventObserver(
        ILinuxWifiScanCompletionTracker tracker,
        ILinuxNativeClock? clock)
        : this(tracker, null, null, null, clock)
    {
    }

    internal LinuxNl80211EventObserver(
        ILinuxWifiScanCompletionTracker tracker,
        ILinuxNl80211Socket? querySocket,
        Func<LinuxNativeNetlinkSocket>? eventSocketFactory,
        bool? ownsQuerySocket,
        ILinuxNativeClock? clock)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _ownsQuerySocket = ownsQuerySocket ?? (querySocket == null);
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

                    LinuxWifiScanEventStatus? status = genlCmd switch
                    {
                        LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN => LinuxWifiScanEventStatus.Started,
                        LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS => LinuxWifiScanEventStatus.Completed,
                        LinuxNl80211Protocol.NL80211_CMD_SCAN_ABORTED => LinuxWifiScanEventStatus.Aborted,
                        LinuxNl80211Protocol.NL80211_CMD_START_SCHED_SCAN => LinuxWifiScanEventStatus.ScheduledStarted,
                        LinuxNl80211Protocol.NL80211_CMD_SCHED_SCAN_RESULTS => LinuxWifiScanEventStatus.ScheduledResults,
                        LinuxNl80211Protocol.NL80211_CMD_STOP_SCHED_SCAN => LinuxWifiScanEventStatus.ScheduledStopped,
                        LinuxNl80211Protocol.NL80211_CMD_SCHED_SCAN_STOPPED => LinuxWifiScanEventStatus.ScheduledStopped,
                        _ => null
                    };

                    if (status.HasValue)
                    {
                        if (TryParseScanAttributes(payload, out int ifIndex, out ulong? wdev, out LinuxWifiScanDomain domain))
                        {
                            var bootNs = LinuxWifiScanCache.TryGetCurrentBootTimeNs(_clock);
                            _tracker.RecordScanEvent(ifIndex, wdev, status.Value, bootNs, domain);
                        }
                    }
                }
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }
    }

    /// <summary>
    /// Parses scan event attributes strictly with structural validation.
    /// Invariant:
    /// - Payload must be strictly valid Netlink attributes (no truncated/trailing garbage).
    /// - IFINDEX must appear exactly once and be exactly 4 bytes, with value > 0.
    /// - WDEV must appear at most once and be exactly 8 bytes, with value != 0.
    /// - SCAN_FREQUENCIES and SCAN_SSIDS are parsed strictly. If present and valid, they build the domain; if malformed, domain is Unknown.
    /// </summary>
    public static bool TryParseScanAttributes(
        ReadOnlySpan<byte> payload,
        out int ifIndex,
        out ulong? wdev,
        out LinuxWifiScanDomain domain)
    {
        ifIndex = 0;
        wdev = null;
        domain = LinuxWifiScanDomain.Unknown;

        if (!LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out var attrs))
        {
            return false;
        }

        bool hasIfIndex = false;
        bool hasWdev = false;
        byte[]? scanFreqsBytes = null;
        byte[]? scanSsidsBytes = null;

        foreach (var (type, val) in attrs)
        {
            switch (type)
            {
                case LinuxNl80211Protocol.NL80211_ATTR_IFINDEX:
                    if (hasIfIndex || val.Length != 4)
                    {
                        return false; // Duplicate or invalid width
                    }
                    ifIndex = MemoryMarshal.Read<int>(val);
                    if (ifIndex <= 0)
                    {
                        return false;
                    }
                    hasIfIndex = true;
                    break;

                case LinuxNl80211Protocol.NL80211_ATTR_WDEV:
                    if (hasWdev || val.Length != 8)
                    {
                        return false; // Duplicate or invalid width
                    }
                    var parsedWdev = MemoryMarshal.Read<ulong>(val);
                    if (parsedWdev == 0)
                    {
                        return false;
                    }
                    wdev = parsedWdev;
                    hasWdev = true;
                    break;

                case LinuxNl80211Protocol.NL80211_ATTR_SCAN_FREQUENCIES:
                    if (scanFreqsBytes != null)
                    {
                        return false; // Duplicate scan freqs
                    }
                    scanFreqsBytes = val;
                    break;

                case LinuxNl80211Protocol.NL80211_ATTR_SCAN_SSIDS:
                    if (scanSsidsBytes != null)
                    {
                        return false; // Duplicate scan ssids
                    }
                    scanSsidsBytes = val;
                    break;
            }
        }

        if (!hasIfIndex)
        {
            return false;
        }

        // Parse Domain
        domain = ParseScanDomain(scanFreqsBytes, scanSsidsBytes);
        return true;
    }

    public static bool TryParseScanAttributes(ReadOnlySpan<byte> payload, out int ifIndex, out ulong? wdev)
    {
        return TryParseScanAttributes(payload, out ifIndex, out wdev, out _);
    }

    public static LinuxWifiScanDomain ParseScanDomain(byte[]? scanFreqsBytes, byte[]? scanSsidsBytes)
    {
        var freqScope = LinuxWifiScanFrequencyScope.Unknown;
        var ssidScope = LinuxWifiScanSsidScope.Unknown;
        var freqs = new List<uint>();
        var ssids = new List<byte[]>();

        // 1. Frequencies
        if (scanFreqsBytes != null)
        {
            if (LinuxGenlProtocol.TryEnumerateAttributesStrict(scanFreqsBytes, out var freqAttrs))
            {
                bool allFreqsValid = true;
                foreach (var (_, val) in freqAttrs)
                {
                    if (val.Length == 4)
                    {
                        freqs.Add(MemoryMarshal.Read<uint>(val));
                    }
                    else
                    {
                        allFreqsValid = false;
                        break;
                    }
                }

                if (allFreqsValid && freqs.Count > 0)
                {
                    freqScope = LinuxWifiScanFrequencyScope.AllAllowed;
                }
                else
                {
                    freqScope = LinuxWifiScanFrequencyScope.Unknown;
                }
            }
            else
            {
                freqScope = LinuxWifiScanFrequencyScope.Unknown;
            }
        }
        else
        {
            freqScope = LinuxWifiScanFrequencyScope.Unknown;
        }

        // 2. SSIDs
        if (scanSsidsBytes != null)
        {
            if (LinuxGenlProtocol.TryEnumerateAttributesStrict(scanSsidsBytes, out var ssidAttrs))
            {
                bool hasWildcard = false;
                foreach (var (_, val) in ssidAttrs)
                {
                    if (val.Length == 0)
                    {
                        hasWildcard = true;
                        ssids.Add(Array.Empty<byte>());
                    }
                    else
                    {
                        ssids.Add(val);
                    }
                }

                if (hasWildcard)
                {
                    ssidScope = LinuxWifiScanSsidScope.WildcardActive;
                }
                else if (ssids.Count > 0)
                {
                    ssidScope = LinuxWifiScanSsidScope.ExplicitSsids;
                }
                else
                {
                    ssidScope = LinuxWifiScanSsidScope.PassiveOnly;
                }
            }
            else
            {
                ssidScope = LinuxWifiScanSsidScope.Unknown;
            }
        }
        else
        {
            ssidScope = LinuxWifiScanSsidScope.PassiveOnly;
        }

        return new LinuxWifiScanDomain(freqScope, ssidScope, freqs, ssids);
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
