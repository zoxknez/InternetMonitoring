using System.Net;
using IEM.Core.Hosting;
using IEM.Core.Probes;

namespace IEM.Service.Linux.Lifecycle;

/// <summary>
/// Baseline stub for power events on Linux until logind D-Bus integration is added in Phase 3.1-8.
/// </summary>
public sealed class LinuxPowerEventSourceStub : IPowerEventSource
{
    public static readonly LinuxPowerEventSourceStub Instance = new();

    public IDisposable OnSuspending(Action callback) => new NullSubscription();

    public void Dispose() { }

    private sealed class NullSubscription : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// Baseline probe factory for Phase 3.1-2.
/// Supplies standard platform link inspection and routing until rtnetlink (3.1-4)
/// and nl80211 (3.1-7) platform adapters are implemented in their dedicated phases.
/// </summary>
public sealed class LinuxProbeFactoryBaseline : IPlatformProbeFactory
{
    public static readonly LinuxProbeFactoryBaseline Instance = new();

    public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null)
    {
        var inspector = new SystemLinkInspector(interfaceName);
        return ValueTask.FromResult<IPlatformLinkInspectionScope>(new BasicLinkInspectionScope(inspector));
    }

    public IRouteResolver CreateRouteResolver() => NullRouteResolver.Instance;

    public IBoundIcmp CreateBoundIcmp() => FallbackBoundIcmp.Instance;

    private sealed class BasicLinkInspectionScope(ILinkInspector inspector) : IPlatformLinkInspectionScope
    {
        public ILinkInspector Inspector { get; } = inspector;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FallbackBoundIcmp : IBoundIcmp
    {
        public static readonly FallbackBoundIcmp Instance = new();

        public Task<IcmpEcho?> SendAsync(
            IPAddress destination,
            IPAddress source,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            // Null indicates unbound / fallback path until Linux ICMP implementation in 3.1-6
            return Task.FromResult<IcmpEcho?>(null);
        }
    }
}
