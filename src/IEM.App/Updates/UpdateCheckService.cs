using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using IEM.Presentation.Updates;

namespace IEM.App.Updates;

/// <summary>
/// Production update checking client for IEM Windows desktop application.
/// Strictly non-blocking, fail-closed, and isolated from evidence recording.
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private const string StableManifestUrl = "https://raw.githubusercontent.com/zoxknez/InternetMonitoring/main/updates/windows/stable.json";
    private const string PreviewManifestUrl = "https://raw.githubusercontent.com/zoxknez/InternetMonitoring/main/updates/windows/preview.json";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _preferencesFilePath;
    private readonly string _currentVersion;

    public UpdatePreferences Preferences { get; private set; }

    public UpdateCheckService(string? preferencesPath = null, string? currentVersion = null)
    {
        _currentVersion = currentVersion ?? ResolveCurrentProductVersion();
        
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "InternetEvidenceMonitor");
        _preferencesFilePath = preferencesPath ?? Path.Combine(dir, "update-preferences.json");

        Preferences = LoadPreferences();
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel? channel = null,
        bool force = false,
        CancellationToken ct = default)
    {
        var targetChannel = channel ?? Preferences.SelectedChannel;

        if (!force)
        {
            if (!Preferences.AutoCheckEnabled)
            {
                return UpdateCheckResult.UpToDate();
            }

            if (Preferences.LastCheckUtc.HasValue &&
                DateTimeOffset.UtcNow - Preferences.LastCheckUtc.Value < TimeSpan.FromHours(24))
            {
                return UpdateCheckResult.UpToDate();
            }
        }

        var url = targetChannel == UpdateChannel.Preview ? PreviewManifestUrl : StableManifestUrl;

        try
        {
            using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Unknown($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                return UpdateCheckResult.Unknown("Neispravan format manifesta ažuriranja.");
            }

            var availability = UpdatePolicy.Evaluate(_currentVersion, manifest, Preferences);

            Preferences = Preferences with { LastCheckUtc = DateTimeOffset.UtcNow };
            SavePreferences(Preferences);

            return availability == UpdateAvailability.UpToDate
                ? UpdateCheckResult.UpToDate(manifest)
                : UpdateCheckResult.Available(availability, manifest);
        }
        catch (Exception ex)
        {
            // Fail-closed: update checker failures never impact monitoring or throw unhandled exceptions
            return UpdateCheckResult.Unknown(ex.Message);
        }
    }

    public void SavePreferences(UpdatePreferences preferences)
    {
        Preferences = preferences;
        try
        {
            var dir = Path.GetDirectoryName(_preferencesFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(Preferences, JsonOptions);
            File.WriteAllText(_preferencesFilePath, json);
        }
        catch
        {
            // Non-fatal preferences persistence failure
        }
    }

    public void SkipVersion(string version)
    {
        SavePreferences(Preferences with { SkippedVersion = version });
    }

    public void Snooze(TimeSpan duration)
    {
        SavePreferences(Preferences with { SnoozedUntilUtc = DateTimeOffset.UtcNow.Add(duration) });
    }

    private UpdatePreferences LoadPreferences()
    {
        try
        {
            if (File.Exists(_preferencesFilePath))
            {
                var json = File.ReadAllText(_preferencesFilePath);
                var loaded = JsonSerializer.Deserialize<UpdatePreferences>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Fallback to default preferences
        }

        return new UpdatePreferences();
    }

    private static string ResolveCurrentProductVersion()
    {
        var assembly = typeof(UpdateCheckService).Assembly;
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            // Strip git hash metadata (e.g. 3.0.0-rc.1+3066764 -> 3.0.0-rc.1)
            var plusIdx = infoVersion.IndexOf('+');
            return plusIdx > 0 ? infoVersion[..plusIdx] : infoVersion;
        }

        var ver = assembly.GetName().Version;
        return ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "3.0.0";
    }
}
