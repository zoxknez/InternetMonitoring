using System.Text.Json;
using System.Text.Json.Serialization;
using IEM.Core.Model;

namespace IEM.Core.Speed;

/// <summary>
/// A completed speed measurement, written beside the session it belongs to.
/// <para>
/// The measurement is not part of the hash chain: the chain is the monitor's own record,
/// appended from its own process, and a measurement run separately cannot honestly claim a
/// place in it. Standing beside the session as its own file, it becomes part of the package
/// - hashed with everything else when the archive is sealed - while the report says exactly
/// what it is and what it does and does not prove.
/// </para>
/// </summary>
public sealed record SpeedMeasurementNote(
    DateTimeOffset MeasuredAtUtc,
    LinkMedium Medium,
    double? LinkSpeedMbps,
    double? ContractedMbps,
    double DownloadMbps,
    long BytesTransferred,
    TimeSpan Duration,
    bool ValidForComplaint,
    string? BandLabel,
    IReadOnlyList<string> Defects)
{
    /// <summary>The file name inside the session directory.</summary>
    public const string FileName = "MerenjeBrzine.json";

    // Added after the first release of this file, and therefore as properties with defaults
    // rather than as constructor parameters: a note written by an earlier build has to keep
    // reading, and a measurement whose upload half never ran must say "not measured" rather
    // than "nought".

    /// <summary>Sending rate, or null when that half did not run.</summary>
    public double? UploadMbps { get; init; }

    public long UploadBytesTransferred { get; init; }

    public double? ContractedUploadMbps { get; init; }

    /// <summary>Where the sending figure falls against the contract, when both are known.</summary>
    public string? UploadBandLabel { get; init; }

    /// <summary>Round-trip time while the connection was doing nothing else, in milliseconds.</summary>
    public double? IdleLatencyMs { get; init; }

    /// <summary>Round-trip time while the line was pulling at full rate.</summary>
    public double? LatencyUnderDownloadMs { get; init; }

    /// <summary>Round-trip time while the line was sending at full rate.</summary>
    public double? LatencyUnderUploadMs { get; init; }

    /// <summary>
    /// How much worse the round trip got under load, taken from the worse direction. This is
    /// the figure a complaint about calls and games rests on, and the one a download-only
    /// measurement cannot produce.
    /// </summary>
    public double? LatencyIncreaseMs { get; init; }

    /// <summary>Serbian label for that increase, so the report and the window agree.</summary>
    public string? LoadedLatencyLabel { get; init; }

    /// <summary>
    /// Builds the note from one measurement and its conditions.
    /// <para>
    /// The single place where a measurement becomes a record, so the console, the window and
    /// the service cannot disagree about what the same figures mean. They used to each build
    /// this by hand, and the window's copy quietly recorded every measurement as invalid.
    /// </para>
    /// </summary>
    public static SpeedMeasurementNote From(
        DateTimeOffset measuredAtUtc,
        LinkMedium medium,
        double? linkSpeedMbps,
        SpeedMeasurementConditions conditions,
        ThroughputResult result)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(result);

        var validity = SpeedMeasurementValidity.Of(conditions);
        var band = SpeedMeasurementValidity.Judge(conditions);
        var uploadBand = SpeedMeasurementValidity.JudgeUpload(conditions);
        var increase = result.LatencyIncreaseUnderLoad;

        return new SpeedMeasurementNote(
            measuredAtUtc,
            medium,
            linkSpeedMbps,
            conditions.ContractedDownloadMbps,
            result.DownloadMbps,
            result.BytesTransferred,
            result.Duration,
            validity.IsValidForComplaint,
            band?.Label(),
            [.. validity.Defects.Select(defect => defect.Explain())])
        {
            UploadMbps = result.UploadMbps,
            UploadBytesTransferred = result.UploadBytes,
            ContractedUploadMbps = conditions.ContractedUploadMbps,
            UploadBandLabel = uploadBand?.UploadLabel(),
            IdleLatencyMs = result.IdleLatency?.Median.TotalMilliseconds,
            LatencyUnderDownloadMs = result.DownloadLoadedLatency?.Median.TotalMilliseconds,
            LatencyUnderUploadMs = result.UploadLoadedLatency?.Median.TotalMilliseconds,
            LatencyIncreaseMs = increase?.TotalMilliseconds,
            LoadedLatencyLabel = result.LoadedLatencyGrade?.Label(),
        };
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes the note into a session directory, replacing any earlier measurement.</summary>
    public void Write(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// Reads the note from a session directory, or null when no measurement was taken.
    /// <para>
    /// A file that cannot be parsed is reported as absent rather than thrown: a broken note
    /// must not take the whole report down with it, and the report can say everything true
    /// without it.
    /// </para>
    /// </summary>
    public static SpeedMeasurementNote? Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, FileName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SpeedMeasurementNote>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
