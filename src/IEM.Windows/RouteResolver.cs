using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Windows;

/// <summary>
/// Answers the routing question the way Windows answers it, by asking Windows.
/// <para>
/// <c>GetBestRoute2</c> is the same lookup the stack performs when a socket connects, so its
/// answer is not an approximation of the choice - it is the choice.
/// </para>
/// <para>
/// Invariants:
/// WIN_SESSION_INTERFACE_IMMUTABLE: Target interface is pinned.
/// WIN_PROBE_PATH_NEVER_ESCAPES_PINNED_INTERFACE: Lookups are constrained to pinned interface index.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RouteResolver : IRouteResolver
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<IPAddress, CachedPath> _cache = new();
    private readonly InterfaceIndexMap _interfaces = new();
    private readonly string? _pinnedInterfaceId;

    public RouteResolver(string? pinnedInterfaceId = null)
    {
        _pinnedInterfaceId = pinnedInterfaceId;
    }

    public RouteResolver(MonitoredInterfaceIdentity monitoredInterface)
    {
        _pinnedInterfaceId = monitoredInterface?.InterfaceId;
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

    /// <summary>Forgets every cached route, so the next probe re-asks from scratch.</summary>
    public void Invalidate()
    {
        _cache.Clear();
        _interfaces.Invalidate();
    }

    private ProbePath Lookup(IPAddress destination)
    {
        Span<byte> destinationSockaddr = stackalloc byte[SockaddrInetSize];
        if (!TryWriteSockaddr(destination, destinationSockaddr))
        {
            return ProbePath.Unresolved;
        }

        uint targetInterfaceIndex = 0;
        if (!string.IsNullOrWhiteSpace(_pinnedInterfaceId))
        {
            var isIpv4 = destination.AddressFamily == AddressFamily.InterNetwork;
            var index = _interfaces.LookupIndex(_pinnedInterfaceId, isIpv4);
            if (index is null or 0)
            {
                return ProbePath.Unresolved;
            }

            targetInterfaceIndex = index.Value;
        }

        Span<byte> row = stackalloc byte[MibIpForwardRow2Size];
        Span<byte> bestSource = stackalloc byte[SockaddrInetSize];

        row.Clear();
        bestSource.Clear();

        var status = GetBestRoute2(
            IntPtr.Zero,
            targetInterfaceIndex,
            IntPtr.Zero,
            MemoryMarshal.GetReference(destinationSockaddr),
            0,
            ref MemoryMarshal.GetReference(row),
            ref MemoryMarshal.GetReference(bestSource));

        if (status != NoError)
        {
            return ProbePath.Unresolved;
        }

        var interfaceIndex = BitConverter.ToUInt32(row[8..12]);
        var source = ReadSockaddr(bestSource);

        if (source is null)
        {
            return ProbePath.Unresolved;
        }

        var resolvedId = _interfaces.Lookup(interfaceIndex);

        if (!string.IsNullOrWhiteSpace(_pinnedInterfaceId) &&
            !WindowsInterfaceResolver.MatchesGuid(resolvedId, _pinnedInterfaceId))
        {
            return ProbePath.Unresolved;
        }

        return new ProbePath(resolvedId, source.ToString(), Resolved: true);
    }

    // ---- SOCKADDR_INET -------------------------------------------------------

    private const int SockaddrInetSize = 28;
    private const int MibIpForwardRow2Size = 256;
    private const uint NoError = 0;
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;

    private static bool TryWriteSockaddr(IPAddress address, Span<byte> destination)
    {
        destination.Clear();

        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                BitConverter.TryWriteBytes(destination, AfInet);
                return address.TryWriteBytes(destination[4..8], out _);

            case AddressFamily.InterNetworkV6:
                BitConverter.TryWriteBytes(destination, AfInet6);
                if (!address.TryWriteBytes(destination[8..24], out _))
                {
                    return false;
                }

                BitConverter.TryWriteBytes(destination[24..28], (uint)address.ScopeId);
                return true;

            default:
                return false;
        }
    }

    private static IPAddress? ReadSockaddr(ReadOnlySpan<byte> value) => BitConverter.ToUInt16(value) switch
    {
        AfInet => new IPAddress(value[4..8]),
        AfInet6 => new IPAddress(value[8..24], BitConverter.ToUInt32(value[24..28])),
        _ => null,
    };

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestRoute2(
        IntPtr interfaceLuid,
        uint interfaceIndex,
        IntPtr sourceAddress,
        in byte destinationAddress,
        uint addressSortOptions,
        ref byte bestRoute,
        ref byte bestSourceAddress);

    private readonly record struct CachedPath(ProbePath Path, long AtTicks)
    {
        public bool IsStale => Stopwatch.GetElapsedTime(AtTicks) > CacheLifetime;
    }

    /// <summary>
    /// Translates the numeric interface index the routing call returns into the stable
    /// adapter identifier everything else in the application uses.
    /// </summary>
    private sealed class InterfaceIndexMap
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

        private readonly Lock _gate = new();
        private Dictionary<uint, string> _byIndex = [];
        private long _builtAtTicks;
        private bool _built;

        public string? Lookup(uint index)
        {
            lock (_gate)
            {
                EnsureBuilt();
                return _byIndex.TryGetValue(index, out var id) ? id : null;
            }
        }

        public uint? LookupIndex(string? interfaceId, bool ipv4)
        {
            if (string.IsNullOrWhiteSpace(interfaceId))
            {
                return null;
            }

            lock (_gate)
            {
                EnsureBuilt();

                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (WindowsInterfaceResolver.MatchesGuid(adapter.Id, interfaceId) ||
                        string.Equals(adapter.Name, interfaceId, StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = IndexOf(adapter, ipv4);
                        if (idx is > 0)
                        {
                            return (uint)idx.Value;
                        }
                    }
                }

                return null;
            }
        }

        public void Invalidate()
        {
            lock (_gate)
            {
                _built = false;
            }
        }

        private void EnsureBuilt()
        {
            if (!_built || Stopwatch.GetElapsedTime(_builtAtTicks) > Lifetime)
            {
                _byIndex = Build();
                _builtAtTicks = Stopwatch.GetTimestamp();
                _built = true;
            }
        }

        private static Dictionary<uint, string> Build()
        {
            var map = new Dictionary<uint, string>();

            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                Add(IndexOf(adapter, IPv4: true), adapter.Id);
                Add(IndexOf(adapter, IPv4: false), adapter.Id);
            }

            return map;

            void Add(int? index, string id)
            {
                if (index is > 0)
                {
                    map[(uint)index.Value] = id;
                }
            }
        }

        private static int? IndexOf(NetworkInterface adapter, bool IPv4)
        {
            try
            {
                var properties = adapter.GetIPProperties();

                return IPv4
                    ? properties.GetIPv4Properties()?.Index
                    : properties.GetIPv6Properties()?.Index;
            }
            catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
            {
                return null;
            }
        }
    }
}
