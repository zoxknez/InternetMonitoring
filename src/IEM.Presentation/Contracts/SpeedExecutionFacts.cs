namespace IEM.Presentation.Contracts;

using IEM.Presentation.Semantics;

/// <summary>
/// Authoritative speed test execution facts provided explicitly to presentation projectors without disk/service reads.
/// Invariants:
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
/// </summary>
public sealed record SpeedExecutionFacts(
    SpeedExecutionState ExecutionState,
    string? MeasurementIntent,
    string? RequestedInterface,
    string? ObservedPath,
    string? PathAgreement,
    string? TunnelIndication,
    double? DownloadThroughputMbps,
    double? UploadThroughputMbps,
    string? RefusalReason,
    string? CustomStatusMessage)
{
    public static SpeedExecutionFacts None { get; } = new(
        ExecutionState: SpeedExecutionState.NotRun,
        MeasurementIntent: null,
        RequestedInterface: null,
        ObservedPath: null,
        PathAgreement: null,
        TunnelIndication: null,
        DownloadThroughputMbps: null,
        UploadThroughputMbps: null,
        RefusalReason: null,
        CustomStatusMessage: null);
}
