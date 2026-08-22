using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace IEM.Windows;

public enum InterfaceEligibilityStatus
{
    EligiblePhysicalCarrier,
    RejectedLoopback,
    RejectedTunnel,
    RejectedWfpFilter,
    RejectedPacketCapture,
    RejectedSoftwarePseudoAdapter,
    RejectedNoPhysicalIdentity,
}

/// <summary>
/// Classifies network interfaces for auto-selection eligibility.
/// IMPORTANT: Eligibility filtering applies ONLY to AUTO selection mode.
/// Explicit user selection honors the requested adapter without rejection.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsInterfaceEligibilityClassifier
{
    public static InterfaceEligibilityStatus Classify(NetworkInterface nic)
    {
        ArgumentNullException.ThrowIfNull(nic);

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return InterfaceEligibilityStatus.RejectedLoopback;
        }

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
        {
            return InterfaceEligibilityStatus.RejectedTunnel;
        }

        var description = nic.Description ?? string.Empty;
        var name = nic.Name ?? string.Empty;

        // Incident Rejection: WFP LightWeight Filters
        if (description.Contains("WFP", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("WFP", StringComparison.OrdinalIgnoreCase))
        {
            return InterfaceEligibilityStatus.RejectedWfpFilter;
        }

        // Packet capture filters & packet schedulers
        if (description.Contains("Npcap", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Packet Scheduler", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Pcap", StringComparison.OrdinalIgnoreCase))
        {
            return InterfaceEligibilityStatus.RejectedPacketCapture;
        }

        // Software pseudo-devices lacking hardware/physical identity
        var macBytes = nic.GetPhysicalAddress()?.GetAddressBytes();
        var hasMac = macBytes is not null && macBytes.Length > 0 && macBytes.Any(b => b != 0);

        if (!hasMac && nic.GetIPProperties().GatewayAddresses.Count == 0 && nic.Speed <= 0)
        {
            return InterfaceEligibilityStatus.RejectedNoPhysicalIdentity;
        }

        return InterfaceEligibilityStatus.EligiblePhysicalCarrier;
    }
}
