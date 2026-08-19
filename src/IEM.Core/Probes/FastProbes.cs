using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IEM.Core.Model;

namespace IEM.Core.Probes;

/// <summary>
/// Probes cheap enough to run on every cycle, including at 100 ms during an incident.
/// <para>
/// All of them are asynchronous and are launched together, so a cycle costs about as
/// long as its slowest probe rather than the sum of all of them. That is what makes
/// sub-second outage boundaries possible: the old sequential approach spent up to four
/// seconds per sample precisely when every millisecond mattered.
/// </para>
/// </summary>
public static class FastProbes
{
    /// <param name="source">Source address to send from, when the path is known.</param>
    /// <param name="bound">
    /// Platform sender that can honour <paramref name="source"/>. Null, or a null answer
    /// from it, falls back to the framework - and the result then says it was not bound
    /// rather than pretending otherwise.
    /// </param>
    public static async Task<ProbeResult> IcmpAsync(
        string target,
        ProbeScope scope,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IPAddress? source = null,
        IBoundIcmp? bound = null)
    {
        if (!IPAddress.TryParse(target, out var address))
        {
            return new ProbeResult(ProbeKind.Icmp, scope, target, ProbeOutcome.Failed, null, "Unparseable address");
        }

        if (bound is not null && source is not null && source.AddressFamily == address.AddressFamily)
        {
            var echo = await bound.SendAsync(address, source, timeout, cancellationToken).ConfigureAwait(false);

            if (echo is { } value)
            {
                return new ProbeResult(
                    ProbeKind.Icmp,
                    scope,
                    target,
                    // A non-timeout status means the stack would not send at all - a local
                    // problem (stale bound source, resource exhaustion) that must never be
                    // charged to the operator as a lost packet. Skipped, like a down adapter.
                    value.Succeeded ? ProbeOutcome.Success : value.TimedOut ? ProbeOutcome.TimedOut : ProbeOutcome.Skipped,
                    value.Succeeded ? value.RoundTrip : null,
                    value.Succeeded ? null : $"IP_STATUS {value.Status}")
                {
                    Family = address.AddressFamily,
                };
            }
        }

        // Ping is not safe for concurrent sends on one instance, so each probe gets its own.
        using var ping = new Ping();

        try
        {
            var reply = await ping.SendPingAsync(address, timeout, cancellationToken: cancellationToken)
                                  .ConfigureAwait(false);

            var outcome = reply.Status switch
            {
                IPStatus.Success => ProbeOutcome.Success,
                IPStatus.TimedOut => ProbeOutcome.TimedOut,
                _ => ProbeOutcome.Failed,
            };

            return new ProbeResult(
                ProbeKind.Icmp,
                scope,
                target,
                outcome,
                outcome == ProbeOutcome.Success ? TimeSpan.FromMilliseconds(reply.RoundtripTime) : null,
                reply.Status.ToString())
            {
                Family = address.AddressFamily,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Timeout(ProbeKind.Icmp, scope, target, address.AddressFamily);
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            return new ProbeResult(ProbeKind.Icmp, scope, target, ProbeOutcome.Failed, null, Describe(ex))
            {
                Family = address.AddressFamily,
            };
        }
    }

    /// <summary>
    /// Opens a TCP connection and closes it again.
    /// <para>
    /// This is the probe that keeps a filtered-ICMP network from being reported as an
    /// outage. Plenty of paths drop ping while carrying traffic perfectly well, and
    /// calling that downtime would manufacture incidents that never happened.
    /// </para>
    /// </summary>
    /// <param name="source">
    /// Source address to connect from, so the attempt provably left through the monitored
    /// link. Null lets the stack choose.
    /// </param>
    public static async Task<ProbeResult> TcpConnectAsync(
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IPAddress? source = null)
    {
        if (!TryParseEndpoint(target, out var endpoint))
        {
            return new ProbeResult(
                ProbeKind.TcpConnect, ProbeScope.External, target, ProbeOutcome.Failed, null, "Unparseable endpoint");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var started = Stopwatch.GetTimestamp();

        try
        {
            if (source is not null && source.AddressFamily == endpoint.AddressFamily)
            {
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                }
                catch (SocketException ex)
                {
                    return new ProbeResult(
                        ProbeKind.TcpConnect,
                        ProbeScope.External,
                        target,
                        ProbeOutcome.Skipped,
                        null,
                        $"LocalBindFailed:{ex.SocketErrorCode}")
                    {
                        Family = endpoint.AddressFamily,
                    };
                }
            }

            await socket.ConnectAsync(endpoint, timeoutSource.Token).ConfigureAwait(false);

            return new ProbeResult(
                ProbeKind.TcpConnect,
                ProbeScope.External,
                target,
                ProbeOutcome.Success,
                Stopwatch.GetElapsedTime(started))
            {
                Family = endpoint.AddressFamily,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Timeout(ProbeKind.TcpConnect, ProbeScope.External, target, endpoint.AddressFamily);
        }
        catch (SocketException ex)
        {
            return new ProbeResult(
                ProbeKind.TcpConnect, ProbeScope.External, target, ProbeOutcome.Failed, null, ex.SocketErrorCode.ToString())
            {
                Family = endpoint.AddressFamily,
            };
        }
    }

    internal static bool TryParseEndpoint(string value, out IPEndPoint endpoint)
    {
        endpoint = default!;

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        var host = value[..separator];
        if (!int.TryParse(value[(separator + 1)..], out var port) || port is < 1 or > 65535)
        {
            return false;
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return false;
        }

        endpoint = new IPEndPoint(address, port);
        return true;
    }

    private static ProbeResult Timeout(ProbeKind kind, ProbeScope scope, string target, AddressFamily family) =>
        new(kind, scope, target, ProbeOutcome.TimedOut, null, "Timed out")
        {
            Family = family,
        };

    private static string Describe(Exception ex) =>
        ex.InnerException is SocketException socket
            ? $"{ex.GetType().Name}: {socket.SocketErrorCode}"
            : $"{ex.GetType().Name}: {ex.Message}";
}
