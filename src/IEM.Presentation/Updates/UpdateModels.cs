using System.Text.Json.Serialization;

namespace IEM.Presentation.Updates;

/// <summary>
/// Distribution channels for IEM software updates.
/// </summary>
public enum UpdateChannel
{
    Stable,
    Preview,
}

/// <summary>
/// Status of update availability relative to the currently installed version.
/// </summary>
public enum UpdateAvailability
{
    UpToDate,
    UpdateAvailable,
    PreviewAvailable,
    UnsupportedCurrentVersion,
    CriticalUpdateAvailable,
    Unknown,
}

/// <summary>
/// Severity level of a software release.
/// </summary>
public enum UpdateSeverity
{
    Normal,
    Recommended,
    Critical,
}

/// <summary>
/// Platform-neutral update manifest format (Schema v1).
/// </summary>
public sealed record UpdateManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("product")]
    public string Product { get; init; } = "InternetEvidenceMonitor";

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "windows-x64";

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "stable";

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset PublishedAt { get; init; }

    [JsonPropertyName("minimumSupportedVersion")]
    public string? MinimumSupportedVersion { get; init; }

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "normal";

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; init; }

    [JsonPropertyName("releaseNotesUrl")]
    public string ReleaseNotesUrl { get; init; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("releaseCommit")]
    public string? ReleaseCommit { get; init; }

    public UpdateSeverity ParseSeverity() => Severity?.ToLowerInvariant() switch
    {
        "critical" => UpdateSeverity.Critical,
        "recommended" => UpdateSeverity.Recommended,
        _ => UpdateSeverity.Normal,
    };
}

/// <summary>
/// User update preferences.
/// </summary>
public sealed record UpdatePreferences
{
    public bool AutoCheckEnabled { get; init; } = true;
    public UpdateChannel SelectedChannel { get; init; } = UpdateChannel.Stable;
    public DateTimeOffset? LastCheckUtc { get; init; }
    public string? SkippedVersion { get; init; }
    public DateTimeOffset? SnoozedUntilUtc { get; init; }
}

/// <summary>
/// Outcome of an update check operation.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    UpdateManifest? Manifest,
    string? ErrorMessage,
    DateTimeOffset CheckedAtUtc)
{
    public static UpdateCheckResult UpToDate(UpdateManifest? manifest = null) =>
        new(UpdateAvailability.UpToDate, manifest, null, DateTimeOffset.UtcNow);

    public static UpdateCheckResult Available(UpdateAvailability availability, UpdateManifest manifest) =>
        new(availability, manifest, null, DateTimeOffset.UtcNow);

    public static UpdateCheckResult Unknown(string error) =>
        new(UpdateAvailability.Unknown, null, error, DateTimeOffset.UtcNow);
}
