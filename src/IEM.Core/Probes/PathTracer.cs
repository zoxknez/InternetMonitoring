using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IEM.Core.Probes;

/// <param name="Address">Responding address, or null if the hop did not answer.</param>
/// <param name="RoundTrip">Time to that hop, or null if it did not answer.</param>
public sealed record TraceHop(int Ttl, string? Address, TimeSpan? RoundTrip)
{
    public bool Answered => Address is not null;

    /// <summary>
    /// Whether this hop is inside the home network. The first public address is the
    /// operator's edge, so the boundary between the two is where responsibility changes.
    /// </summary>
    public bool IsPrivate => Address is not null &&
        IPAddress.TryParse(Address, out var parsed) &&
        IsPrivateAddress(parsed);

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                   (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        var bytes = address.GetAddressBytes();

        return bytes[0] switch
        {
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,

            // Carrier-grade NAT. Not the customer's network, but not a public address
            // either - plenty of Serbian subscribers sit behind it.
            100 when bytes[1] >= 64 && bytes[1] <= 127 => true,
            _ => false,
        };
    }
}

/// <param name="ReachedTarget">Whether the trace got all the way to the destination.</param>
public sealed record TraceResult(string Target, IReadOnlyList<TraceHop> Hops, bool ReachedTarget)
{
    /// <summary>
    /// Hops inside the home network, before the operator's edge.
    /// <para>
    /// Counted only up to the first public hop. Private ranges appearing later in the path
    /// are carrier-grade NAT in the middle of the route, not the customer's equipment, and
    /// counting them inflated this figure - which is persisted into the evidence - with
    /// hops that belong to the operator's side.
    /// </para>
    /// </summary>
    public int PrivateHopCount
    {
        get
        {
            var count = 0;
            var pastEdge = false;

            foreach (var hop in Hops)
            {
                if (!hop.IsPrivate)
                {
                    pastEdge = true;
                    continue;
                }

                if (!pastEdge)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// The first hop outside the home network that answered. This is the operator's edge,
    /// and whether it answers is the difference between "the line is down" and "something
    /// further out is down".
    /// </summary>
    public TraceHop? FirstPublicHop => Hops.FirstOrDefault(h => h.Answered && !h.IsPrivate);

    /// <summary>
    /// The furthest hop that answered.
    /// <para>
    /// Read in one direction only. That a hop answered proves the path reached it - that is
    /// solid, and it is the useful half. That nothing answered <em>beyond</em> it proves
    /// nothing about the next hop: routers on the internet are under no obligation to reply
    /// to a expiring packet, and a great many are configured not to. Treating silence as a
    /// located fault would put an accusation about a specific device into a complaint on the
    /// strength of a configuration choice.
    /// </para>
    /// </summary>
    public int? LastAnsweringTtl => Hops.LastOrDefault(h => h.Answered)?.Ttl;

    /// <summary>
    /// The trace never got past the customer's own network.
    /// <para>
    /// This <em>is</em> a strong finding, and it is the asymmetry working the other way: no
    /// public hop answered at all, which is quite different from a particular hop declining
    /// to reply. Combined with the router itself answering, it says the packet never left.
    /// </para>
    /// </summary>
    public bool StopsInsideHomeNetwork =>
        FirstPublicHop is null && Hops.Any(h => h.Answered);

    /// <summary>
    /// What this trace supports, in Serbian, phrased so it can be quoted directly.
    /// </summary>
    public string Interpretation => ReachedTarget
        ? "Putanja je u celosti prošla do mete."
        : FirstPublicHop is { } edge
            ? $"Putanja je dokazano stigla do {edge.Address} (hop {edge.Ttl}), izvan vaše mreže. " +
              "Dalje nema odgovora, ali to samo po sebi ne dokazuje kvar na sledećem uređaju - " +
              "ruteri na internetu ne moraju da odgovaraju na ovu vrstu provere."
            : Hops.Any(h => h.Answered)
                ? "Putanja nije izašla iz vaše lokalne mreže. Nijedan uređaj izvan nje nije odgovorio."
                : "Nijedan hop nije odgovorio, pa trasa ništa ne dokazuje.";
}

/// <summary>
/// Walks the path to a target by increasing the time-to-live one hop at a time.
/// <para>
/// Taken when an incident starts and again when it ends, because it answers a question no
/// amount of ping statistics can: not whether the connection is down, but how far the
/// packets got. A trace that never leaves the home network points at the customer's own
/// equipment; one that reaches the operator's edge proves the packets got that far.
/// </para>
/// <para>
/// What it does <em>not</em> do is locate a fault from silence. Routers are widely
/// configured not to answer expiring packets, so "no reply past hop 4" is a weak signal, not
/// a finding about hop 5.
/// </para>
/// <para>
/// Implemented with TTL-limited pings rather than by running the system tracert, so there
/// is no process to spawn, no output to parse in whatever language Windows is installed
/// in, and the results come back as data rather than as text.
/// </para>
/// </summary>
public static class PathTracer
{
    public static async Task<TraceResult> TraceAsync(
        string target,
        int maxHops = 20,
        TimeSpan? perHopTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        var timeout = perHopTimeout ?? TimeSpan.FromSeconds(1);
        var hops = new List<TraceHop>(maxHops);

        if (!IPAddress.TryParse(target, out var destination))
        {
            return new TraceResult(target, hops, ReachedTarget: false);
        }

        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hop = await ProbeHopAsync(destination, ttl, timeout, cancellationToken).ConfigureAwait(false);
            hops.Add(hop.Hop);

            if (hop.ReachedDestination)
            {
                return new TraceResult(target, hops, ReachedTarget: true);
            }
        }

        return new TraceResult(target, hops, ReachedTarget: false);
    }

    private static async Task<(TraceHop Hop, bool ReachedDestination)> ProbeHopAsync(
        IPAddress destination,
        int ttl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var options = new PingOptions(ttl, dontFragment: true);

        try
        {
            var reply = await ping
                .SendPingAsync(destination, timeout, Array.Empty<byte>(), options, cancellationToken)
                .ConfigureAwait(false);

            return reply.Status switch
            {
                // The expected answer from an intermediate router: it discarded the packet
                // because the hop budget ran out, and told us who it was.
                IPStatus.TtlExpired => (
                    new TraceHop(ttl, reply.Address?.ToString(), TimeSpan.FromMilliseconds(reply.RoundtripTime)),
                    false),

                IPStatus.Success => (
                    new TraceHop(ttl, reply.Address?.ToString(), TimeSpan.FromMilliseconds(reply.RoundtripTime)),
                    true),

                // Silent hop. Common and not itself a fault: plenty of routers decline to
                // answer, so a gap in the middle of a trace means nothing on its own.
                _ => (new TraceHop(ttl, null, null), false),
            };
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            return (new TraceHop(ttl, null, null), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new TraceHop(ttl, null, null), false);
        }
    }
}
