using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Probes;

namespace IEM.Linux.Wifi;

/// <summary>
/// Linux implementation of IWirelessRadio backed by nl80211 Netlink socket and wiphy-scoped rfkill.
/// Implements Invariants 249-254:
/// - 249. RADIO_ON_UNKNOWN_NEVER_BECOMES_FALSE
/// - 250. SSID_ABSENCE_WITHOUT_FRESH_COMPLETE_SCAN_IS_UNKNOWN
/// - 251. ACTIVE_SCAN_IS_NEVER_REQUIRED_FOR_EVIDENCE_OPERATION
/// - 252. WIFI_ADAPTER_FAILURE_NEVER_BECOMES_RADIO_OFF_OR_SSID_GONE
/// - 253. SIGNAL_STRENGTH_IS_NEVER_A_CONNECTIVITY_VERDICT
/// - 254. NETWORKMANAGER_NEVER_OVERRIDES_NL80211_ASSOCIATION
/// </summary>
public sealed class LinuxNl80211Radio : IWirelessRadio, IDisposable, IAsyncDisposable
{
    private readonly ILinuxNl80211Socket _socket;
    private readonly ILinuxRfkillReader _rfkillReader;
    private readonly bool _ownsSocket;
    private GenlFamilyInfo? _cachedFamily;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LinuxNl80211Radio(
        ILinuxNl80211Socket? socket = null,
        ILinuxRfkillReader? rfkillReader = null)
    {
        _ownsSocket = socket == null;
        _socket = socket ?? LinuxNl80211Socket.Create();
        _rfkillReader = rfkillReader ?? LinuxRfkillReader.Instance;
    }

    /// <summary>
    /// Whether the radio is on for the monitored interface, according to wiphy-scoped rfkill.
    /// Invariant 249: Only positive hard/soft block evidence yields false. Everything else yields null.
    /// </summary>
    public bool? IsRadioOn(string interfaceId)
    {
        if (string.IsNullOrWhiteSpace(interfaceId))
        {
            return null;
        }

        try
        {
            var task = IsRadioOnAsync(interfaceId);
            return task.GetAwaiter().GetResult();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    public async Task<bool?> IsRadioOnAsync(string interfaceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceId))
        {
            return null;
        }

        var family = await EnsureFamilyAsync(cancellationToken).ConfigureAwait(false);
        if (family == null)
        {
            return null;
        }

        int? requestedIfIndex = int.TryParse(interfaceId, out var parsedIndex) ? parsedIndex : null;

        var interfaces = await _socket.GetInterfacesAsync(family.FamilyId, requestedIfIndex, cancellationToken).ConfigureAwait(false);
        if (interfaces == null || interfaces.Count == 0)
        {
            // Try dump lookup if single query by index returned nothing or interfaceId is name
            interfaces = await _socket.GetInterfacesAsync(family.FamilyId, null, cancellationToken).ConfigureAwait(false);
        }

        if (interfaces == null || interfaces.Count == 0)
        {
            return null;
        }

        var targetIf = interfaces.FirstOrDefault(i =>
            (requestedIfIndex.HasValue && i.IfIndex == requestedIfIndex.Value) ||
            i.IfName.Equals(interfaceId, StringComparison.OrdinalIgnoreCase));

        if (targetIf == null)
        {
            return null;
        }

        var obs = _rfkillReader.ReadObservationForWiphy(targetIf.WiphyIndex, targetIf.IfName);
        if (obs == null)
        {
            // Invariant 249: Unknown rfkill never becomes false
            return null;
        }

        if (obs.HardBlocked || obs.SoftBlocked)
        {
            // Invariant 249: Positive evidence of radio block
            return false;
        }

        return true;
    }

    private async Task<GenlFamilyInfo?> EnsureFamilyAsync(CancellationToken cancellationToken)
    {
        if (_cachedFamily != null)
        {
            return _cachedFamily;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedFamily != null)
            {
                return _cachedFamily;
            }

            _cachedFamily = await _socket.GetFamilyAsync("nl80211", cancellationToken).ConfigureAwait(false);
            return _cachedFamily;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Association reading (Phase 3.1-7B). Returns null in 7A.
    /// </summary>
    public WirelessAssociation? ReadAssociation(string interfaceId) => null;

    /// <summary>
    /// Access point details (Phase 3.1-7B). Returns null in 7A.
    /// </summary>
    public WirelessAccessPoint? ReadAccessPoint(string ssid, string bssid) => null;

    /// <summary>
    /// Scan visibility check (Phase 3.1-7C). Returns null in 7A.
    /// </summary>
    public bool? IsSsidVisible(string ssid) => null;

    /// <summary>
    /// Optional urgent scan trigger (Phase 3.1-7C). No-op in 7A.
    /// </summary>
    public void RequestUrgentScan()
    {
    }

    public void Dispose()
    {
        if (_ownsSocket)
        {
            _socket.Dispose();
        }
        _lock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsSocket)
        {
            await _socket.DisposeAsync().ConfigureAwait(false);
        }
        _lock.Dispose();
    }
}
