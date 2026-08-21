namespace IEM.Presentation.Models;

using IEM.Core.Model;
using IEM.Presentation.Semantics;

/// <summary>
/// Immutable representation of a time-bucketed slice in the outage history.
/// </summary>
/// <param name="Severity">Worst domain severity seen in this slice of time.</param>
/// <param name="Tone">Platform-neutral semantic tone for renderer styling.</param>
public readonly record struct TimelineSlice(Severity Severity, SemanticTone Tone)
{
    public TimelineSlice(Severity severity)
        : this(severity, MapSeverityToTone(severity))
    {
    }

    private static SemanticTone MapSeverityToTone(Severity severity) => severity switch
    {
        Severity.Ok => SemanticTone.Good,
        Severity.Degraded => SemanticTone.Warning,
        Severity.Outage => SemanticTone.Bad,
        Severity.Info => SemanticTone.Info,
        _ => SemanticTone.Unknown,
    };
}
