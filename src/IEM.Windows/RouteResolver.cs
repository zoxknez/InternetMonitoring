using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Windows;

/// <summary>
/// Answers the routing question the way Windows answers it, by asking Windows.
/// <para>
/// <c>GetBestRoute2</c> is the same lookup the stack performs when a socket connects, so its
/// answer is not an approximation of the choice - it is the choice. That matters because the
/// alternative on offer is guesswork: pick the adapter with the best metric, or the one the
/// user selected in the interface, and hope. On a laptop with Wi-Fi, a docking-station
/// Ethernet and a corporate VPN, that guess is wrong often enough to put a fabricated
/// attribution into someone's complaint.
/// </para>
/// <para>
/// Answers are cached briefly. The route table does change - that is the whole point of
/// P0-8 - but not between two probes fired a hundred milliseconds apart, and the lookup is
/// a system call that would otherwise run several times per sample.
/// </para>
/// </summary>
public sealed class RouteResolver : IRouteResolver
{
    /// <summary>
    /// How long a resolved route is reused.
    /// <para>
    /// Short enough that a failover from Wi-Fi to Ethernet is noticed within a second or two
    /// - which is what makes a route change during an incident detectable at all - and long
    /// enough that the lookup does not run on every probe of every sample.
    /// </para>
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<IPAddress, CachedPath> _cache = new();
    private readonly InterfaceIndexMap _interfaces = new();

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

        // The row is read only for its first twelve bytes - the interface LUID and index.
        // Over-allocating avoids having to reproduce the exact layout of a struct whose
        // tail has grown across Windows versions.
        Span<byte> row = stackalloc byte[MibIpForwardRow2Size];
        Span<byte> bestSource = stackalloc byte[SockaddrInetSize];

        row.Clear();
        bestSource.Clear();

        var status = GetBestRoute2(
            IntPtr.Zero,
            0,
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

        return new ProbePath(_interfaces.Lookup(interfaceIndex), source.ToString(), Resolved: true);
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
                if (!_built || Stopwatch.GetElapsedTime(_builtAtTicks) > Lifetime)
                {
                    _byIndex = Build();
                    _builtAtTicks = Stopwatch.GetTimestamp();
                    _built = true;
                }

                return _byIndex.TryGetValue(index, out var id) ? id : null;
            }
        }

        public void Invalidate()
        {
            lock (_gate)
            {
                _built = false;
            }
        }

        private static Dictionary<uint, string> Build()
        {
            var map = new Dictionary<uint, string>();

            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Each family is read separately. An adapter with IPv4 configured and IPv6
                // not will throw on the second lookup, and one try block around both would
                // then lose the index that did work.
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
                // The family is not configured on this adapter, or the adapter went away
                // mid-enumeration. Neither is worth failing the whole map over.
                return null;
            }
        }
    }
}
