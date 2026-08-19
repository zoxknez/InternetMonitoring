using System.Net;
using IEM.Core.Hosting;
using IEM.Core.Probes;

namespace IEM.Linux.Network;

/// <summary>
/// Platform factory supplying Linux network probes and FIB route resolvers.
/// Invariants 211, 271-275.
/// </summary>
public sealed class LinuxProbeFactory : IPlatformProbeFactory
{
    public static LinuxProbeFactory Instance { get; } = new();

    public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null)
    {
        var inspector = new SystemLinkInspector(interfaceName);
        return ValueTask.FromResult<IPlatformLinkInspectionScope>(new BasicLinkInspectionScope(inspector));
    }

    public IRouteResolver CreateRouteResolver() => new LinuxRouteResolver();

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
            return Task.FromResult<IcmpEcho?>(null);
        }
    }
}
