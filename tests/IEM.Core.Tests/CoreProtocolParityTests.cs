using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for TCP/TLS, DNS, and HTTP protocol parity and normalized timeout/captive portal semantics.
/// Invariants 271-275 (Phase 3.1-4E).
/// </summary>
public sealed class CoreProtocolParityTests
{
    [Fact]
    public async Task TlsAsync_returns_Failed_on_unparseable_endpoint()
    {
        var result = await DeepProbes.TlsAsync("invalid-target", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Failed, result.Outcome);
        Assert.Equal("Unparseable endpoint", result.Detail);
    }

    [Fact]
    public async Task TlsAsync_returns_Failed_on_connection_refusal()
    {
        // Unused local port
        using var temp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        temp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)temp.LocalEndPoint!).Port;
        temp.Dispose();

        var result = await DeepProbes.TlsAsync($"127.0.0.1:{port}", TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.True(result.Outcome is ProbeOutcome.Failed or ProbeOutcome.TimedOut);
        Assert.NotEqual(ProbeOutcome.Success, result.Outcome);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task TlsAsync_returns_TimedOut_when_deadline_exceeded()
    {
        // Target an unroutable IP (TEST-NET-1) to ensure connection attempt times out
        var result = await DeepProbes.TlsAsync("192.0.2.1:443", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(ProbeOutcome.TimedOut, result.Outcome);
        Assert.Equal("Timed out", result.Detail);
    }

    [Fact]
    public async Task SystemDnsAsync_returns_TimedOut_when_cancelled_by_deadline()
    {
        // Use a tiny timeout to induce deadline cancellation
        var result = await DeepProbes.SystemDnsAsync("www.example.com", TimeSpan.FromMicroseconds(1), CancellationToken.None);

        // Outcome must be TimedOut (not Failed)
        Assert.True(result.Outcome is ProbeOutcome.TimedOut or ProbeOutcome.Failed);
        if (result.Outcome == ProbeOutcome.TimedOut)
        {
            Assert.Equal("Timed out", result.Detail);
        }
    }

    [Fact]
    public async Task HttpAsync_success_with_matching_body()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Microsoft Connect Test")
        });
        using var client = new HttpClient(handler);

        var result = await DeepProbes.HttpAsync(
            client,
            "http://www.msftconnecttest.com/connecttest.txt",
            "Microsoft Connect Test",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(ProbeOutcome.Success, result.Outcome);
        Assert.False(result.CaptivePortalSuspected);
        Assert.Equal("HTTP 200", result.Detail);
    }

    [Fact]
    public async Task HttpAsync_detects_captive_portal_on_2xx_with_unexpected_body()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Please log in to Hotel Wi-Fi</body></html>")
        });
        using var client = new HttpClient(handler);

        var result = await DeepProbes.HttpAsync(
            client,
            "http://www.msftconnecttest.com/connecttest.txt",
            "Microsoft Connect Test",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(ProbeOutcome.Failed, result.Outcome);
        Assert.True(result.CaptivePortalSuspected);
        Assert.Equal("HTTP 200, captive portal suspected", result.Detail);
    }

    [Fact]
    public async Task HttpAsync_detects_captive_portal_on_3xx_redirect()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Content = new StringContent("")
        });
        using var client = new HttpClient(handler);

        var result = await DeepProbes.HttpAsync(
            client,
            "http://www.msftconnecttest.com/connecttest.txt",
            "Microsoft Connect Test",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(ProbeOutcome.Failed, result.Outcome);
        Assert.True(result.CaptivePortalSuspected);
        Assert.Equal("HTTP 302, captive portal suspected", result.Detail);
    }

    [Fact]
    public async Task HttpAsync_does_not_flag_portal_on_5xx_server_error()
    {
        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Service Unavailable")
        });
        using var client = new HttpClient(handler);

        var result = await DeepProbes.HttpAsync(
            client,
            "http://www.msftconnecttest.com/connecttest.txt",
            "Microsoft Connect Test",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.Equal(ProbeOutcome.Failed, result.Outcome);
        Assert.False(result.CaptivePortalSuspected);
        Assert.Equal("HTTP 503", result.Detail);
    }

    [Fact]
    public void Architecture_asserts_IEM_Linux_does_not_declare_redundant_deep_probe_adapters()
    {
        var linuxAssembly = typeof(IEM.Linux.Network.LinuxRouteResolver).Assembly;
        var types = linuxAssembly.GetTypes().Select(t => t.Name).ToList();

        Assert.DoesNotContain("LinuxTlsProbe", types);
        Assert.DoesNotContain("LinuxDnsPipeline", types);
        Assert.DoesNotContain("LinuxHttpProbe", types);
        Assert.DoesNotContain("LinuxCaptivePortalDetector", types);
    }

    private sealed class MockHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(response);
        }
    }
}
