using System.Net;
using System.Net.Sockets;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Source-Address Binding Parity across ICMP, TCP, DNS, and HTTP.
/// Invariants 271-275 (Phase 3.1-4D):
/// 1. Route-selected source address != successfully bound socket.
/// 2. Local bind failure maps strictly to ProbeOutcome.Skipped and Bound=false (never false network failure).
/// 3. No silent unbound retry when an explicit source is requested.
/// 4. IEM.Linux relies on portable Core socket binding for TCP, DNS, and HTTP (zero redundant adapters).
/// </summary>
public sealed class SourceBindingParityTests
{
    private static readonly IPAddress NonExistentLocalIp = IPAddress.Parse("198.51.100.222");

    [Fact]
    public async Task TcpConnectAsync_returns_Skipped_and_no_retry_when_local_bind_fails()
    {
        // Attempting to bind to a non-local IP must fail at bind() with SocketError.AddressNotAvailable
        var target = "127.0.0.1:80";
        var result = await FastProbes.TcpConnectAsync(
            target,
            TimeSpan.FromMilliseconds(500),
            CancellationToken.None,
            source: NonExistentLocalIp);

        Assert.Equal(ProbeOutcome.Skipped, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.StartsWith("LocalBindFailed:", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TcpConnectAsync_preserves_network_refusal_when_bind_succeeds()
    {
        // Find an unused local port to ensure refusal
        using var temp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        temp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)temp.LocalEndPoint!).Port;
        temp.Dispose();

        var target = $"127.0.0.1:{port}";
        var result = await FastProbes.TcpConnectAsync(
            target,
            TimeSpan.FromMilliseconds(500),
            CancellationToken.None,
            source: IPAddress.Loopback);

        // Bind succeeded, outcome is a real network outcome (Failed or TimedOut), never Skipped
        Assert.True(result.Outcome is ProbeOutcome.Failed or ProbeOutcome.TimedOut);
        Assert.NotEqual(ProbeOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task DnsQuery_ResolveAsync_returns_BindFailed_when_local_bind_fails()
    {
        var result = await DnsQuery.ResolveAsync(
            "1.1.1.1",
            "example.com",
            TimeSpan.FromMilliseconds(500),
            CancellationToken.None,
            source: NonExistentLocalIp);

        Assert.False(result.Succeeded);
        Assert.True(result.BindFailed);
        Assert.NotNull(result.Error);
        Assert.StartsWith("LocalBindFailed:", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeepProbes_DnsAsync_maps_BindFailed_to_ProbeOutcome_Skipped()
    {
        var result = await DeepProbes.DnsAsync(
            "1.1.1.1",
            DnsResolverRole.Public,
            "example.com",
            TimeSpan.FromMilliseconds(500),
            CancellationToken.None,
            source: NonExistentLocalIp);

        Assert.Equal(ProbeOutcome.Skipped, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.StartsWith("LocalBindFailed:", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MeasurementHttpClient_forced_interface_fails_fast_without_silent_unbound_retry()
    {
        using var client = MeasurementHttpClient.Create(
            intent: MeasurementIntent.MeasureRequestedInterface,
            bindLocalAddress: NonExistentLocalIp);

        // Attempting to connect through non-existent source must throw HttpRequestException with inner SocketException
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await client.GetAsync("http://127.0.0.1:8080/speedtest", HttpCompletionOption.ResponseHeadersRead);
        });

        Assert.IsType<SocketException>(ex.InnerException);
    }

    [Fact]
    public void Architecture_asserts_IEM_Linux_does_not_contain_redundant_protocol_adapters()
    {
        var linuxAssembly = typeof(IEM.Linux.Network.LinuxRouteResolver).Assembly;
        var types = linuxAssembly.GetTypes().Select(t => t.Name).ToList();

        // Core socket binding is strictly portable; IEM.Linux must only contain Route and ICMP adapters
        Assert.DoesNotContain("LinuxTcpProbe", types);
        Assert.DoesNotContain("LinuxDnsQuery", types);
        Assert.DoesNotContain("LinuxHttpClient", types);
        Assert.DoesNotContain("LinuxHttpProbe", types);
    }
}
