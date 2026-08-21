namespace IEM.Presentation.Models;

using IEM.Core.Model;
using IEM.Presentation.Semantics;

/// <summary>
/// Immutable representation of a time-bucketed slice in the outage history.
/// Invariants:
/// 161. NON_OBSERVABLE_HOST_INTERVAL_IS_NEVER_VISUALIZED_AS_NETWORK_OUTAGE
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
/// <param name="Severity">Worst domain severity seen in this slice of time.</param>
public readonly record struct TimelineSlice(Severity Severity)
{
    /// <summary>
    /// Platform-neutral semantic tone strictly derived from Severity authority (Invariant 170).
    /// </summary>
    public SemanticTone Tone => Severity switch
    {
        Severity.Ok => SemanticTone.Good,
        Severity.Degraded => SemanticTone.Warning,
        Severity.Outage => SemanticTone.Bad,
        Severity.Info => SemanticTone.Info,
        _ => SemanticTone.Unknown,
    };
}
