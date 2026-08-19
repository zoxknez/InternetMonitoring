using System.Globalization;

namespace IEM.Service.Runtime;

/// <summary>
/// Platform-neutral configuration settings for the monitoring runtime.
/// </summary>
public sealed class MonitorSettings
{
    public const string SectionName = "Monitor";

    /// <summary>
    /// How long a session should run, as text: <c>48h</c>, <c>7d</c>, <c>90m</c>, or
    /// <c>beskonacno</c> to run until stopped.
    /// </summary>
    public string Duration { get; set; } = "48h";

    /// <summary>Adapter to watch. Empty means whichever one carries the default route.</summary>
    public string? Interface { get; set; }

    /// <summary>
    /// Where session folders are created. If empty, the host composition root supplies the default.
    /// </summary>
    public string OutputRoot { get; set; } = string.Empty;

    /// <summary>The configured output root, or the supplied fallback when it is blank.</summary>
    public string ResolveOutputRoot(string fallback) =>
        string.IsNullOrWhiteSpace(OutputRoot) ? fallback : OutputRoot;

    /// <summary>
    /// Whether to pick up an unfinished session on start instead of opening a new one.
    /// </summary>
    public bool ResumeUnfinished { get; set; } = true;

    /// <summary>Build the report package automatically when a session finishes.</summary>
    public bool BuildReportOnCompletion { get; set; } = true;

    /// <summary>
    /// Start a session on service start even when nobody asked for one.
    /// </summary>
    public bool AutoStart { get; set; }

    public TimeSpan ResolveDuration() =>
        TryParseDuration(Duration, out var parsed) ? parsed : TimeSpan.FromHours(48);

    /// <summary>Accepts 45s, 90m, 48h, 7d, a bare number of minutes, or "beskonacno".</summary>
    public static bool TryParseDuration(string? value, out TimeSpan duration)
    {
        duration = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim().ToLowerInvariant();

        if (value is "beskonacno" or "beskonačno" or "infinite")
        {
            duration = Timeout.InfiniteTimeSpan;
            return true;
        }

        var suffix = value[^1];
        var numberText = char.IsDigit(suffix) ? value : value[..^1];

        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ||
            amount <= 0)
        {
            return false;
        }

        duration = suffix switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ when char.IsDigit(suffix) => TimeSpan.FromMinutes(amount),
            _ => default,
        };

        return duration > TimeSpan.Zero;
    }
}
