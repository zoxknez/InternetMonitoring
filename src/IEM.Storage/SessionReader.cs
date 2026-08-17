using System.Globalization;
using IEM.Core.Classification;
using IEM.Core.Model;
using IEM.Core.Probes;
using Microsoft.Data.Sqlite;

namespace IEM.Storage;

public sealed record IncidentRow(
    int Number,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    NetworkState WorstState,
    FaultAttribution Attribution,
    TimeSpan DurationMin,
    TimeSpan DurationReported,
    TimeSpan DurationMax,
    int SampleCount,
    bool IsOpen,
    bool EndedByGap,
    bool StartedAfterGap,
    bool RouteChanged,
    Guid CorrelationId,
    int? Support = null,
    int? Coverage = null,
    long? PeakLocalTrafficBytesPerSecond = null)
{
    public TimeSpan Uncertainty => (DurationMax - DurationMin) / 2;

    /// <summary>
    /// How far the evidence goes, as a band. Null when nothing was scored - which is not
    /// the same as scoring zero, and must not be shown as though it were.
    /// </summary>
    public ConfidenceBand? Band => Support is { } support && Coverage is { } coverage
        ? new ConfidenceScore(support, coverage, []).Band
        : null;

    /// <summary>
    /// A pause in monitoring interrupted this event on one side or the other, so the figures
    /// describe only the watched part of it.
    /// </summary>
    public bool TouchesGap => EndedByGap || StartedAfterGap;
}

public sealed record GapRow(DateTimeOffset DetectedUtc, TimeSpan Duration, string Cause);

/// <summary>
/// A path trace taken at an incident boundary.
/// <para>
/// Read in one direction only. That a hop answered proves the packets reached it, and that
/// is the useful half. That nothing answered beyond it proves nothing about the next device:
/// routers are widely configured not to reply to expiring packets, and reading silence as a
/// located fault would put an accusation about a specific machine into a complaint on the
/// strength of a configuration choice.
/// </para>
/// </summary>
public sealed record TraceRow(
    int IncidentNumber,
    string Phase,
    DateTimeOffset TakenUtc,
    string Target,
    bool ReachedTarget,
    int PrivateHopCount,
    string? FirstPublicHop,
    int? LastAnsweringTtl,
    bool StopsInsideHomeNetwork,
    IReadOnlyList<TraceHop> Hops)
{
    /// <summary>Taken while the outage was in progress - the one that carries weight.</summary>
    public bool DuringOutage =>
        string.Equals(Phase, nameof(TracePhase.DuringOutage), StringComparison.Ordinal);

    /// <summary>What this trace supports, phrased so it can be quoted directly.</summary>
    public string Interpretation => ReachedTarget
        ? "Putanja je u celosti prošla do mete."
        : FirstPublicHop is { } edge
            ? $"Putanja je dokazano stigla do {edge}, izvan vaše mreže. Dalje nema odgovora, " +
              "ali to samo po sebi ne dokazuje kvar na sledećem uređaju - ruteri na internetu " +
              "ne moraju da odgovaraju na ovu vrstu provere."
            : Hops.Any(h => h.Answered)
                ? "Putanja nije izašla iz vaše lokalne mreže. Nijedan uređaj izvan nje nije odgovorio."
                : "Nijedan hop nije odgovorio, pa trasa ništa ne dokazuje.";
}

/// <param name="MinRtt">Fastest round trip in the bucket, or null if nothing succeeded.</param>
/// <param name="Outage">True when any sample in the bucket was an outage.</param>
public sealed record LatencyBucket(
    TimeSpan Offset,
    double? MinRtt,
    double? AverageRtt,
    double? MaxRtt,
    bool Outage,
    bool Degraded);

public sealed record SessionSnapshot(
    string SessionId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string Machine,
    string InterfaceName,
    LinkMedium Medium,
    long? LinkSpeedBitsPerSecond,
    string? Gateway,
    TimeSpan MonitoredTime,
    TimeSpan GapTime,
    TimeSpan UpstreamDowntime,
    TimeSpan LocalDowntime,
    double AvailabilityPercent,
    double UpstreamAvailabilityPercent,
    long SampleCount,
    IReadOnlyList<IncidentRow> Incidents,
    IReadOnlyList<GapRow> Gaps,
    IReadOnlyList<LatencyBucket> Latency,
    IReadOnlyList<TraceRow> Traces)
{
    /// <summary>
    /// Which reasoning produced this session's conclusions, as recorded when it ran.
    /// <para>
    /// Not the current build's numbers. A report can be rebuilt years later - that is the
    /// point of keeping the raw chain - and printing today's versions over an old session
    /// would defeat the one thing these fields are for: telling a discrepancy apart from a
    /// changed algorithm. Null only for an index written before they were stored, where the
    /// report falls back to the current values and says nothing it cannot support.
    /// </para>
    /// </summary>
    public int? SchemaVersion { get; init; }

    public string? ClassifierVersion { get; init; }

    public string? AttributionModelVersion { get; init; }

    public string? ConfidenceModelVersion { get; init; }

    public IEnumerable<IncidentRow> UpstreamIncidents => Incidents.Where(i => i.Attribution == FaultAttribution.Upstream);

    public TimeSpan LongestUpstreamOutage =>
        UpstreamIncidents.Select(i => i.DurationReported).DefaultIfEmpty(TimeSpan.Zero).Max();

    public TimeSpan TotalDowntime => UpstreamDowntime + LocalDowntime;

    /// <summary>Total span the report covers, gaps included.</summary>
    public TimeSpan WallClockTime => MonitoredTime + GapTime;
}

/// <summary>
/// Loads a finished session for reporting.
/// <para>
/// Reads the SQLite index rather than the raw chain: the chain is optimised for being
/// impossible to alter, not for being queried, and a two-day session holds well over a
/// hundred thousand lines.
/// </para>
/// </summary>
public sealed class SessionReader : IDisposable
{
    /// <summary>
    /// Chart resolution. A 48-hour session at one sample a second is far more points than
    /// an SVG - or an eye - can use, so samples are folded into buckets that keep the
    /// extremes. Averaging alone would erase the latency spikes that matter most.
    /// </summary>
    private const int LatencyBuckets = 600;

    private readonly SqliteConnection _connection;

    private SessionReader(SqliteConnection connection) => _connection = connection;

    public static SessionReader Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Baza sesije nije pronađena.", databasePath);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,

            // Same reason as the writer: a pooled handle would outlive this reader and
            // keep the file locked while the package is being zipped.
            Pooling = false,
        }.ToString());

        connection.Open();
        return new SessionReader(connection);
    }

    /// <summary>The most recent session in the database, or null if there is none.</summary>
    public SessionSnapshot? Load()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, started_utc, ended_utc, machine, interface_name, medium, link_speed_bps, gateway,
                   monitored_ms, gap_ms, upstream_downtime_ms, local_downtime_ms,
                   availability, upstream_availability,
                   schema_version, classifier_version, attribution_version, confidence_version
            FROM sessions
            ORDER BY started_utc DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var sessionId = reader.GetString(0);

        return new SessionSnapshot(
            sessionId,
            ParseDate(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseDate(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            Enum.TryParse<LinkMedium>(reader.GetString(5), out var medium) ? medium : LinkMedium.Unknown,
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            Milliseconds(reader, 8),
            Milliseconds(reader, 9),
            Milliseconds(reader, 10),
            Milliseconds(reader, 11),
            reader.IsDBNull(12) ? 100d : reader.GetDouble(12),
            reader.IsDBNull(13) ? 100d : reader.GetDouble(13),
            CountSamples(sessionId),
            ReadIncidents(sessionId),
            ReadGaps(sessionId),
            ReadLatency(sessionId),
            ReadTraces(sessionId))
        {
            SchemaVersion = reader.IsDBNull(14) ? null : reader.GetInt32(14),
            ClassifierVersion = reader.IsDBNull(15) ? null : reader.GetString(15),
            AttributionModelVersion = reader.IsDBNull(16) ? null : reader.GetString(16),
            ConfidenceModelVersion = reader.IsDBNull(17) ? null : reader.GetString(17),
        };
    }

    private List<TraceRow> ReadTraces(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT incident_number, phase, taken_utc, target, reached, private_hops,
                   first_public_hop, last_answering_ttl, stops_at_home, hops
            FROM traces WHERE session_id = $id ORDER BY taken_utc;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        var rows = new List<TraceRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new TraceRow(
                reader.GetInt32(0),
                reader.GetString(1),
                ParseDate(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4) != 0,
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.GetInt32(8) != 0,
                ParseHops(reader.GetString(9))));
        }

        return rows;
    }

    private static IReadOnlyList<TraceHop> ParseHops(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var hops = new List<TraceHop>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("ttl", out var ttl) || !ttl.TryGetInt32(out var hopTtl))
                {
                    continue;
                }

                var address = item.TryGetProperty("address", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String
                    ? a.GetString()
                    : null;

                var rtt = item.TryGetProperty("rttMs", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? TimeSpan.FromMilliseconds(r.GetDouble())
                    : (TimeSpan?)null;

                hops.Add(new TraceHop(hopTtl, address, rtt));
            }

            return hops;
        }
        catch (System.Text.Json.JsonException)
        {
            // The index is a cache; an unreadable hop list costs the detail on one trace and
            // nothing else. The chain still holds it.
            return [];
        }
    }

    private long CountSamples(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM samples WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<IncidentRow> ReadIncidents(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT number, started_utc, ended_utc, worst_state, attribution,
                   duration_min_ms, duration_ms, duration_max_ms, sample_count, is_open,
                   ended_by_gap, started_after_gap, route_changed, correlation_id,
                   support, coverage, local_bps_peak
            FROM incidents WHERE session_id = $id ORDER BY number;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        var rows = new List<IncidentRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new IncidentRow(
                reader.GetInt32(0),
                ParseDate(reader.GetString(1)),
                ParseDate(reader.GetString(2)),
                Enum.TryParse<NetworkState>(reader.GetString(3), out var state) ? state : NetworkState.InternetDown,
                Enum.TryParse<FaultAttribution>(reader.GetString(4), out var attribution)
                    ? attribution
                    : FaultAttribution.Undetermined,
                TimeSpan.FromMilliseconds(reader.GetDouble(5)),
                TimeSpan.FromMilliseconds(reader.GetDouble(6)),
                TimeSpan.FromMilliseconds(reader.GetDouble(7)),
                reader.GetInt32(8),
                reader.GetInt32(9) != 0,
                reader.GetInt32(10) != 0,
                reader.GetInt32(11) != 0,
                reader.GetInt32(12) != 0,
                Guid.TryParse(reader.GetString(13), out var correlation) ? correlation : Guid.Empty,
                reader.IsDBNull(14) ? null : reader.GetInt32(14),
                reader.IsDBNull(15) ? null : reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetInt64(16)));
        }

        return rows;
    }

    private List<GapRow> ReadGaps(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT detected_utc, duration_ms, cause FROM gaps WHERE session_id = $id ORDER BY detected_utc;";
        command.Parameters.AddWithValue("$id", sessionId);

        var rows = new List<GapRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new GapRow(
                ParseDate(reader.GetString(0)),
                TimeSpan.FromMilliseconds(reader.GetDouble(1)),
                reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// Folds samples into fixed-width buckets, keeping the minimum, mean and maximum in
    /// each and flagging any bucket that contained an outage or degradation.
    /// </summary>
    private List<LatencyBucket> ReadLatency(string sessionId)
    {
        using var span = _connection.CreateCommand();
        span.CommandText = "SELECT MIN(mono_ms), MAX(mono_ms) FROM samples WHERE session_id = $id;";
        span.Parameters.AddWithValue("$id", sessionId);

        double firstMs;
        double lastMs;

        using (var spanReader = span.ExecuteReader())
        {
            if (!spanReader.Read() || spanReader.IsDBNull(0))
            {
                return [];
            }

            firstMs = spanReader.GetDouble(0);
            lastMs = spanReader.GetDouble(1);
        }

        var totalMs = Math.Max(1d, lastMs - firstMs);
        var bucketMs = totalMs / LatencyBuckets;

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT CAST((mono_ms - $first) / $width AS INTEGER) AS bucket,
                   MIN(rtt_ms), AVG(rtt_ms), MAX(rtt_ms),
                   MAX(CASE WHEN severity = 'Outage' THEN 1 ELSE 0 END),
                   MAX(CASE WHEN severity = 'Degraded' THEN 1 ELSE 0 END)
            FROM samples
            WHERE session_id = $id
            GROUP BY bucket
            ORDER BY bucket;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$first", firstMs);
        command.Parameters.AddWithValue("$width", bucketMs);

        var buckets = new List<LatencyBucket>(LatencyBuckets);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            buckets.Add(new LatencyBucket(
                TimeSpan.FromMilliseconds(reader.GetInt64(0) * bucketMs),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.GetInt32(4) != 0,
                reader.GetInt32(5) != 0));
        }

        return buckets;
    }

    private static TimeSpan Milliseconds(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? TimeSpan.Zero : TimeSpan.FromMilliseconds(reader.GetDouble(ordinal));

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose() => _connection.Dispose();
}
