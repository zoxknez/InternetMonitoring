using System.Text.RegularExpressions;

namespace IEM.Presentation.Updates;

/// <summary>
/// Deterministic update evaluation policy.
/// Isolates version comparison, channels, snooze, and skipped versions without side-effects.
/// </summary>
public static class UpdatePolicy
{
    public static UpdateAvailability Evaluate(
        string currentVersionStr,
        UpdateManifest? manifest,
        UpdatePreferences? preferences = null,
        DateTimeOffset? nowUtc = null)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
        {
            return UpdateAvailability.Unknown;
        }

        if (!SemanticVersion.TryParse(currentVersionStr, out var currentVersion))
        {
            return UpdateAvailability.Unknown;
        }

        if (!SemanticVersion.TryParse(manifest.Version, out var remoteVersion))
        {
            return UpdateAvailability.Unknown;
        }

        preferences ??= new UpdatePreferences();
        var now = nowUtc ?? DateTimeOffset.UtcNow;

        // Channel filter: Stable users ignore Preview channel manifests
        var manifestIsPreview = string.Equals(manifest.Channel, "preview", StringComparison.OrdinalIgnoreCase) ||
                                remoteVersion.IsPrerelease;

        if (preferences.SelectedChannel == UpdateChannel.Stable && manifestIsPreview)
        {
            return UpdateAvailability.UpToDate;
        }

        // Skipped version check (unless mandatory/critical)
        var isCritical = manifest.ParseSeverity() == UpdateSeverity.Critical || manifest.Mandatory;
        if (!isCritical && string.Equals(preferences.SkippedVersion, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateAvailability.UpToDate;
        }

        // Snoozed check (unless mandatory/critical)
        if (!isCritical && preferences.SnoozedUntilUtc.HasValue && preferences.SnoozedUntilUtc.Value > now)
        {
            return UpdateAvailability.UpToDate;
        }

        // Minimum supported version check
        if (!string.IsNullOrWhiteSpace(manifest.MinimumSupportedVersion) &&
            SemanticVersion.TryParse(manifest.MinimumSupportedVersion, out var minSupportedVersion) &&
            currentVersion.CompareTo(minSupportedVersion) < 0)
        {
            return UpdateAvailability.UnsupportedCurrentVersion;
        }

        // Version comparison
        var comparison = remoteVersion.CompareTo(currentVersion);
        if (comparison > 0)
        {
            if (isCritical)
            {
                return UpdateAvailability.CriticalUpdateAvailable;
            }

            return manifestIsPreview
                ? UpdateAvailability.PreviewAvailable
                : UpdateAvailability.UpdateAvailable;
        }

        return UpdateAvailability.UpToDate;
    }
}

/// <summary>
/// Lightweight, zero-dependency SemVer 2.0 parser & comparer.
/// </summary>
public sealed record SemanticVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<SemanticVersion>
{
    private static readonly Regex SemVerRegex = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:-(?<pre>[0-9A-Za-z\.-]+))?$",
        RegexOptions.Compiled);

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static bool TryParse(string? versionText, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(versionText)) return false;

        var match = SemVerRegex.Match(versionText.Trim());
        if (!match.Success) return false;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        var majorCmp = Major.CompareTo(other.Major);
        if (majorCmp != 0) return majorCmp;

        var minorCmp = Minor.CompareTo(other.Minor);
        if (minorCmp != 0) return minorCmp;

        var patchCmp = Patch.CompareTo(other.Patch);
        if (patchCmp != 0) return patchCmp;

        // SemVer 2.0: 1.0.0-rc.1 < 1.0.0 (release is higher than prerelease)
        if (Prerelease is null && other.Prerelease is null) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        return string.Compare(Prerelease, other.Prerelease, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (Prerelease is not null ? $"-{Prerelease}" : "");
}
