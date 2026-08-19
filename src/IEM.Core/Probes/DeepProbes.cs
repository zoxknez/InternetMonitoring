using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using IEM.Core.Model;

namespace IEM.Core.Probes;

/// <summary>
/// Probes that are slower and heavier than a ping: name resolution, a TLS handshake, the
/// connectivity endpoint. They run on their own schedule - the scheduler's deep loop, woken early when the fast probes see trouble
/// rather than inside the sampling cycle.
/// </summary>
public static class DeepProbes
{
    /// <param name="source">
    /// Source address the query is sent from. Binding both the operator's resolver and the
    /// public one to the same source is what makes comparing them meaningful - unbound, the
    /// two queries could leave the machine by different routes entirely.
    /// </param>
    public static async Task<ProbeResult> DnsAsync(
        string resolver,
        DnsResolverRole role,
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IPAddress? source = null)
    {
        var result = await DnsQuery
            .ResolveAsync(resolver, name, timeout, cancellationToken, source)
            .ConfigureAwait(false);

        var outcome = result.Succeeded
            ? ProbeOutcome.Success
            : (result.BindFailed
                ? ProbeOutcome.Skipped
                : (result.Error == "Timed out" ? ProbeOutcome.TimedOut : ProbeOutcome.Failed));

        return new ProbeResult(
            ProbeKind.Dns,
            ProbeScope.External,
            resolver,
            outcome,
            result.Succeeded ? result.Elapsed : null,
            result.Error)
        {
            DnsRole = role,
        };
    }

    /// <summary>
    /// Resolves through whatever the operating system is configured to use.
    /// Kept alongside the direct queries because it is what actual applications experience.
    /// </summary>
    public static async Task<ProbeResult> SystemDnsAsync(
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var addresses = await System.Net.Dns
                .GetHostAddressesAsync(name, timeoutSource.Token)
                .ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(started);
            var ok = addresses.Length > 0;

            return new ProbeResult(
                ProbeKind.Dns,
                ProbeScope.External,
                "system",
                ok ? ProbeOutcome.Success : ProbeOutcome.Failed,
                ok ? elapsed : null,
                ok ? null : "No addresses returned")
            {
                DnsRole = DnsResolverRole.System,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(
                ProbeKind.Dns,
                ProbeScope.External,
                "system",
                ProbeOutcome.TimedOut,
                null,
                "Timed out")
            {
                DnsRole = DnsResolverRole.System,
            };
        }
        catch (SocketException ex)
        {
            return Failed(ProbeKind.Dns, "system", ex.SocketErrorCode.ToString(), DnsResolverRole.System);
        }
    }

    /// <summary>
    /// Completes a TLS handshake, proving that encrypted traffic - which is what the
    /// customer actually uses all day - gets through, not merely that packets route.
    /// </summary>
    public static async Task<ProbeResult> TlsAsync(
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var separator = target.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(target[(separator + 1)..], out var port))
        {
            return Failed(ProbeKind.TlsHandshake, target, "Unparseable endpoint");
        }

        var host = target[..separator];

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutSource.Token).ConfigureAwait(false);

            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host },
                timeoutSource.Token).ConfigureAwait(false);

            return new ProbeResult(
                ProbeKind.TlsHandshake,
                ProbeScope.External,
                target,
                ProbeOutcome.Success,
                Stopwatch.GetElapsedTime(started),
                ssl.SslProtocol.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(
                ProbeKind.TlsHandshake,
                ProbeScope.External,
                target,
                ProbeOutcome.TimedOut,
                null,
                "Timed out");
        }
        catch (Exception ex) when (ex is SocketException or AuthenticationException or IOException)
        {
            return Failed(ProbeKind.TlsHandshake, target, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches the connectivity endpoint Windows itself uses.
    /// <para>
    /// A reachable endpoint returning the wrong body is the signature of an intercepting
    /// portal. That is reported as suspected rather than confirmed: HSTS preloading and
    /// HTTPS-first browsing have made plain-HTTP portal detection unreliable enough that
    /// stating it as fact would be overreach.
    /// </para>
    /// </summary>
    public static async Task<ProbeResult> HttpAsync(
        HttpClient client,
        string url,
        string expectedBody,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await client.GetAsync(url, timeoutSource.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            var statusCode = (int)response.StatusCode;
            var is2xx = response.IsSuccessStatusCode;
            var is3xx = statusCode is >= 300 and < 400;
            var bodyMatches = string.IsNullOrEmpty(expectedBody) || body.Contains(expectedBody, StringComparison.Ordinal);
            var ok = is2xx && bodyMatches;

            // Captive Portal Suspected semantics:
            // - 2xx with unexpected body: intercepting web portal returned modified/login page.
            // - 3xx redirect: redirecting to captive login portal.
            // - 4xx / 5xx: regular server error, NOT suspected captive portal.
            var portal = (is2xx && !bodyMatches) || is3xx;

            var detail = ok
                ? $"HTTP {statusCode}"
                : (portal
                    ? $"HTTP {statusCode}, captive portal suspected"
                    : $"HTTP {statusCode}");

            return new ProbeResult(
                ProbeKind.Http,
                ProbeScope.External,
                url,
                ok ? ProbeOutcome.Success : ProbeOutcome.Failed,
                ok ? elapsed : null,
                detail)
            {
                CaptivePortalSuspected = portal,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(
                ProbeKind.Http,
                ProbeScope.External,
                url,
                ProbeOutcome.TimedOut,
                null,
                "Timed out");
        }
        catch (HttpRequestException ex)
        {
            return Failed(ProbeKind.Http, url, $"{ex.HttpRequestError}: {ex.Message}");
        }
    }

    private static ProbeResult Failed(ProbeKind kind, string target, string detail, DnsResolverRole? role = null) =>
        new(kind, ProbeScope.External, target, ProbeOutcome.Failed, null, detail)
        {
            DnsRole = role,
        };
}
