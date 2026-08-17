using System.Runtime.Versioning;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Windows;

/// <summary>
/// Adds wireless detail to a link snapshot.
/// <para>
/// A decorator rather than a replacement, so the platform-neutral inspector stays the
/// single source of link state and this only fills in what the Native Wifi API can add:
/// signal, access point, channel, whether the radio is even on, and whether the network is
/// still being broadcast.
/// </para>
/// <para>
/// The judgements about those readings are not here. They live in
/// <see cref="WirelessDetailReader"/>, which knows nothing about Windows and can therefore
/// be tested without a radio - this class is the wiring between it and the platform. That
/// split matters because those rules decide whether a vanished network is read as the
/// router's fault or the customer's, and until it existed the only way to exercise them was
/// to walk around a flat with a laptop.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
/// <param name="interfaceId">The monitored adapter; its scan answers for its own airwaves only.</param>
public sealed class WlanLinkInspector(ILinkInspector inner, WlanScanCache scanCache, Guid? interfaceId = null)
    : ILinkInspector
{
    private readonly ILinkInspector _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private readonly WirelessDetailReader _wireless = new(
        new NativeWifiRadio(
            scanCache ?? throw new ArgumentNullException(nameof(scanCache)),
            interfaceId));

    public LinkSnapshot Inspect()
    {
        var snapshot = _inner.Inspect();

        if (snapshot.Medium != LinkMedium.Wireless)
        {
            return snapshot;
        }

        var wireless = _wireless.Read(snapshot.InterfaceId);

        // Trouble on a wireless link is the moment the scan answer decides who is at
        // fault, so ask for a fresh one rather than waiting for the healthy interval.
        if (!snapshot.IsUp || wireless?.IsSignalWeak == true)
        {
            _wireless.NoteTrouble();
        }

        return snapshot with { Wireless = wireless };
    }
}
