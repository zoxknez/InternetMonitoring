using System.Text.Json;
using IEM.Presentation.Updates;
using Xunit;

namespace IEM.Core.Tests;

public sealed class UpdateNotificationTests
{
    [Fact]
    public void Remote_newer_version_evaluates_to_UpdateAvailable()
    {
        var manifest = new UpdateManifest
        {
            Version = "3.0.1",
            Channel = "stable",
            Severity = "normal"
        };

        var result = UpdatePolicy.Evaluate("3.0.0", manifest);
        Assert.Equal(UpdateAvailability.UpdateAvailable, result);
    }

    [Fact]
    public void Remote_same_or_older_version_evaluates_to_UpToDate()
    {
        var manifestSame = new UpdateManifest { Version = "3.0.0", Channel = "stable" };
        var manifestOlder = new UpdateManifest { Version = "2.8.0", Channel = "stable" };

        Assert.Equal(UpdateAvailability.UpToDate, UpdatePolicy.Evaluate("3.0.0", manifestSame));
        Assert.Equal(UpdateAvailability.UpToDate, UpdatePolicy.Evaluate("3.0.0", manifestOlder));
    }

    [Fact]
    public void Stable_channel_ignores_preview_manifests()
    {
        var manifestPreview = new UpdateManifest
        {
            Version = "3.1.0-beta.1",
            Channel = "preview",
            Severity = "normal"
        };

        var preferences = new UpdatePreferences { SelectedChannel = UpdateChannel.Stable };
        var result = UpdatePolicy.Evaluate("3.0.0", manifestPreview, preferences);

        Assert.Equal(UpdateAvailability.UpToDate, result);
    }

    [Fact]
    public void Preview_channel_receives_preview_manifests()
    {
        var manifestPreview = new UpdateManifest
        {
            Version = "3.1.0-beta.1",
            Channel = "preview",
            Severity = "normal"
        };

        var preferences = new UpdatePreferences { SelectedChannel = UpdateChannel.Preview };
        var result = UpdatePolicy.Evaluate("3.0.0", manifestPreview, preferences);

        Assert.Equal(UpdateAvailability.PreviewAvailable, result);
    }

    [Fact]
    public void Critical_severity_or_mandatory_returns_CriticalUpdateAvailable()
    {
        var manifestCritical = new UpdateManifest
        {
            Version = "3.0.2",
            Channel = "stable",
            Severity = "critical"
        };

        var manifestMandatory = new UpdateManifest
        {
            Version = "3.0.2",
            Channel = "stable",
            Severity = "normal",
            Mandatory = true
        };

        Assert.Equal(UpdateAvailability.CriticalUpdateAvailable, UpdatePolicy.Evaluate("3.0.0", manifestCritical));
        Assert.Equal(UpdateAvailability.CriticalUpdateAvailable, UpdatePolicy.Evaluate("3.0.0", manifestMandatory));
    }

    [Fact]
    public void Minimum_supported_version_triggers_UnsupportedCurrentVersion()
    {
        var manifest = new UpdateManifest
        {
            Version = "4.0.0",
            Channel = "stable",
            MinimumSupportedVersion = "3.0.0"
        };

        var result = UpdatePolicy.Evaluate("2.7.0", manifest);
        Assert.Equal(UpdateAvailability.UnsupportedCurrentVersion, result);
    }

    [Fact]
    public void Skipped_version_is_ignored_unless_critical()
    {
        var manifestNormal = new UpdateManifest { Version = "3.0.1", Channel = "stable", Severity = "normal" };
        var manifestCritical = new UpdateManifest { Version = "3.0.1", Channel = "stable", Severity = "critical" };

        var preferences = new UpdatePreferences { SkippedVersion = "3.0.1" };

        Assert.Equal(UpdateAvailability.UpToDate, UpdatePolicy.Evaluate("3.0.0", manifestNormal, preferences));
        Assert.Equal(UpdateAvailability.CriticalUpdateAvailable, UpdatePolicy.Evaluate("3.0.0", manifestCritical, preferences));
    }

    [Fact]
    public void Snoozed_version_is_ignored_until_snooze_expires()
    {
        var manifest = new UpdateManifest { Version = "3.0.1", Channel = "stable", Severity = "normal" };
        var now = DateTimeOffset.UtcNow;

        var prefActiveSnooze = new UpdatePreferences { SnoozedUntilUtc = now.AddHours(2) };
        var prefExpiredSnooze = new UpdatePreferences { SnoozedUntilUtc = now.AddHours(-1) };

        Assert.Equal(UpdateAvailability.UpToDate, UpdatePolicy.Evaluate("3.0.0", manifest, prefActiveSnooze, now));
        Assert.Equal(UpdateAvailability.UpdateAvailable, UpdatePolicy.Evaluate("3.0.0", manifest, prefExpiredSnooze, now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("v999.invalid.xyz")]
    public void Malformed_or_missing_version_fails_closed_to_Unknown(string? invalidVersion)
    {
        var manifest = new UpdateManifest { Version = invalidVersion ?? string.Empty };
        Assert.Equal(UpdateAvailability.Unknown, UpdatePolicy.Evaluate("3.0.0", manifest));
        Assert.Equal(UpdateAvailability.Unknown, UpdatePolicy.Evaluate(invalidVersion ?? string.Empty, new UpdateManifest { Version = "3.0.1" }));
    }

    [Fact]
    public void Canonical_manifest_files_in_repository_are_valid_and_parseable()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var stableJson = File.ReadAllText(Path.Combine(FindRepoRoot(), "updates", "windows", "stable.json"));
        var stableManifest = JsonSerializer.Deserialize<UpdateManifest>(stableJson, jsonOptions);
        Assert.NotNull(stableManifest);
        Assert.Equal(1, stableManifest.SchemaVersion);
        Assert.Equal("stable", stableManifest.Channel);
        Assert.True(SemanticVersion.TryParse(stableManifest.Version, out _));

        var previewJson = File.ReadAllText(Path.Combine(FindRepoRoot(), "updates", "windows", "preview.json"));
        var previewManifest = JsonSerializer.Deserialize<UpdateManifest>(previewJson, jsonOptions);
        Assert.NotNull(previewManifest);
        Assert.Equal(1, previewManifest.SchemaVersion);
        Assert.Equal("preview", previewManifest.Channel);
        Assert.True(SemanticVersion.TryParse(previewManifest.Version, out _));
    }

    private static string FindRepoRoot()
    {
        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var candidate in candidates)
        {
            var current = candidate;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "InternetEvidenceMonitor.slnx")) ||
                    Directory.Exists(Path.Combine(current, "updates")))
                {
                    return current;
                }
                current = Directory.GetParent(current)?.FullName;
            }
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}
