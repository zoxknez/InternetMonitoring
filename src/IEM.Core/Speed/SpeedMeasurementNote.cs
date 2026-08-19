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

    /// <summary>
    /// The rules the conclusions in this file were drawn under.
    /// <para>
    /// 1 is the first build whose <see cref="ValidForComplaint"/> and <see cref="BandLabel"/>
    /// can be taken at face value. A file without it - everything written up to 2.7.0 - was
    /// written when an unchecked measurement path counted as verified and the bands carried
    /// the regulator's terms, so its conclusions are history rather than findings.
    /// </para>
    /// </summary>
    public const int CurrentFindingSchemaVersion = 1;

    /// <summary>Zero for every file written before 2.7.1, which is what marks it as legacy.</summary>
    public int FindingSchemaVersion { get; init; }

    public bool IsLegacyFinding => FindingSchemaVersion < CurrentFindingSchemaVersion;

    /// <summary>
    /// What can be said about this measurement today.
    /// <para>
    /// Every surface asks this rather than reading <see cref="ValidForComplaint"/> and
    /// <see cref="BandLabel"/> directly, because those two fields are whatever the build that
    /// wrote them concluded. For a file from 2.6 the report used to print "ispunjava uslove za
    /// korišćenje uz prigovor" directly beneath "putanja merenja nije proverena" - the old
    /// rule speaking through the new presentation.
    /// </para>
    /// </summary>
    public SpeedFindingAssessment Assess()
    {
        if (!IsLegacyFinding)
        {
            return new SpeedFindingAssessment
            {
                State = ValidForComplaint
                    ? SpeedAssessmentState.MeetsConditions
                    : SpeedAssessmentState.DoesNotMeetConditions,
                BandLabel = BandLabel,
                UploadBandLabel = UploadBandLabel,
                Defects = Defects,
            };
        }

        // The numbers stay exactly as recorded - they are the measurement. Everything derived
        // from them is derived again, under the rules that apply now.
        return new SpeedFindingAssessment
        {
            State = SpeedAssessmentState.Undetermined,
            BandLabel = SpeedMeasurementValidity.BandFor(DownloadMbps, ContractedMbps)?.Label(),
            UploadBandLabel = SpeedMeasurementValidity
                .BandFor(UploadMbps, ContractedUploadMbps)?.UploadLabel(),
            RecordedAssessment = ValidForComplaint,
            Reason = SpeedText.LegacyFindingNote,
            Defects = Defects,
        };
    }

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
    /// What the route table said about the measurement's own traffic.
    /// <para>
    /// A note written before 2.7 has no such field, so it reads back as
    /// <see cref="MeasurementRouteState.Unknown"/> - which is the truth about it. Those
    /// measurements were recorded under a rule that treated an unresolved route as a
    /// verified one, and there is no way to tell afterwards which of them were checked.
    /// </para>
    /// </summary>
    public MeasurementRouteState RouteState { get; init; } = MeasurementRouteState.Unknown;

    /// <summary>
    /// What the measurement's own sockets did: how many connections were observed, how many
    /// left through the adapter this figure is filed under, and through which others.
    /// <para>
    /// The route table is a prediction; this is the observation. Recorded separately rather
    /// than folded into one verdict, because they answer different questions and a reader is
    /// entitled to see that they agreed - or that they did not.
    /// </para>
    /// <para>
    /// Absent from every note written before 3.0, which read back as not observed. That is the
    /// truth about them: nothing was watching the sockets.
    /// </para>
    /// </summary>
    public PathAgreementState ActualPathState { get; init; } = PathAgreementState.Unknown;

    /// <summary>Whether the measurement was unforced or forced to a requested interface.</summary>
    public MeasurementIntent Intent { get; init; } = MeasurementIntent.ObserveSystemPath;

    /// <summary>Tunnel indication inference, if detected.</summary>
    public TunnelIndication Tunnel { get; init; } = TunnelIndication.Unknown;

    /// <summary>How many connections the measurement opened, as observed.</summary>
    public int ObservedConnections { get; init; }

    /// <summary>The adapters those connections actually left through, by name.</summary>
    public IReadOnlyList<string> ObservedInterfaces { get; init; } = [];

    /// <summary>
    /// How many of them could not be matched to any adapter on this machine.
    /// <para>
    /// Recorded because agreement is judged on the connections that resolved, and a reader is
    /// owed the size of the remainder. Without it a note saying "left through the chosen
    /// adapter" would hide that a third of the connections were never placed at all.
    /// </para>
    /// </summary>
    public int UnresolvedConnections { get; init; }

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
            RouteState = conditions.RouteState,
            ActualPathState = conditions.ActualPath.State,
            Intent = conditions.Intent,
            Tunnel = conditions.Tunnel,
            ObservedConnections = conditions.ActualPath.Attempts.Count,
            UnresolvedConnections = conditions.ActualPath.UnresolvedCount,
            ObservedInterfaces =
            [
                .. conditions.ActualPath.Attempts
                    .Select(attempt => attempt.Observed?.Name)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ],
            FindingSchemaVersion = CurrentFindingSchemaVersion,
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
