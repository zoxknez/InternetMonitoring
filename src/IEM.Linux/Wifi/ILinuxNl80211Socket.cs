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
    /// Queries a single wireless interface or dumps all interfaces.
    /// </summary>
    Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries physical wireless wiphy devices.
    /// </summary>
    Task<List<LinuxNl80211WiphyInfo>> GetWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default);
}
