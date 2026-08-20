using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Probes;
using IEM.Linux.Time;

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
    private readonly ILinuxWifiScanCompletionTracker _scanCompletionTracker;
    private readonly ILinuxNativeClock? _clock;
    private readonly string? _boundInterfaceId;
    private readonly bool _ownsSocket;
    private volatile string? _lastQueriedInterfaceId;
    private GenlFamilyInfo? _cachedFamily;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LinuxNl80211Radio(
        ILinuxNl80211Socket? socket = null,
        ILinuxRfkillReader? rfkillReader = null,
        string? boundInterfaceId = null,
        bool? ownsSocket = null,
        ILinuxWifiScanCompletionTracker? scanCompletionTracker = null)
        : this(socket, rfkillReader, boundInterfaceId, ownsSocket, scanCompletionTracker, null)
    {
    }

    internal LinuxNl80211Radio(
        ILinuxNl80211Socket? socket,
        ILinuxRfkillReader? rfkillReader,
        string? boundInterfaceId,
        bool? ownsSocket,
        ILinuxWifiScanCompletionTracker? scanCompletionTracker,
        ILinuxNativeClock? clock)
    {
        _ownsSocket = ownsSocket ?? (socket == null);
        _socket = socket ?? LinuxNl80211Socket.Create();
        _rfkillReader = rfkillReader ?? LinuxRfkillReader.Instance;
        _scanCompletionTracker = scanCompletionTracker ?? new LinuxWifiScanCompletionTracker();
        _boundInterfaceId = boundInterfaceId;
        _clock = clock;
    }

    public string? BoundInterfaceId => _boundInterfaceId;
    internal ILinuxWifiScanCompletionTracker ScanCompletionTracker => _scanCompletionTracker;

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
        if (!string.IsNullOrWhiteSpace(interfaceId))
        {
            _lastQueriedInterfaceId = interfaceId;
        }

        var obs = await ReadAssociationObservationAsync(interfaceId, cancellationToken).ConfigureAwait(false);
        if (obs == null || obs.State != LinuxWirelessAssociationState.Associated)
        {
            return null;
        }

        var mlo = ComposeMloAssociation(obs.Links);
        if (mlo.State == LinuxMloCompositionState.NotMlo)
        {
            if (obs.Links.Count == 1)
            {
                var link = obs.Links[0];
                return new WirelessAssociation(link.DisplaySsid, link.Bssid, link.SignalUnspec);
            }
            return null;
        }

        // MLO association: safe Core projection
        // Ssid = common DisplaySsid
        // Bssid = null (strictly never link BSSID, never MLD address)
        // SignalQuality = null (strictly never copied from arbitrary link)
        return new WirelessAssociation(mlo.DisplaySsid, null, null);
    }

    /// <summary>
    /// Captures full composed wireless association observation (Phase 3.1-7B-3).
    /// Orchestrates BSS association truth (7B-1) with station peer enrichment (7B-2).
    /// Enforces t0 -> station -> t2 temporal continuity with bounded retry.
    /// Invariants 255-258, 262.
    /// </summary>
    public async Task<LinuxComposedAssociationObservation?> ReadComposedAssociationObservationAsync(
        string interfaceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceId))
        {
            return null;
        }

        const int maxAttempts = 2; // Exactly max 2 attempts (Attempt 1 + 1 retry on drift)

        LinuxWirelessAssociationObservation? freshestBss = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var t0 = await ReadAssociationObservationAsync(interfaceId, cancellationToken).ConfigureAwait(false);
            if (t0 == null)
            {
                return null;
            }

            freshestBss = t0;

            // Invariant: Non-associated or Unknown states NEVER invoke GET_STATION
            if (t0.State != LinuxWirelessAssociationState.Associated || !t0.Wdev.HasValue)
            {
                return new LinuxComposedAssociationObservation(
                    IfIndex: t0.IfIndex,
                    IfName: t0.IfName,
                    WiphyIndex: t0.WiphyIndex,
                    State: t0.State,
                    Links: t0.Links,
                    Wdev: t0.Wdev,
                    Generation: t0.Generation,
                    StationInfo: null,
                    ContinuityVerified: t0.State == LinuxWirelessAssociationState.NotAssociated,
                    DumpStatus: t0.DumpStatus);
            }

            // Evidence hardening: Associated requires proven BSS generation, never synthesize fallback generation 0
            if (!t0.Generation.HasValue)
            {
                return new LinuxComposedAssociationObservation(
                    IfIndex: t0.IfIndex,
                    IfName: t0.IfName,
                    WiphyIndex: t0.WiphyIndex,
                    State: LinuxWirelessAssociationState.Associated,
                    Links: t0.Links,
                    Wdev: t0.Wdev,
                    Generation: null,
                    StationInfo: null,
                    ContinuityVerified: false,
                    DumpStatus: t0.DumpStatus);
            }

            // Extract peer MAC for station query (Non-MLO = single BSSID, MLO = proven common MLD address)
            if (!TryExtractPeerMac(t0, out var peerMac) || peerMac == null)
            {
                // Inconsistent or missing MLD identity: do not guess, return Associated with StationInfo = null and ContinuityVerified = false (no t2 comparison performed)
                return new LinuxComposedAssociationObservation(
                    IfIndex: t0.IfIndex,
                    IfName: t0.IfName,
                    WiphyIndex: t0.WiphyIndex,
                    State: LinuxWirelessAssociationState.Associated,
                    Links: t0.Links,
                    Wdev: t0.Wdev,
                    Generation: t0.Generation,
                    StationInfo: null,
                    ContinuityVerified: false,
                    DumpStatus: t0.DumpStatus);
            }

            var token = new LinuxNl80211StationCorrelationToken(
                IfIndex: t0.IfIndex,
                Wdev: t0.Wdev.Value,
                WiphyIndex: t0.WiphyIndex,
                PeerMac: peerMac,
                PeerMacString: LinuxNl80211Protocol.FormatMacAddress(peerMac),
                BssGeneration: t0.Generation.Value);

            // t1: Query station metadata
            var staInfo = await ReadStationInfoAsync(token, cancellationToken).ConfigureAwait(false);

            // t2: Re-read BSS observation to verify continuity
            var t2 = await ReadAssociationObservationAsync(interfaceId, cancellationToken).ConfigureAwait(false);
            if (t2 == null)
            {
                return null;
            }

            freshestBss = t2;

            if (AreAssociationIdentitiesEqual(t0, t2))
            {
                // Continuity verified: stable across composition window
                return new LinuxComposedAssociationObservation(
                    IfIndex: t2.IfIndex,
                    IfName: t2.IfName,
                    WiphyIndex: t2.WiphyIndex,
                    State: t2.State,
                    Links: t2.Links,
                    Wdev: t2.Wdev,
                    Generation: t2.Generation,
                    StationInfo: staInfo,
                    ContinuityVerified: true,
                    DumpStatus: t2.DumpStatus);
            }

            // Drift detected between t0 and t2: station info from t1 is stale for the new state, discard it.
            // On attempt 1, loop continues to attempt 2.
        }

        // After max attempts with continuous drift, return freshest authoritative BSS observation with StationInfo = null
        return new LinuxComposedAssociationObservation(
            IfIndex: freshestBss!.IfIndex,
            IfName: freshestBss.IfName,
            WiphyIndex: freshestBss.WiphyIndex,
            State: freshestBss.State,
            Links: freshestBss.Links,
            Wdev: freshestBss.Wdev,
            Generation: freshestBss.Generation,
            StationInfo: null,
            ContinuityVerified: false,
            DumpStatus: freshestBss.DumpStatus);
    }

    private static bool TryExtractPeerMac(LinuxWirelessAssociationObservation obs, out byte[]? peerMac)
    {
        peerMac = null;
        if (obs.Links == null || obs.Links.Count == 0)
        {
            return false;
        }

        if (obs.Links.Count == 1)
        {
            var single = obs.Links[0];
            if (single.MldAddressBytes != null && single.MldAddressBytes.Length == 6)
            {
                peerMac = single.MldAddressBytes;
                return true;
            }
            if (single.BssidBytes != null && single.BssidBytes.Length == 6)
            {
                peerMac = single.BssidBytes;
                return true;
            }
            return false;
        }

        // MLO (multi-link): all associated links must share the exact same valid 6-byte MLD address
        byte[]? commonMld = null;
        foreach (var link in obs.Links)
        {
            if (link.MldAddressBytes == null || link.MldAddressBytes.Length != 6)
            {
                // Incomplete / inconsistent link identity
                return false;
            }

            if (commonMld == null)
            {
                commonMld = link.MldAddressBytes;
            }
            else if (!commonMld.AsSpan().SequenceEqual(link.MldAddressBytes))
            {
                // Links disagree on MLD address
                return false;
            }
        }

        if (commonMld != null)
        {
            peerMac = commonMld;
            return true;
        }

        return false;
    }

    private static bool AreAssociationIdentitiesEqual(LinuxWirelessAssociationObservation a, LinuxWirelessAssociationObservation b)
    {
        if (a.IfIndex != b.IfIndex ||
            a.Wdev != b.Wdev ||
            a.WiphyIndex != b.WiphyIndex ||
            a.Generation != b.Generation ||
            a.State != b.State)
        {
            return false;
        }

        if (a.State != LinuxWirelessAssociationState.Associated)
        {
            return true;
        }

        if (a.Links.Count != b.Links.Count)
        {
            return false;
        }

        // Compare links as an unordered set of (MloLinkId, BssidBytes, MldAddressBytes)
        var matched = new bool[b.Links.Count];

        foreach (var linkA in a.Links)
        {
            bool found = false;
            for (int i = 0; i < b.Links.Count; i++)
            {
                if (matched[i]) continue;
                var linkB = b.Links[i];

                if (linkA.MloLinkId == linkB.MloLinkId &&
                    ByteArraysEqual(linkA.BssidBytes, linkB.BssidBytes) &&
                    ByteArraysEqual(linkA.MldAddressBytes, linkB.MldAddressBytes))
                {
                    matched[i] = true;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ByteArraysEqual(byte[]? a, byte[]? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    /// <summary>
    /// Evaluates MLO candidate links and deterministically composes MLO association facts.
    /// Phase 3.1-7B-5: Canonical ordering (LinkId, raw BSSID), strict common MLD/SSID validation,
    /// and separation of aggregate MLD station telemetry from per-link facts.
    /// </summary>
    public static LinuxMloAssociationInfo ComposeMloAssociation(LinuxComposedAssociationObservation observation)
    {
        if (observation == null)
        {
            return new LinuxMloAssociationInfo(
                State: LinuxMloCompositionState.NotMlo,
                MldAddressBytes: null,
                MldAddress: null,
                SsidBytes: null,
                DisplaySsid: null,
                Links: Array.Empty<LinuxAssociatedBssLink>());
        }
        return ComposeMloAssociation(observation.Links);
    }

    /// <summary>
    /// Evaluates MLO candidate links and deterministically composes MLO association facts.
    /// </summary>
    public static LinuxMloAssociationInfo ComposeMloAssociation(IReadOnlyList<LinuxAssociatedBssLink> links)
    {
        if (links == null || links.Count == 0)
        {
            return new LinuxMloAssociationInfo(
                State: LinuxMloCompositionState.NotMlo,
                MldAddressBytes: null,
                MldAddress: null,
                SsidBytes: null,
                DisplaySsid: null,
                Links: Array.Empty<LinuxAssociatedBssLink>());
        }

        bool isMloCandidate = links.Count > 1 ||
                              links.Any(l => l.MloLinkId.HasValue || l.MldAddressBytes != null);

        if (!isMloCandidate)
        {
            return new LinuxMloAssociationInfo(
                State: LinuxMloCompositionState.NotMlo,
                MldAddressBytes: null,
                MldAddress: null,
                SsidBytes: links[0].SsidBytes,
                DisplaySsid: links[0].DisplaySsid,
                Links: links);
        }

        bool hasIncomplete = false;
        bool hasConflict = false;

        byte[]? commonMldBytes = null;
        string? commonMldStr = null;
        byte[]? commonSsidBytes = null;
        string? commonDisplaySsid = null;
        bool seenSsidBytes = false;
        bool seenDisplaySsidOnly = false;

        var seenLinkIds = new HashSet<byte>();
        var seenRawBssids = new List<byte[]>();

        foreach (var link in links)
        {
            // Incomplete check: missing LinkId or missing/invalid MldAddressBytes or invalid BssidBytes
            if (!link.MloLinkId.HasValue || link.MldAddressBytes == null || link.MldAddressBytes.Length != 6 ||
                link.BssidBytes == null || link.BssidBytes.Length != 6)
            {
                hasIncomplete = true;
            }

            // Conflicted check: MLD address mismatch
            if (link.MldAddressBytes != null && link.MldAddressBytes.Length == 6)
            {
                if (commonMldBytes == null)
                {
                    commonMldBytes = link.MldAddressBytes;
                    commonMldStr = link.MldAddress ?? LinuxNl80211Protocol.FormatMacAddress(link.MldAddressBytes);
                }
                else if (!commonMldBytes.AsSpan().SequenceEqual(link.MldAddressBytes))
                {
                    hasConflict = true;
                }
            }

            // Conflicted check: duplicate LinkId
            if (link.MloLinkId.HasValue)
            {
                if (!seenLinkIds.Add(link.MloLinkId.Value))
                {
                    hasConflict = true;
                }
            }

            // Conflicted check: duplicate raw BSSID bytes (wire identity authority)
            if (link.BssidBytes != null && link.BssidBytes.Length == 6)
            {
                bool isDuplicateRawBssid = false;
                foreach (var seen in seenRawBssids)
                {
                    if (seen.AsSpan().SequenceEqual(link.BssidBytes))
                    {
                        isDuplicateRawBssid = true;
                        break;
                    }
                }

                if (isDuplicateRawBssid)
                {
                    hasConflict = true;
                }
                else
                {
                    seenRawBssids.Add(link.BssidBytes);
                }
            }

            // SSID handling: raw byte authority (including observed zero-length hidden SSID)
            if (link.SsidBytes != null)
            {
                if (!seenSsidBytes)
                {
                    seenSsidBytes = true;
                    commonSsidBytes = link.SsidBytes;
                    commonDisplaySsid = link.DisplaySsid;
                }
                else if (!commonSsidBytes!.AsSpan().SequenceEqual(link.SsidBytes))
                {
                    hasConflict = true;
                }
            }
            else if (!string.IsNullOrEmpty(link.DisplaySsid))
            {
                if (!seenSsidBytes && !seenDisplaySsidOnly)
                {
                    seenDisplaySsidOnly = true;
                    commonDisplaySsid = link.DisplaySsid;
                }
                else if (commonDisplaySsid != null && !string.Equals(commonDisplaySsid, link.DisplaySsid, StringComparison.Ordinal))
                {
                    hasConflict = true;
                }
            }
        }

        // Canonical ordering: sort by MloLinkId (ascending, nulls last), then raw BSSID bytes, then Bssid string
        var canonicalLinks = links
            .OrderBy(l => l.MloLinkId ?? byte.MaxValue)
            .ThenBy(l => l.BssidBytes, ByteSequenceComparer.Instance)
            .ThenBy(l => l.Bssid, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LinuxMloCompositionState state;
        if (hasConflict)
        {
            state = LinuxMloCompositionState.Conflicted;
            commonMldBytes = null;
            commonMldStr = null;
            commonSsidBytes = null;
            commonDisplaySsid = null;
        }
        else if (hasIncomplete)
        {
            state = LinuxMloCompositionState.Incomplete;
        }
        else
        {
            state = LinuxMloCompositionState.Valid;
        }

        return new LinuxMloAssociationInfo(
            State: state,
            MldAddressBytes: commonMldBytes,
            MldAddress: commonMldStr,
            SsidBytes: commonSsidBytes,
            DisplaySsid: commonDisplaySsid,
            Links: canonicalLinks);
    }

    private sealed class ByteSequenceComparer : IComparer<byte[]?>
    {
        public static readonly ByteSequenceComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            int minLen = Math.Min(x.Length, y.Length);
            for (int i = 0; i < minLen; i++)
            {
                int cmp = x[i].CompareTo(y[i]);
                if (cmp != 0) return cmp;
            }
            return x.Length.CompareTo(y.Length);
        }
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
    /// Converts a frequency in MHz to its standard 802.11 channel number.
    /// Strict evidence-grade raster check: returns null if the frequency does not fall precisely on a valid channel raster.
    /// </summary>
    public static int? FrequencyMhzToChannel(uint frequencyMhz)
    {
        // 2.4 GHz Band: 2412..2472 on 5 MHz grid -> channels 1..13
        if (frequencyMhz >= 2412 && frequencyMhz <= 2472 && (frequencyMhz - 2412) % 5 == 0)
        {
            return (int)((frequencyMhz - 2407) / 5);
        }
        // 2.4 GHz Band: 2484 MHz -> channel 14
        if (frequencyMhz == 2484)
        {
            return 14;
        }
        // 4.9 GHz Band (Public Safety): 4910..4980 on 5 MHz grid -> channels 182..196
        if (frequencyMhz >= 4910 && frequencyMhz <= 4980 && (frequencyMhz - 4910) % 5 == 0)
        {
            return (int)((frequencyMhz - 4000) / 5);
        }
        // 5 GHz Band: 5000..<5925 on 5 MHz grid -> e.g. 5180 -> 36
        if (frequencyMhz >= 5000 && frequencyMhz < 5925 && (frequencyMhz - 5000) % 5 == 0)
        {
            int ch = (int)((frequencyMhz - 5000) / 5);
            return ch > 0 ? ch : null;
        }
        // 6 GHz Band: 5935 MHz -> channel 2
        if (frequencyMhz == 5935)
        {
            return 2;
        }
        // 6 GHz Band: 5955..7115 on 5 MHz grid -> channels 1..233
        if (frequencyMhz >= 5955 && frequencyMhz <= 7115 && (frequencyMhz - 5955) % 5 == 0)
        {
            return (int)((frequencyMhz - 5950) / 5);
        }

        return null;
    }

    /// <summary>
    /// The access point with this exact BSSID from the bound monitored adapter's cached scan.
    /// Invariant 258: Adapter-scoped projection of one complete cached GET_SCAN snapshot.
    /// Null when no bound interface is set, BSS is missing from scan, dump was incomplete, or BSSID/SSID mismatch.
    /// </summary>
    public WirelessAccessPoint? ReadAccessPoint(string ssid, string bssid)
    {
        var targetInterface = _boundInterfaceId ?? _lastQueriedInterfaceId;
        if (string.IsNullOrWhiteSpace(targetInterface))
        {
            return null;
        }

        try
        {
            return ReadAccessPointAsync(targetInterface, ssid, bssid).GetAwaiter().GetResult();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// Asynchronously queries cached BSS scan results on the specified interface for an exact BSSID match.
    /// Invariants:
    /// - Strict raw byte BSSID matching (presentation string is not identity authority).
    /// - Strict display SSID guard (must match requested SSID).
    /// - Incomplete dumps return null (never partial knowledge).
    /// - Never invokes active scanning (TRIGGER_SCAN) or station queries (GET_STATION).
    /// </summary>
    public async Task<WirelessAccessPoint?> ReadAccessPointAsync(
        string interfaceId,
        string ssid,
        string bssid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceId) ||
            string.IsNullOrWhiteSpace(ssid) ||
            string.IsNullOrWhiteSpace(bssid))
        {
            return null;
        }

        if (!LinuxNl80211Protocol.TryParseMacAddress(bssid, out var requestedMacBytes) || requestedMacBytes == null)
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
            return null;
        }

        foreach (var bss in bssDump.Items)
        {
            if (bss.Bssid != null &&
                bss.Bssid.AsSpan().SequenceEqual(requestedMacBytes) &&
                string.Equals(bss.DisplaySsid, ssid, StringComparison.Ordinal))
            {
                int? channel = bss.FrequencyMhz.HasValue ? FrequencyMhzToChannel(bss.FrequencyMhz.Value) : null;
                int? rssi = bss.SignalDbm; // SignalMbm / 100 if SignalMbm is present, null if only SignalUnspec

                return new WirelessAccessPoint(bss.BssidString, channel, rssi);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether the specified SSID is currently visible in the adapter's cached scan results.
    /// Invariants 250, 258:
    /// - true:  Positive evidence of at least one fresh matching BSS in cached scan.
    /// - false: Proven absence from a complete, fresh scan dump on the bound adapter.
    /// - null:  Stale scan, incomplete dump, empty dump without scan-done evidence, or unbound adapter.
    /// </summary>
    public bool? IsSsidVisible(string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return null;
        }

        var targetInterface = _boundInterfaceId ?? _lastQueriedInterfaceId;
        if (string.IsNullOrWhiteSpace(targetInterface))
        {
            return null;
        }

        try
        {
            return IsSsidVisibleAsync(targetInterface, ssid).GetAwaiter().GetResult();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// Asynchronously queries cached scan results on the specified interface to evaluate SSID visibility.
    /// Strict adapter-scoping: never queries other interfaces or unions global scan results.
    /// Invariants 250, 251, 252, 258.
    /// </summary>
    public async Task<bool?> IsSsidVisibleAsync(
        string interfaceId,
        string ssid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceId) || string.IsNullOrWhiteSpace(ssid))
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
            !targetIf.Wdev.HasValue ||
            targetIf.IfType != LinuxNl80211Protocol.NL80211_IFTYPE_STATION)
        {
            return null;
        }

        var preSnapshot = _scanCompletionTracker.GetSnapshot(targetIf.IfIndex, targetIf.Wdev);
        var dumpStartedAtNs = LinuxWifiScanCache.TryGetCurrentBootTimeNs(_clock);

        var bssDump = await _socket.DumpBssAsync(family.FamilyId, targetIf.IfIndex, targetIf.Wdev.Value, cancellationToken).ConfigureAwait(false);

        var dumpCompletedAtNs = LinuxWifiScanCache.TryGetCurrentBootTimeNs(_clock);
        var postSnapshot = _scanCompletionTracker.GetSnapshot(targetIf.IfIndex, targetIf.Wdev);

        var snapshot = LinuxWifiScanCache.EvaluateScanDump(
            bssDump,
            targetIf.IfIndex,
            targetIf.Wdev,
            _scanCompletionTracker,
            dumpCompletedAtNs,
            preSnapshot,
            postSnapshot,
            dumpStartedAtNs,
            dumpCompletedAtNs,
            requestedSsid: ssid);

        return LinuxWifiScanCache.EvaluateSsidVisibility(snapshot, ssid, dumpCompletedAtNs);
    }

    /// <summary>
    /// Optional urgent scan trigger (Phase 3.1-7C).
    /// Intentionally NO-OP in Linux 3.1 baseline (active scan is never required for evidence, Invariant 251).
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
