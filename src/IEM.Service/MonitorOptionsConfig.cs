using System.Globalization;

using IEM.Storage;

namespace IEM.Service;

/// <summary>
/// What the service is supposed to be monitoring, as read from configuration.
/// <para>
/// Kept deliberately small. A background service that silently reconfigures itself is a
/// service nobody can reason about after the fact, and this one exists to produce
/// evidence about a specific test over a specific window.
/// </para>
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
    /// Where session folders are created. Empty falls back to <see cref="DefaultOutputRoot"/>.
    /// <para>
    /// Under ProgramData rather than a user's desktop: the service runs without a
    /// logged-in user, and writing into a profile that may not be loaded is how background
    /// services end up silently recording nothing.
    /// </para>
    /// </summary>
    public string OutputRoot { get; set; } = string.Empty;

    /// <summary>
    /// Where sessions go when configuration does not say otherwise. Taken from the shared
    /// contract so the interface looks in the same place the service writes to.
    /// </summary>
    public static string DefaultOutputRoot => ServiceContract.DefaultOutputRoot;

    /// <summary>The configured output root, or the default when it is blank.</summary>
    public string ResolveOutputRoot() =>
        string.IsNullOrWhiteSpace(OutputRoot) ? DefaultOutputRoot : OutputRoot;

    /// <summary>
    /// Whether to pick up an unfinished session on start instead of opening a new one.
    /// <para>
    /// On by default, because the service restarting mid-test is the ordinary case this
    /// whole mechanism exists for.
    /// </para>
    /// </summary>
    public bool ResumeUnfinished { get; set; } = true;

    /// <summary>Build the report package automatically when a session finishes.</summary>
    public bool BuildReportOnCompletion { get; set; } = true;

    /// <summary>
    /// Start a session on service start even when nobody asked for one.
    /// <para>
    /// Off by default, and deliberately so. With it on, a machine that reboots the day
    /// after a finished test would quietly begin another two-day session nobody wanted.
    /// Turn it on only for a machine dedicated to measuring.
    /// </para>
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
            'h' or 'č' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ when char.IsDigit(suffix) => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.Zero,
        };

        return duration > TimeSpan.Zero;
    }
}
