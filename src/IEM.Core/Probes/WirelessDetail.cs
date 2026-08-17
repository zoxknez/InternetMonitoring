using IEM.Core.Model;
using IEM.Core.Time;

namespace IEM.Core.Probes;

/// <param name="SignalQuality">0-100 as reported for this association.</param>
public sealed record WirelessAssociation(string? Ssid, string? Bssid, int? SignalQuality);

/// <param name="Rssi">The adapter's own reading in dBm, where the scan carried one.</param>
public sealed record WirelessAccessPoint(string Bssid, int? Channel, int? Rssi);

/// <summary>
/// Everything the platform's wireless API is asked for, behind one interface.
/// <para>
/// The interface exists so the rules built on these readings - which SSID to ask about after
/// the adapter has dropped, which access point's signal counts, when a remembered name stops
/// meaning anything - can be tested without a radio, a router, or someone walking around the
/// flat with a laptop. Those rules decide whether a vanished network is read as the router's
/// fault or the customer's, which is the single most consequential judgement this tool makes.
/// </para>
/// <para>
/// Every method may answer <see langword="null"/>, and null means "not known" rather than
/// "no". The confidence score treats a missing reading very differently from a negative one,
/// and an implementation that guessed would quietly turn one into the other.
/// </para>
/// </summary>
public interface IWirelessRadio
{
    /// <summary>Whether the radio is on, for this adapter and no other.</summary>
    bool? IsRadioOn(string interfaceId);

    /// <summary>The current association on this adapter, or null when it is not connected.</summary>
    WirelessAssociation? ReadAssociation(string interfaceId);

    /// <summary>
    /// The access point with this exact BSSID, as the most recent scan describes it - never
    /// the loudest one advertising the same name.
    /// </summary>
    WirelessAccessPoint? ReadAccessPoint(string ssid, string bssid);

    /// <summary>Whether the named network was on the air at the last usable scan.</summary>
    bool? IsSsidVisible(string ssid);

    /// <summary>Asks for a scan sooner than the healthy interval, because something looks wrong.</summary>
    void RequestUrgentScan();
}

/// <summary>
/// Turns raw wireless readings into the snapshot the classifier reasons about.
/// <para>
/// Deliberately free of any platform API, because this is where the judgements live. The
/// most consequential one: after the adapter drops, Windows no longer says which network it
/// was on, so the name has to be remembered - and a remembered name is only good for as long
/// as it still describes the connection being measured. Hours later it names a network the
/// adapter has not seen since, and a visibility check built on it would answer about the
/// wrong link entirely while looking perfectly authoritative.
/// </para>
/// </summary>
public sealed class WirelessDetailReader(IWirelessRadio radio, IClock? clock = null)
{
    private readonly IWirelessRadio _radio = radio ?? throw new ArgumentNullException(nameof(radio));
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    /// <summary>How long a dropped connection's name stays meaningful.</summary>
    public static readonly TimeSpan RememberedSsidLifetime = TimeSpan.FromMinutes(10);

    private string? _lastKnownSsid;
    private long _lastKnownSsidAt;

    /// <summary>
    /// Reads what the radio can add about this adapter, or null when there is nothing to add.
    /// <para>
    /// Null rather than an empty snapshot: "no wireless detail was available" and "the
    /// wireless detail says nothing is wrong" are opposite findings, and the confidence
    /// score is built to tell them apart.
    /// </para>
    /// </summary>
    public WirelessSnapshot? Read(string interfaceId)
    {
        var radioOn = _radio.IsRadioOn(interfaceId);
        var association = _radio.ReadAssociation(interfaceId);
        var connectedSsid = association?.Ssid;

        if (connectedSsid is not null)
        {
            _lastKnownSsid = connectedSsid;
            _lastKnownSsidAt = _clock.MonotonicTicks;
        }

        var ssid = connectedSsid ?? RememberedSsid();

        if (ssid is null)
        {
            return null;
        }

        // Looked up by the BSSID the adapter is actually associated with, not by picking the
        // loudest access point advertising this name. On a mesh or a dual-band router several
        // access points share an SSID, and picking by signal names the wrong one - then
        // invents a roaming event every time the strongest changes while the adapter sits
        // still on the sofa.
        var accessPoint = association?.Bssid is { } associated
            ? _radio.ReadAccessPoint(ssid, associated)
            : null;

        return new WirelessSnapshot(ssid, accessPoint?.Bssid ?? association?.Bssid, association?.SignalQuality, accessPoint?.Channel)
        {
            // The adapter's own dBm reading where available. Deriving it from the quality
            // percentage loses most of the resolution, and the difference between -68 and
            // -74 dBm is the difference between a healthy link and one that drops.
            MeasuredRssiDbm = accessPoint?.Rssi,
            RadioOn = radioOn,
            SsidVisibleInScan = _radio.IsSsidVisible(ssid),
        };
    }

    /// <summary>
    /// Asks for a fresh scan when the link looks like it is in trouble, because that is the
    /// moment the scan's answer decides who is at fault.
    /// </summary>
    public void NoteTrouble() => _radio.RequestUrgentScan();

    /// <summary>
    /// The name of the network the adapter was last on, while that still means something.
    /// <para>
    /// The remembered name exists to bridge the drop, not to outlive it.
    /// </para>
    /// </summary>
    private string? RememberedSsid() =>
        _lastKnownSsid is not null &&
        _clock.MonotonicElapsedSince(_lastKnownSsidAt) < RememberedSsidLifetime
            ? _lastKnownSsid
            : null;
}
