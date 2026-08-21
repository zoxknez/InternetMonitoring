namespace IEM.Presentation.States;

using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral, immutable presentation state for speed measurement intent, status, and throughput.
/// Invariants:
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
/// </summary>
public sealed record SpeedPresentationState(
    string? MeasurementIntent,
    string? RequestedInterface,
    string? ObservedPath,
    string? PathAgreement,
    string? TunnelIndication,
    double? DownloadThroughputMbps,
    double? UploadThroughputMbps,
    string DownloadThroughputText,
    string UploadThroughputText,
    string MeasurementStatusText,
    bool Ran,
    string? RefusalReason,
    SemanticTone Tone)
{
    public static SpeedPresentationState Initial { get; } = new(
        MeasurementIntent: null,
        RequestedInterface: null,
        ObservedPath: null,
        PathAgreement: null,
        TunnelIndication: null,
        DownloadThroughputMbps: null,
        UploadThroughputMbps: null,
        DownloadThroughputText: "— (Nije pokrenuto)",
        UploadThroughputText: "— (Nije pokrenuto)",
        MeasurementStatusText: "Spremno za pokretanje testa brzine.",
        Ran: false,
        RefusalReason: null,
        Tone: SemanticTone.Neutral);
}
