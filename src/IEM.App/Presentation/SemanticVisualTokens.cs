using IEM.Core.Quality;
using IEM.Core.Reports;

namespace IEM.App.Presentation;

/// <summary>
/// Central presentation tokens mapping domain states to visual styles without collapsing semantics.
/// Invariant 170: VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE.
/// </summary>
public static class SemanticVisualTokens
{
    public static string GetEpistemicBadgeText(EpistemicClass epistemic) => epistemic switch
    {
        EpistemicClass.Fact => "FACT",
        EpistemicClass.Inference => "INFERENCE",
        EpistemicClass.Assessment => "ASSESSMENT",
        _ => "UNKNOWN",
    };

    public static string GetQualityBadgeText(EvidenceQualityBand band) => band switch
    {
        EvidenceQualityBand.Strong => "Strong",
        EvidenceQualityBand.Moderate => "Moderate",
        EvidenceQualityBand.Limited => "Limited",
        EvidenceQualityBand.Insufficient => "Insufficient",
        _ => "Unknown",
    };

    public static string GetIntegrityBadgeText(string integrityState) => integrityState switch
    {
        "Verified" => "Verified (Celo)",
        "Incomplete" => "Incomplete (Nepotpuno)",
        "Invalid" => "Invalid (Nevažeće)",
        _ => integrityState,
    };

    public static string GetTrustBadgeText(string trustState) => trustState switch
    {
        "Established" => "Established (TSA Potvrđen)",
        "NotEstablished" => "Not Established (Bez TSA žiga)",
        "NotApplicable" => "N/A",
        _ => trustState,
    };

    public static string GetTimelineCategoryLabel(TimelineEntryCategory category) => category switch
    {
        TimelineEntryCategory.ActiveMonitoring => "Aktivno osmatranje (Normal)",
        TimelineEntryCategory.InterruptionObserved => "Prekid dostupnosti (Network)",
        TimelineEntryCategory.HostSuspended => "Mirovanje računara (Host Sleep)",
        TimelineEntryCategory.ClockAdjustment => "Pomeranje sata (Clock Delta)",
        TimelineEntryCategory.BootBoundary => "Restart sistema (Reboot)",
        TimelineEntryCategory.ProbeDegraded => "Degradacija lokalne sonde",
        _ => "Nepoznato",
    };
}
