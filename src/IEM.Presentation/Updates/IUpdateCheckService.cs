namespace IEM.Presentation.Updates;

/// <summary>
/// Service interface for checking application updates.
/// Invariant: Implementation belongs strictly to presentation/application periphery and never affects evidence recording.
/// </summary>
public interface IUpdateCheckService
{
    UpdatePreferences Preferences { get; }
    
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel? channel = null,
        bool force = false,
        CancellationToken ct = default);

    void SavePreferences(UpdatePreferences preferences);
    void SkipVersion(string version);
    void Snooze(TimeSpan duration);
}
