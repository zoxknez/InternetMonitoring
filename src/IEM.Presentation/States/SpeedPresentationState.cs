namespace IEM.Presentation.States;

using System.Globalization;
using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral, immutable presentation state for speed measurement intent, status, and throughput.
/// Uses strongly-typed state variants to eliminate impossible payload combinations by construction.
/// Invariants:
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
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
    /// Display throughput is strictly unobserved ("— (Nije pokrenuto)") and Tone is Neutral.
    /// </summary>
    public sealed record NotRun(
        string MeasurementStatusText = "Spremno za pokretanje testa brzine.") : SpeedPresentationState
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.NotRun;
        public override bool Ran => false;
        public override bool IsRefused => false;
        public override bool HasTerminalOutcome => false;
        public override string DownloadThroughputText => "— (Nije pokrenuto)";
        public override string UploadThroughputText => "— (Nije pokrenuto)";
        public override string MeasurementStatusText { get; } = MeasurementStatusText;
        public override SemanticTone Tone => SemanticTone.Neutral;
    }

    /// <summary>
    /// Active measurement execution in progress.
    /// Display throughput is strictly in-progress ("Merenje u toku...") and Tone is Info.
    /// </summary>
    public sealed record Executing(
        string? MeasurementIntent,
        string? RequestedInterface,
        string? MeasurementStatusText = null) : SpeedPresentationState
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Executing;
        public override bool Ran => false;
        public override bool IsRefused => false;
        public override bool HasTerminalOutcome => false;
        public override string DownloadThroughputText => "Merenje u toku...";
        public override string UploadThroughputText => "Merenje u toku...";
        public override string MeasurementStatusText { get; } = MeasurementStatusText ?? "Pokrenuto merenje propusnog opsega...";
        public override SemanticTone Tone => SemanticTone.Info;
    }

    /// <summary>
    /// Measurement was evaluated and explicitly refused (e.g. VPN tunnel detected, no default route).
    /// Invariant 167: Refused measurements have Ran == false, Tone == Warning, and physically cannot display throughput numbers.
    /// </summary>
    public sealed record Refused(
        string? MeasurementIntent,
        string? RequestedInterface,
        string RefusalReason,
        string? MeasurementStatusText = null) : SpeedPresentationState
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Refused;
        public override bool Ran => false; // Invariant 167: Refused is non-executed, Ran == false
        public override bool IsRefused => true;
        public override bool HasTerminalOutcome => true;
        public override string DownloadThroughputText => "— (Merenje odbijeno)";
        public override string UploadThroughputText => "— (Merenje odbijeno)";
        public override string MeasurementStatusText { get; } = MeasurementStatusText ?? $"Merenje odbijeno ({RefusalReason})";
        public override SemanticTone Tone => SemanticTone.Warning;
    }

    /// <summary>
    /// Measurement executed to completion with authoritative throughput numbers.
    /// Display throughput is strictly formatted from the authoritative numeric throughput values.
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
        string? MeasurementStatusText = null) : SpeedPresentationState
    {
        public override SpeedExecutionState ExecutionState => SpeedExecutionState.Succeeded;
        public override bool Ran => true;
        public override bool IsRefused => false;
        public override bool HasTerminalOutcome => true;
        public override string DownloadThroughputText => FormatThroughput(DownloadThroughputMbps);
        public override string UploadThroughputText => FormatThroughput(UploadThroughputMbps);
        public override string MeasurementStatusText { get; } = MeasurementStatusText ?? "Merenje uspešno završeno.";
        public override SemanticTone Tone => SemanticTone.Good;

        private static string FormatThroughput(double? mbps) => mbps.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} Mbps", mbps.Value)
            : "—";
    }
}
