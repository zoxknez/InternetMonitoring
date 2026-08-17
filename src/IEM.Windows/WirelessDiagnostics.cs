using System.Runtime.Versioning;
using ManagedNativeWifi;

namespace IEM.Windows;

/// <param name="State">What the adapter reports about itself.</param>
/// <param name="VisibleNetworks">Networks the last scan turned up.</param>
public sealed record WirelessRadio(
    string Description,
    string State,
    string? Ssid,
    string? Bssid,
    int? SignalQuality,
    int VisibleNetworks);

/// <summary>
/// Reads every wireless adapter on the machine, for diagnosis rather than for measurement.
/// <para>
/// Separate from <see cref="WlanLinkInspector"/> because the questions differ. That one asks
/// what the monitored link is doing right now; this asks whether the wireless layer can be
/// read at all on this machine - which is what someone needs to know before trusting a
/// Wi-Fi outage to be attributable.
/// </para>
/// <para>
/// Everything is wrapped. A desktop with no Wi-Fi card and the WLAN service stopped is the
/// common case, not an edge one, and it has to produce an empty list rather than an
/// exception - the alternative is a crash during someone's two-day recording.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WirelessDiagnostics
{
    public static IReadOnlyList<WirelessRadio> Read()
    {
        var interfaces = Enumerate();

        if (interfaces.Count == 0)
        {
            return [];
        }

        var visible = CountVisible();
        var radios = new List<WirelessRadio>(interfaces.Count);

        foreach (var adapter in interfaces)
        {
            var connection = CurrentConnection(adapter.Id);

            radios.Add(new WirelessRadio(
                adapter.Description ?? adapter.Id.ToString(),
                connection?.InterfaceState.ToString() ?? adapter.State.ToString(),
                connection?.Ssid?.ToString(),
                connection?.Bssid?.ToString(),
                connection?.SignalQuality,
                visible));
        }

        return radios;
    }

    private static IReadOnlyList<InterfaceInfo> Enumerate()
    {
        try
        {
            return [.. NativeWifi.EnumerateInterfaces()];
        }
        catch (Exception exception) when (IsWlanUnavailable(exception))
        {
            return [];
        }
    }

    private static CurrentConnectionInfo? CurrentConnection(Guid interfaceId)
    {
        try
        {
            var (result, connection) = NativeWifi.GetCurrentConnection(interfaceId);
            return result == ActionResult.Success ? connection : null;
        }
        catch (Exception exception) when (IsWlanUnavailable(exception))
        {
            return null;
        }
    }

    private static int CountVisible()
    {
        try
        {
            return NativeWifi.EnumerateBssNetworks().Count();
        }
        catch (Exception exception) when (IsWlanUnavailable(exception))
        {
            return 0;
        }
    }

    /// <summary>
    /// The ways the wireless subsystem says it is not there.
    /// <para>
    /// Named rather than swallowing everything: a genuine bug in this layer should still
    /// surface, while a machine without Wi-Fi should not look like one.
    /// </para>
    /// <para>
    /// Unwrapped, because the one that matters arrives wrapped. On a machine where the WLAN
    /// service is stopped - a desktop with no wireless card, or a laptop with Wi-Fi switched
    /// off - the underlying call fails with Win32 error 1062, but it is raised from inside a
    /// reflection-created object and reaches here as a <see cref="TargetInvocationException"/>.
    /// Matching only the outer type let the commonest configuration of all crash the layer.
    /// </para>
    /// </summary>
    private static bool IsWlanUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.ComponentModel.Win32Exception
                        or InvalidOperationException
                        or PlatformNotSupportedException
                        or DllNotFoundException
                        or EntryPointNotFoundException)
            {
                return true;
            }
        }

        return false;
    }
}
