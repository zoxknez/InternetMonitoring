namespace IEM.Presentation.Contracts;

using System.Collections.Immutable;
using IEM.Presentation.Models;

/// <summary>
/// Time-bucketed timeline and latency point collections for rolling chart visualization.
/// </summary>
public sealed record HistoryPresentationState(
    ImmutableArray<TimelineSlice> Timeline,
    ImmutableArray<LatencyPoint> Latency)
{
    public static HistoryPresentationState Empty { get; } = new(
        ImmutableArray<TimelineSlice>.Empty,
        ImmutableArray<LatencyPoint>.Empty);
}
