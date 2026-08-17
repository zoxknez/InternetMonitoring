using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IEM.Storage;

/// <summary>
/// Says, for as long as it is held, that this tool is deliberately saturating the connection.
/// <para>
/// A speed measurement fills the line on purpose, so a monitoring session running at the same
/// time records delay and loss that the service did not cause. The classifier has always
/// known what to do about that - it has a state for it, and the period is excluded from
/// assessment rather than counted against anyone - but nothing ever told it, so the mark was
/// never set and the spike went into the evidence looking like a fault.
/// </para>
/// <para>
/// A file rather than a flag in memory, because the measurement and the monitoring are
/// usually not the same process: the service monitors while the console measures, or the
/// window measures while the service monitors. The file sits in the session folder both of
/// them already share.
/// </para>
/// </summary>
public static class MeasurementMarker
{
    private const string FileName = "merenje-u-toku.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How long a mark is honoured at most.
    /// <para>
    /// A process killed mid-measurement cannot clean up after itself, and a mark left behind
    /// would suspend assessment for the rest of a two-day test - turning a safeguard into the
    /// worst possible failure, a session that records nothing and says everything is fine.
    /// So the mark carries the moment it stops meaning anything, and readers ignore it after.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MaximumHold = TimeSpan.FromMinutes(2);

    public static string PathFor(string outputRoot) => Path.Combine(outputRoot, FileName);

    /// <summary>
    /// Marks the connection as deliberately loaded until the returned handle is disposed.
    /// <para>
    /// Never throws. A measurement that cannot write the mark still measures; the cost is
    /// that a session running beside it records the load as degradation, which is what
    /// happened before this existed.
    /// </para>
    /// </summary>
    public static IDisposable Hold(string? outputRoot, TimeSpan? maximum = null)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return Released.Instance;
        }

        var until = DateTimeOffset.UtcNow + (maximum ?? MaximumHold);

        try
        {
            Directory.CreateDirectory(outputRoot);

            File.WriteAllText(
                PathFor(outputRoot),
                JsonSerializer.Serialize(
                    new Marker(until.ToString("O", CultureInfo.InvariantCulture), Environment.ProcessId),
                    JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return new Handle(outputRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Released.Instance;
        }
    }

    /// <summary>Whether a measurement is loading the connection right now.</summary>
    public static bool IsHeld(string? outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return false;
        }

        try
        {
            var path = PathFor(outputRoot);

            if (!File.Exists(path))
            {
                return false;
            }

            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), JsonOptions);

            return marker is not null &&
                   DateTimeOffset.TryParse(
                       marker.UntilUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until) &&
                   DateTimeOffset.UtcNow <= until;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable mark is treated as absent. Suspending assessment on the strength
            // of a file we cannot read would be the wrong way round: it would silence the
            // monitoring on the basis of nothing.
            return false;
        }
    }

    /// <summary>Removes the mark, whoever left it. Called when a session starts, so a mark
    /// abandoned by a killed process cannot outlive the situation it described.</summary>
    public static void Clear(string? outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return;
        }

        try
        {
            var path = PathFor(outputRoot);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The expiry in the file covers this; a stale mark stops being honoured anyway.
        }
    }

    private sealed record Marker(string UntilUtc, int ProcessId);

    private sealed class Handle(string outputRoot) : IDisposable
    {
        public void Dispose() => Clear(outputRoot);
    }

    private sealed class Released : IDisposable
    {
        public static readonly Released Instance = new();

        public void Dispose()
        {
            // Nothing was marked, so there is nothing to take back.
        }
    }
}
