namespace IEM.Presentation.States;

using IEM.Presentation.Models;
using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral, immutable presentation state projecting live session observations and health.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// 161. NON_OBSERVABLE_HOST_INTERVAL_IS_NEVER_VISUALIZED_AS_NETWORK_OUTAGE
/// </summary>
public sealed record MonitorPresentationState(
    string TargetHealthSummary,
    string ProbeHealthSummary,
    BadgeKind QualityBand,
    string QualityBandText,
    string TotalDuration,
    string ActiveDuration,
    string SuspendDuration,
    int? InterruptionsCount,
    SemanticTone Tone,
    IReadOnlyList<MonitorTimelinePresentationItem> TimelineItems)
{
    public static MonitorPresentationState Initial { get; } = new(
        TargetHealthSummary: "Nema aktivnih merenja (No data yet)",
        ProbeHealthSummary: "Sonde u stanju pripravnosti",
        QualityBand: BadgeKind.Unknown,
        QualityBandText: "Nepoznato",
        TotalDuration: "—",
        ActiveDuration: "—",
        SuspendDuration: "—",
        InterruptionsCount: null,
        Tone: SemanticTone.Unknown,
        TimelineItems: Array.Empty<MonitorTimelinePresentationItem>());
}
