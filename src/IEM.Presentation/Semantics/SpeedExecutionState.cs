namespace IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral speed test lifecycle execution state.
/// Invariants:
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
/// </summary>
public enum SpeedExecutionState
{
    NotRun,
    Executing,
    Refused,
    Succeeded
}
