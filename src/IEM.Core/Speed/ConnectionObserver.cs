using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IEM.Core.Speed;

/// <summary>What the measurement was asked to do about its path.</summary>
/// <remarks>
/// Two different questions, not a flag on a socket. "Which way does the system actually send
/// this?" and "can I measure over this particular adapter, and how fast?" have different
/// answers and prove different things - a figure taken over a socket pinned to one adapter
/// says what that adapter can do, not which adapter ordinary traffic would use. Keeping them
/// apart in the record is what stops a report a year from now conflating the two.
/// </remarks>
public enum MeasurementIntent
{
    /// <summary>Nothing is forced. The question is which way the system chooses.</summary>
    ObserveSystemPath,

    /// <summary>The socket is deliberately bound to a chosen adapter. Not implemented yet.</summary>
    MeasureRequestedInterface,
}

/// <param name="Id">The adapter's stable identifier, as the route table and the link inspector use it.</param>
public sealed record NetworkInterfaceIdentity(string Id, string Name);

/// <summary>
/// One connection the measurement actually opened.
/// <para>
/// A fact: these endpoints were negotiated by this socket at this moment. Nothing here is
/// inferred, which is why the interface is recorded as what the local address resolved to and
/// left null when it resolved to nothing.
/// </para>
/// </summary>
public sealed record ConnectionAttempt(
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress RemoteAddress,
    int RemotePort,
    DateTimeOffset ConnectedAtUtc)
{
    public AddressFamily Family => RemoteAddress.AddressFamily;

    /// <summary>The intent under which this connection was opened.</summary>
    public MeasurementIntent Intent { get; init; } = MeasurementIntent.ObserveSystemPath;

    /// <summary>The adapter that owns <see cref="LocalAddress"/>, or null when none could be matched.</summary>
    public NetworkInterfaceIdentity? Observed { get; init; }

    public override string ToString() =>
        $"{LocalAddress}:{LocalPort} → {RemoteAddress}:{RemotePort}" +
        (Observed is { } via ? $" ({via.Name})" : " (adapter nije utvrđen)");
}

/// <summary>
/// Which adapter owns a local address.
/// <para>
/// A seam so the rules built on it can be tested without a network stack. The rule itself is
/// small - match the address against each adapter's unicast addresses - but it is the step
/// that turns a socket's endpoint into a statement about an adapter, and that step has to be
/// checkable.
/// </para>
/// </summary>
public interface ILocalAddressMap
{
    NetworkInterfaceIdentity? For(IPAddress localAddress);

    /// <summary>Finds a local IP address assigned to the given interface.</summary>
    IPAddress? FindAddressForInterface(string interfaceId, AddressFamily family = AddressFamily.InterNetwork);
}

/// <summary>Reads the machine's own adapters. Portable; no platform API beyond the framework.</summary>
public sealed class SystemLocalAddressMap : ILocalAddressMap
{
    public NetworkInterfaceIdentity? For(IPAddress localAddress)
    {
        ArgumentNullException.ThrowIfNull(localAddress);

        var wanted = Unmap(localAddress);

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (Unmap(unicast.Address).Equals(wanted))
                {
                    return new NetworkInterfaceIdentity(adapter.Id, adapter.Name);
                }
            }
        }

        // Null rather than a guess. An address the machine does not claim is a finding in its
        // own right, and inventing an adapter for it would be the substitution this project
        // spends its releases removing.
        return null;
    }

    public IPAddress? FindAddressForInterface(string interfaceId, AddressFamily family = AddressFamily.InterNetwork)
    {
        ArgumentNullException.ThrowIfNull(interfaceId);

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(adapter.Id, interfaceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var addr = Unmap(unicast.Address);
                if (addr.AddressFamily == family && !IPAddress.IsLoopback(addr))
                {
                    return addr;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// An IPv4 address written the long way round, back to itself.
    /// <para>
    /// A dual-stack socket reports an IPv4 connection as <c>::ffff:192.168.1.102</c>, while the
    /// adapter holds plain <c>192.168.1.102</c>, and the two are not equal. That is how the
    /// first live run came back with six connections and not one of them placed: every address
    /// on the machine was compared against a form no adapter ever uses.
    /// </para>
    /// </summary>
    internal static IPAddress Unmap(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>
/// Collects the connections a measurement opened, as they open.
/// <para>
/// Until now the strongest thing that could be said about a speed figure was that the route
/// table agreed with the chosen adapter - a statement about what the operating system would
/// choose, not about what the transfer did. This is the other half: the socket's own endpoints,
/// recorded at the moment it connected.
/// </para>
/// </summary>
public sealed class ConnectionObserver(ILocalAddressMap? addresses = null, IEM.Core.Time.IClock? clock = null)
{
    private readonly ILocalAddressMap _addresses = addresses ?? new SystemLocalAddressMap();
    private readonly IEM.Core.Time.IClock _clock = clock ?? IEM.Core.Time.SystemClock.Instance;
    private readonly List<ConnectionAttempt> _attempts = [];
    private readonly Lock _gate = new();

    /// <summary>Every connection observed so far, oldest first.</summary>
    public IReadOnlyList<ConnectionAttempt> Attempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _attempts];
            }
        }
    }

    /// <summary>
    /// Records a connected socket. Called from the connect callback, on several threads at
    /// once - the transfer opens three connections per direction.
    /// </summary>
    public void Record(EndPoint? local, EndPoint? remote, MeasurementIntent intent = MeasurementIntent.ObserveSystemPath)
    {
        if (local is not IPEndPoint from || remote is not IPEndPoint to)
        {
            return;
        }

        // Recorded unmapped, because the mapped form is ours and not the network's: the socket
        // is opened dual-stack, so an IPv4 connection arrives written as ::ffff:a.b.c.d. Left
        // as it comes, every IPv4 measurement on this machine would be filed as IPv6 - and the
        // mixed-family case is precisely what this record exists to catch.
        var fromAddress = SystemLocalAddressMap.Unmap(from.Address);
        var toAddress = SystemLocalAddressMap.Unmap(to.Address);

        var attempt = new ConnectionAttempt(fromAddress, from.Port, toAddress, to.Port, _clock.UtcNow)
        {
            Intent = intent,
            Observed = _addresses.For(fromAddress),
        };

        lock (_gate)
        {
            _attempts.Add(attempt);
        }
    }
}

