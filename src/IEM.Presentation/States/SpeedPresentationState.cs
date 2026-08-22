namespace IEM.Presentation.States;

using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral, immutable presentation state for speed measurement intent, status, and throughput.
/// Uses strongly-typed state variants to eliminate impossible payload combinations by construction.
/// Invariants:
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
/// </summary>
public abstract record SpeedPresentationState
{
    public abstract SpeedExecutionState ExecutionState { get; }
    public abstract bool Ran { get; }
    public abstract bool IsRefused { get; }
    public abstract bool HasTerminalOutcome { get; }
    public abstract string DownloadThroughputText { get; }
    public abstract string UploadThroughputText { get; }
    public abstract string MeasurementStatusText { get; }
    public abstract SemanticTone Tone { get; }

    public static SpeedPresentationState Initial { get; } = new NotRun();

    /// <summary>
    /// Initial unmeasured state before any speed test has been requested or run.
    /// </summary>
    public sealed record NotRun(
        string DownloadThroughputText = "— (Nije pokrenuto)",
        string UploadThroughputText = "— (Nije pokrenuto)",
        string MeasurementStatusText = "Spremno za pokretanje testa brzine.",
        SemanticTone Tone = SemanticTone.Neutral) : SpeedPresentationState
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.NotRun;
        public override bool Ran => false;
        public override bool IsRefused => false;
        public override bool HasTerminalOutcome => false;
        public override string DownloadThroughputText { get; } = DownloadThroughputText;
        public override string UploadThroughputText { get; } = UploadThroughputText;
        public override string MeasurementStatusText { get; } = MeasurementStatusText;
        public override SemanticTone Tone { get; } = Tone;
    }

    /// <summary>
    /// Active measurement execution in progress.
    /// </summary>
    public sealed record Executing(
        string? MeasurementIntent,
        string? RequestedInterface,
        string DownloadThroughputText = "Merenje u toku...",
        string UploadThroughputText = "Merenje u toku...",
        string MeasurementStatusText = "Pokrenuto merenje propusnog opsega...",
        SemanticTone Tone = SemanticTone.Info) : SpeedPresentationState
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Executing;
        public override bool Ran => false;
        public override bool IsRefused => false;
        public override bool HasTerminalOutcome => false;
        public override string DownloadThroughputText { get; } = DownloadThroughputText;
        public override string UploadThroughputText { get; } = UploadThroughputText;
        public override string MeasurementStatusText { get; } = MeasurementStatusText;
        public override SemanticTone Tone { get; } = Tone;
    }

    /// <summary>
    /// Measurement was evaluated and explicitly refused (e.g. VPN tunnel detected, no default route).
    /// Invariant 167: Refused measurements have Ran == false and cannot carry throughput numbers.
    /// </summary>
    public sealed record Refused(
        string? MeasurementIntent,
        string? RequestedInterface,
        string RefusalReason,
        string DownloadThroughputText = "— (Merenje odbijeno)",
        string UploadThroughputText = "— (Merenje odbijeno)",
        string? MeasurementStatusText = null,
        SemanticTone Tone = SemanticTone.Warning) : SpeedPresentationState
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Refused;
        public override bool Ran => false; // Invariant 167: Refused is non-executed, Ran == false
        public override bool IsRefused => true;
        public override bool HasTerminalOutcome => true;
        public override string DownloadThroughputText { get; } = DownloadThroughputText;
        public override string UploadThroughputText { get; } = UploadThroughputText;
        public override string MeasurementStatusText { get; } = MeasurementStatusText ?? $"Merenje odbijeno ({RefusalReason})";
        public override SemanticTone Tone { get; } = Tone;
    }

    /// <summary>
    /// Measurement executed to completion with authoritative throughput numbers.
    /// Physically cannot carry refusal reasons.
    /// </summary>
    public sealed record Succeeded(
        string? MeasurementIntent,
        string? RequestedInterface,
        string? ObservedPath,
        string? PathAgreement,
        string? TunnelIndication,
        double? DownloadThroughputMbps,
        double? UploadThroughputMbps,
        string DownloadThroughputText,
        string UploadThroughputText,
        string MeasurementStatusText = "Merenje uspešno završeno.",
        SemanticTone Tone = SemanticTone.Good) : SpeedPresentationState
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Succeeded;
        public override bool Ran => true;
        public override bool IsRefused => false;
        public override bool HasTerminalOutcome => true;
        public override string DownloadThroughputText { get; } = DownloadThroughputText;
        public override string UploadThroughputText { get; } = UploadThroughputText;
        public override string MeasurementStatusText { get; } = MeasurementStatusText;
        public override SemanticTone Tone { get; } = Tone;
    }
}
