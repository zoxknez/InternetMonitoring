namespace IEM.Presentation.Contracts;

using IEM.Presentation.Semantics;

/// <summary>
/// Authoritative speed test execution facts provided explicitly to presentation projectors without disk/service reads.
/// Uses strongly-typed variants so that impossible fact combinations are unrepresentable by construction.
/// Invariants:
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
/// </summary>
public abstract record SpeedExecutionFacts
{
    public abstract SpeedExecutionState ExecutionState { get; }

    public static SpeedExecutionFacts None { get; } = new NotRun();

    /// <summary>
    /// No speed test has been requested or executed.
    /// </summary>
    public sealed record NotRun : SpeedExecutionFacts
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.NotRun;
    }

    /// <summary>
    /// Speed test measurement currently in progress.
    /// </summary>
    public sealed record Executing(
        string? MeasurementIntent,
        string? RequestedInterface) : SpeedExecutionFacts
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Executing;
    }

    /// <summary>
    /// Speed test measurement evaluated and refused before execution.
    /// Physically cannot carry completed throughput facts.
    /// </summary>
    public sealed record Refused(
        string? MeasurementIntent,
        string? RequestedInterface,
        string RefusalReason) : SpeedExecutionFacts
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Refused;
    }

    /// <summary>
    /// Speed test measurement successfully executed with authoritative throughput.
    /// Physically cannot carry refusal facts.
    /// </summary>
    public sealed record Succeeded(
        string? MeasurementIntent,
        string? RequestedInterface,
        string? ObservedPath,
        string? PathAgreement,
        string? TunnelIndication,
        double DownloadThroughputMbps,
        double? UploadThroughputMbps) : SpeedExecutionFacts
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Succeeded;
    }
}
