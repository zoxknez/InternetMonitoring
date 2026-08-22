namespace IEM.Presentation.Contracts;

using IEM.Core.Presentation;

/// <summary>
/// Explicit input required to project authoritative speed measurement state without ambient/disk reads.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 152. LIVE_UI_CONSUMES_IMMUTABLE_VERSIONED_PRESENTATION_SNAPSHOTS
/// 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
/// </summary>
public sealed record SpeedProjectionInput(
    PresentationSnapshot Snapshot,
    SpeedExecutionFacts Execution);
