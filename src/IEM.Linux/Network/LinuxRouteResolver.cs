using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network.Netlink;

namespace IEM.Linux.Network;

/// <summary>
/// Authoritative Linux route resolver that queries the Linux kernel FIB via Netlink RTM_GETROUTE.
/// Implements 2-second route caching, interface index mapping, and defensive multipath safety.
/// Invariants 271-275:
/// - Asks the kernel directly rather than guessing.
/// - Unresolved routes return ProbePath.Unresolved without crashing or disabling other probes.
/// - Zero shell execution (no ip, route, ping).
/// </summary>
public sealed class LinuxRouteResolver : IRouteResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<IPAddress, CachedPath> _cache = new();
    private readonly LinuxInterfaceIndexMap _interfaces = new();
    private readonly LinuxNetlinkRouteClient _netlinkClient;

    public LinuxRouteResolver(LinuxNetlinkRouteClient? netlinkClient = null)
    {
        _netlinkClient = netlinkClient ?? LinuxNetlinkRouteClient.Instance;
    }

    public ProbePath Resolve(IPAddress destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (_cache.TryGetValue(destination, out var cached) && !cached.IsStale)
        {
            return cached.Path;
        }

        var path = Lookup(destination);
        _cache[destination] = new CachedPath(path, Stopwatch.GetTimestamp());

        return path;
    }

    /// <summary>
    /// Forgets all cached routes and interface mappings so the next probe queries fresh kernel state.
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
        _interfaces.Invalidate();
    }

    private ProbePath Lookup(IPAddress destination)
    {
        var response = _netlinkClient.QueryRoute(destination);

        // 1. If Netlink query failed or response indicated error
        if (!response.IsSuccess)
        {
            return ProbePath.Unresolved;
        }

        // 2. Multipath safety: If kernel returned RTA_MULTIPATH, avoid false single-link certainty
        if (response.IsMultipath)
        {
            return ProbePath.Unresolved;
        }

        // 3. Must have a valid interface index
        if (!response.InterfaceIndex.HasValue || response.InterfaceIndex.Value <= 0)
        {
            return ProbePath.Unresolved;
        }

        var interfaceId = _interfaces.FindId(response.InterfaceIndex.Value);
        if (string.IsNullOrWhiteSpace(interfaceId))
        {
            return ProbePath.Unresolved;
        }

        var sourceAddress = response.PreferredSource;

        return new ProbePath(
            InterfaceId: interfaceId,
            SourceAddress: sourceAddress?.ToString(),
            Resolved: true,
            Bound: false);
    }

    private readonly struct CachedPath(ProbePath path, long recordedTimestamp)
    {
        public ProbePath Path { get; } = path;

        public bool IsStale =>
            Stopwatch.GetElapsedTime(recordedTimestamp) >= CacheLifetime;
    }

    private sealed class LinuxInterfaceIndexMap
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
        private readonly ConcurrentDictionary<int, string> _indexToId = new();
        private long _lastRefreshed = long.MinValue;
        private readonly object _refreshLock = new();

        public string? FindId(int interfaceIndex)
        {
            EnsureFresh();
            return _indexToId.TryGetValue(interfaceIndex, out var id) ? id : null;
        }

        public void Invalidate()
        {
            lock (_refreshLock)
            {
                _indexToId.Clear();
                _lastRefreshed = long.MinValue;
            }
        }

        private void EnsureFresh()
        {
            if (_lastRefreshed != long.MinValue &&
                Stopwatch.GetElapsedTime(_lastRefreshed) < RefreshInterval)
            {
                return;
            }

            lock (_refreshLock)
            {
                if (_lastRefreshed != long.MinValue &&
                    Stopwatch.GetElapsedTime(_lastRefreshed) < RefreshInterval)
                {
                    return;
                }

                _indexToId.Clear();

                try
                {
                    var nics = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var nic in nics)
                    {
                        var ipProps = nic.GetIPProperties();
                        try
                        {
                            var v4Props = ipProps.GetIPv4Properties();
                            if (v4Props is not null && v4Props.Index > 0)
                            {
                                _indexToId[v4Props.Index] = nic.Id;
                            }
                        }
                        catch
                        {
                        }

                        try
                        {
                            var v6Props = ipProps.GetIPv6Properties();
                            if (v6Props is not null && v6Props.Index > 0)
                            {
                                _indexToId[v6Props.Index] = nic.Id;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                    // Fallback on network interface query failure
                }

                _lastRefreshed = Stopwatch.GetTimestamp();
            }
        }
    }
}
