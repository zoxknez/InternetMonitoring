using System;

namespace IEM.Linux.Wifi;

/// <summary>
/// Source basis for rfkill positive observation.
/// </summary>
public enum LinuxRfkillEvidenceBasis
{
    DevRfkill,
    SysfsPhy,
    SysfsClass
}

/// <summary>
/// Structured observation of an rfkill switch scoped to a specific wiphy.
/// Invariant 249.
/// </summary>
public sealed record LinuxRfkillObservation(
    int RfkillIndex,
    uint WiphyIndex,
    bool HardBlocked,
    bool SoftBlocked,
    LinuxRfkillEvidenceBasis Basis);

/// <summary>
/// Abstraction for reading wiphy-scoped rfkill state.
/// </summary>
public interface ILinuxRfkillReader
{
    /// <summary>
    /// Reads positive rfkill block/unblock state for the given wiphy index.
    /// Returns null when rfkill node is absent, unreadable, or ambiguous.
    /// </summary>
    LinuxRfkillObservation? ReadObservationForWiphy(uint wiphyIndex, string? ifname = null);
}
