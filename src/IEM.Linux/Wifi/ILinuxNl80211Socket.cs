using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace IEM.Linux.Wifi;

/// <summary>
/// Netlink communication client for Generic Netlink and nl80211.
/// Decoupled interface to support 100% deterministic unit testing and live unprivileged execution.
/// </summary>
public interface ILinuxNl80211Socket : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Discovers a Generic Netlink family ID and multicast groups (e.g. "nl80211").
    /// </summary>
    Task<GenlFamilyInfo?> GetFamilyAsync(string familyName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dumps all wireless interfaces or queries a single interface with full status provenance.
    /// </summary>
    Task<LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>> DumpInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dumps physical wireless wiphy devices with full status provenance.
    /// </summary>
    Task<LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>> DumpWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dumps cached BSS scan results for a specific wireless interface with full status provenance.
    /// Invariant 259: Reads cached kernel BSS results without triggering an RF scan.
    /// </summary>
    Task<LinuxNl80211DumpResult<LinuxNl80211BssInfo>> DumpBssAsync(ushort nl80211FamilyId, int ifindex, ulong? expectedWdev = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries a single wireless interface or dumps all interfaces.
    /// </summary>
    Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries physical wireless wiphy devices.
    /// </summary>
    Task<List<LinuxNl80211WiphyInfo>> GetWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default);
}
