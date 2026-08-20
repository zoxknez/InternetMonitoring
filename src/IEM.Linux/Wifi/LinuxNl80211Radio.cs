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

        if (targetIf == null || !targetIf.WiphyIndex.HasValue)
        {
            // Invariant 249: Unmapped interface or missing WIPHY attribute cannot be attributed to phy0
            return null;
        }

        var obs = _rfkillReader.ReadObservationForWiphy(targetIf.WiphyIndex.Value, targetIf.IfName);
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
    /// Captures full evidence-grade wireless association observation for the specified interface.
    /// Invariants 255-262: Evaluates cached BSS results without active scanning, preserves MLO links.
    /// </summary>
    public async Task<LinuxWirelessAssociationObservation?> ReadAssociationObservationAsync(
        string interfaceId,
        CancellationToken cancellationToken = default)
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

        var ifDump = await _socket.DumpInterfacesAsync(family.FamilyId, requestedIfIndex, cancellationToken).ConfigureAwait(false);
        if (!ifDump.IsComplete || ifDump.Items.Count == 0)
        {
            ifDump = await _socket.DumpInterfacesAsync(family.FamilyId, null, cancellationToken).ConfigureAwait(false);
        }

        if (!ifDump.IsComplete || ifDump.Items.Count == 0)
        {
            return null;
        }

        var targetIf = ifDump.Items.FirstOrDefault(i =>
            (requestedIfIndex.HasValue && i.IfIndex == requestedIfIndex.Value) ||
            i.IfName.Equals(interfaceId, StringComparison.OrdinalIgnoreCase));

        if (targetIf == null ||
            !targetIf.WiphyIndex.HasValue ||
            !targetIf.Wdev.HasValue ||
            targetIf.IfType != LinuxNl80211Protocol.NL80211_IFTYPE_STATION)
        {
            return null;
        }

        var bssDump = await _socket.DumpBssAsync(family.FamilyId, targetIf.IfIndex, targetIf.Wdev.Value, cancellationToken).ConfigureAwait(false);
        if (!bssDump.IsComplete)
        {
            // Invariant 256: Incomplete BSS snapshot never becomes Disconnected
            return new LinuxWirelessAssociationObservation(
                IfIndex: targetIf.IfIndex,
                IfName: targetIf.IfName,
                WiphyIndex: targetIf.WiphyIndex.Value,
                State: LinuxWirelessAssociationState.Unknown,
                Links: Array.Empty<LinuxAssociatedBssLink>(),
                Wdev: targetIf.Wdev,
                Generation: null,
                DumpStatus: bssDump.Status);
        }

        var associatedLinks = new List<LinuxAssociatedBssLink>();
        ulong? wdev = targetIf.Wdev;
        uint? generation = null;

        foreach (var bss in bssDump.Items)
        {
            if (bss.Wdev.HasValue) wdev = bss.Wdev;
            if (bss.Generation.HasValue) generation = bss.Generation;

            if (bss.IsAssociated)
            {
                associatedLinks.Add(new LinuxAssociatedBssLink(
                    Bssid: bss.BssidString,
                    BssidBytes: bss.Bssid,
                    MloLinkId: bss.MloLinkId,
                    MldAddress: bss.MldAddressString,
                    MldAddressBytes: bss.MldAddress,
                    SsidBytes: bss.SsidBytes,
                    DisplaySsid: bss.DisplaySsid,
                    FrequencyMhz: bss.FrequencyMhz,
                    SignalMbm: bss.SignalMbm,
                    SignalUnspec: bss.SignalQuality,
                    SeenMsAgo: bss.SeenMsAgo,
                    LastSeenBootTimeNs: bss.LastSeenBootTimeNs));
            }
        }

        if (associatedLinks.Count == 0)
        {
            // Invariant: Empty dump NotAssociated verdict requires interface identity continuity check (t0 == t1)
            var ifDumpT1 = await _socket.DumpInterfacesAsync(family.FamilyId, targetIf.IfIndex, cancellationToken).ConfigureAwait(false);
            if (!ifDumpT1.IsComplete || ifDumpT1.Items.Count == 0)
            {
                return new LinuxWirelessAssociationObservation(
                    IfIndex: targetIf.IfIndex,
                    IfName: targetIf.IfName,
                    WiphyIndex: targetIf.WiphyIndex.Value,
                    State: LinuxWirelessAssociationState.Unknown,
                    Links: Array.Empty<LinuxAssociatedBssLink>(),
                    Wdev: targetIf.Wdev,
                    Generation: generation,
                    DumpStatus: bssDump.Status);
            }

            var t1 = ifDumpT1.Items.FirstOrDefault(i => i.IfIndex == targetIf.IfIndex);
            if (t1 == null || t1.Wdev != targetIf.Wdev || t1.WiphyIndex != targetIf.WiphyIndex || t1.IfType != targetIf.IfType)
            {
                // Identity changed / interface replaced (TOCTOU guard)
                return new LinuxWirelessAssociationObservation(
                    IfIndex: targetIf.IfIndex,
                    IfName: targetIf.IfName,
                    WiphyIndex: targetIf.WiphyIndex.Value,
                    State: LinuxWirelessAssociationState.Unknown,
                    Links: Array.Empty<LinuxAssociatedBssLink>(),
                    Wdev: targetIf.Wdev,
                    Generation: generation,
                    DumpStatus: bssDump.Status);
            }
        }

        var state = associatedLinks.Count > 0
            ? LinuxWirelessAssociationState.Associated
            : LinuxWirelessAssociationState.NotAssociated;

        return new LinuxWirelessAssociationObservation(
            IfIndex: targetIf.IfIndex,
            IfName: targetIf.IfName,
            WiphyIndex: targetIf.WiphyIndex.Value,
            State: state,
            Links: associatedLinks,
            Wdev: wdev,
            Generation: generation,
            DumpStatus: bssDump.Status);
    }

    /// <summary>
    /// The current association on this adapter, or null when it is not connected or unknown.
    /// Invariants 255, 262: Single-link association projects to Core WirelessAssociation;
    /// Multi-link MLO does not pick arbitrary single BSSID.
    /// </summary>
    public WirelessAssociation? ReadAssociation(string interfaceId)
    {
        if (string.IsNullOrWhiteSpace(interfaceId))
        {
            return null;
        }

        try
        {
            return ReadAssociationAsync(interfaceId).GetAwaiter().GetResult();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    public async Task<WirelessAssociation?> ReadAssociationAsync(
        string interfaceId,
        CancellationToken cancellationToken = default)
    {
        var obs = await ReadAssociationObservationAsync(interfaceId, cancellationToken).ConfigureAwait(false);
        if (obs == null || obs.State != LinuxWirelessAssociationState.Associated)
        {
            return null;
        }

        // Invariant 262: If exactly 1 link, project to singular Core record; if multi-link MLO, do NOT arbitrarily choose first link
        if (obs.Links.Count == 1)
        {
            var link = obs.Links[0];
            return new WirelessAssociation(link.DisplaySsid, link.Bssid, link.SignalUnspec);
        }

        return null;
    }

    /// <summary>
    /// Queries station metadata for a verified peer MAC associated with an interface using a correlation token.
    /// Invariant 257: Station metadata is enrichment truth and never changes the BSS association truth.
    /// Returns null if station query fails (e.g. ENOENT, timeout, permissions, WDEV mismatch), leaving association intact.
    /// </summary>
    public async Task<LinuxNl80211StationInfo?> ReadStationInfoAsync(
        LinuxNl80211StationCorrelationToken token,
        CancellationToken cancellationToken = default)
    {
        if (token == null)
        {
            return null;
        }

        return await ReadStationInfoAsync(token.IfIndex, token.Wdev, token.PeerMac, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Queries station metadata for a verified peer MAC on a specific interface index and WDEV.
    /// Invariant 257: Station metadata is enrichment truth and never changes the BSS association truth.
    /// </summary>
    public async Task<LinuxNl80211StationInfo?> ReadStationInfoAsync(
        int ifindex,
        ulong wdev,
        byte[] peerMac,
        CancellationToken cancellationToken = default)
    {
        if (ifindex <= 0 || peerMac == null || peerMac.Length != 6)
        {
            return null;
        }

        var family = await EnsureFamilyAsync(cancellationToken).ConfigureAwait(false);
        if (family == null)
        {
            return null;
        }

        var result = await _socket.GetStationAsync(family.FamilyId, ifindex, wdev, peerMac, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return null;
        }

        return result.Item;
    }

    /// <summary>
    /// Access point details (Phase 3.1-7B-4). Returns null in 7B-1.
    /// </summary>
    public WirelessAccessPoint? ReadAccessPoint(string ssid, string bssid) => null;

    /// <summary>
    /// Scan visibility check (Phase 3.1-7C). Returns null in 7B-1.
    /// </summary>
    public bool? IsSsidVisible(string ssid) => null;

    /// <summary>
    /// Optional urgent scan trigger (Phase 3.1-7C). No-op in 7B-1.
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
