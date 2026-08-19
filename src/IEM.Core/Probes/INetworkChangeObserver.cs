using System.Net;
using IEM.Core.Model;

namespace IEM.Core.Probes;

/// <summary>
/// Asynchronously observes kernel routing and link state changes, tracking RouteGeneration
/// and providing TOCTOU path continuity evaluation.
/// Invariants 247 &amp; §5.4-§5.8 (Phase 3.1-4G).
/// </summary>
public interface INetworkChangeObserver : IAsyncDisposable
{
    /// <summary>Monotonically increasing counter incremented whenever routing/link/address state changes.</summary>
    ulong RouteGeneration { get; }

    /// <summary>True when kernel multicast change subscriptions are actively established and streaming events.</summary>
    bool IsLive { get; }

    /// <summary>Fired whenever a relevant route, link, or address change event is received from the kernel.</summary>
    event Action<ulong>? RouteChanged;

    /// <summary>
    /// Evaluates whether the given probe path remained held during the [startedTicks, completedTicks] execution window.
    /// </summary>
    PathContinuity EvaluateContinuity(long startedTicks, long completedTicks, ProbePath path, IPAddress? destination = null);
}

/// <summary>Default no-op observer returning PathContinuity.Unknown without synthetic provenance.</summary>
public sealed class NullNetworkChangeObserver : INetworkChangeObserver
{
    public static readonly NullNetworkChangeObserver Instance = new();

    public ulong RouteGeneration => 1;
    public bool IsLive => false;
    public event Action<ulong>? RouteChanged { add { } remove { } }

    public PathContinuity EvaluateContinuity(long startedTicks, long completedTicks, ProbePath path, IPAddress? destination = null) =>
        PathContinuity.Unknown;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
