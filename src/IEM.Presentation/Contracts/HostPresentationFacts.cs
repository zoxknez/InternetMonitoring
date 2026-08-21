namespace IEM.Presentation.Contracts;

/// <summary>
/// Platform host presentation metadata provided explicitly without ambient/global OS reads.
/// </summary>
public sealed record HostPresentationFacts(
    bool SurvivesClosing,
    string BackgroundClaimLabel,
    string BackgroundClaimDetail,
    string RestartClaimLabel,
    string RestartClaimDetail,
    string HostDescription);
