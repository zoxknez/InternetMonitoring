namespace IEM.Presentation.Contracts;

/// <summary>
/// Explicit application update notification state.
/// </summary>
public sealed record UpdatePresentationState(
    bool IsUpdateBannerVisible,
    string UpdateVersionText,
    string UpdateSummaryText,
    string UpdateReleaseNotesUrl,
    string UpdateDownloadUrl)
{
    public static UpdatePresentationState Hidden { get; } = new(
        IsUpdateBannerVisible: false,
        UpdateVersionText: string.Empty,
        UpdateSummaryText: "Dostupna su nova poboljšanja i ispravke.",
        UpdateReleaseNotesUrl: string.Empty,
        UpdateDownloadUrl: string.Empty);
}
