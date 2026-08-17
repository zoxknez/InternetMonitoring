using IEM.Storage.Evidence;
using Microsoft.Data.Sqlite;

namespace IEM.Storage;

/// <summary>
/// Queryable index over a session.
/// <para>
/// Explicitly a derived store, not the record. The raw JSONL chain is the evidence; this
/// database exists so the interface and the reports can ask questions without replaying a
/// hundred and seventy thousand lines. If it is lost or corrupted it can be rebuilt from
/// the chain, which is why it can afford to batch writes while the chain cannot.
/// </para>
/// <para>
/// Samples are committed in batches. A crash loses at most the last batch from here, and
/// those samples are still in the chain, so nothing is actually lost - only the index
/// falls briefly behind.
/// </para>
/// </summary>
public sealed class SqliteSessionStore : IDisposable
{
    private const int BatchSize = 500;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    private SqliteTransaction? _batch;
    private int _pendingRows;

    private SqliteSessionStore(SqliteConnection connection) => _connection = connection;

    public static SqliteSessionStore Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Pooling keeps the file handle alive after the connection is disposed, so the
            // report generator that runs moments later finds the database still locked.
            // One session owns one connection for its lifetime, so pooling buys nothing here.
            Pooling = false,
        }.ToString());

        try
        {
        connection.Open();

        // WAL survives an abrupt process exit far better than the rollback journal, and
        // NORMAL synchronous is the right trade here: the chain already carries the
        // durability guarantee, so paying for a disk sync per commit twice over is waste.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");

        var store = new SqliteSessionStore(connection);
        store.DiscardOutdatedSchema();
        store.CreateSchema();
        return store;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Version of the index layout. Bumped whenever a column is added or changed.
    /// <para>
    /// 2 adds outage segments - correlation id, gap boundaries, route change - and the
    /// confidence figures.
    /// </para>
    /// <para>
    /// 4 adds the busiest second of the machine's own traffic during each segment, so the
    /// report can say how busy the line was rather than only that the check did not come
    /// out clean.
    /// </para>
    /// <para>
    /// 5 adds the model versions the session was recorded under. Without them a report
    /// rebuilt by a newer build printed today's version numbers over yesterday's session -
    /// which is precisely the confusion those numbers exist to prevent.
    /// </para>
    /// </summary>
    private const int SchemaVersion = 5;

    /// <summary>
    /// Throws away an index written by an older build and starts a fresh one.
    /// <para>
    /// Safe precisely because this database is a derived cache: everything in it is
    /// reconstructible from the raw chain, and an export rebuilds it from there anyway. The
    /// alternative - migrating with <c>ALTER TABLE</c> - would mean carrying every past
    /// layout forever to preserve a file that is by design disposable.
    /// </para>
    /// <para>
    /// Without this, <c>CREATE TABLE IF NOT EXISTS</c> silently leaves an old table in
    /// place and the first insert fails on a column that does not exist - which would land
    /// on exactly the users who had a session already running when they updated.
    /// </para>
    /// </summary>
    private void DiscardOutdatedSchema()
    {
        using var read = _connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";

        var version = Convert.ToInt32(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        if (version == SchemaVersion)
        {
            return;
        }

        Execute(
            _connection,
            """
            DROP TABLE IF EXISTS samples;
            DROP TABLE IF EXISTS incidents;
            DROP TABLE IF EXISTS traces;
            DROP TABLE IF EXISTS gaps;
            DROP TABLE IF EXISTS sessions;
            """);

        Execute(_connection, $"PRAGMA user_version={SchemaVersion};");
    }

    private void CreateSchema() => Execute(
        _connection,
        """
        CREATE TABLE IF NOT EXISTS sessions (
            id                      TEXT PRIMARY KEY,
            started_utc             TEXT NOT NULL,
            ended_utc               TEXT,
            planned_duration_ms     INTEGER,
            tool_version            TEXT,
            machine                 TEXT,
            interface_name          TEXT,
            medium                  TEXT,
            link_speed_bps          INTEGER,
            gateway                 TEXT,
            monitored_ms            REAL,
            gap_ms                  REAL,
            upstream_downtime_ms    REAL,
            local_downtime_ms       REAL,
            availability            REAL,
            upstream_availability   REAL,
            incident_count          INTEGER,
            upstream_incident_count INTEGER,
            schema_version          INTEGER,
            classifier_version      TEXT,
            attribution_version     TEXT,
            confidence_version      TEXT
        );

        CREATE TABLE IF NOT EXISTS samples (
            session_id  TEXT NOT NULL,
            n           INTEGER NOT NULL,
            utc         TEXT NOT NULL,
            mono_ms     REAL NOT NULL,
            state       TEXT NOT NULL,
            severity    TEXT NOT NULL,
            phase       TEXT NOT NULL,
            link        TEXT NOT NULL,
            rtt_ms      REAL,
            gw_ok       INTEGER, gw_total   INTEGER,
            icmp_ok     INTEGER, icmp_total INTEGER,
            tcp_ok      INTEGER, tcp_total  INTEGER,
            signal      INTEGER,
            overran     INTEGER NOT NULL,
            PRIMARY KEY (session_id, n)
        );

        CREATE TABLE IF NOT EXISTS incidents (
            session_id      TEXT NOT NULL,
            number          INTEGER NOT NULL,
            started_utc     TEXT NOT NULL,
            ended_utc       TEXT NOT NULL,
            worst_state     TEXT NOT NULL,
            attribution     TEXT NOT NULL,
            duration_min_ms REAL NOT NULL,
            duration_ms     REAL NOT NULL,
            duration_max_ms REAL NOT NULL,
            sample_count    INTEGER NOT NULL,
            is_open         INTEGER NOT NULL,
            ended_by_gap    INTEGER NOT NULL,
            started_after_gap INTEGER NOT NULL,
            route_changed   INTEGER NOT NULL,
            correlation_id  TEXT NOT NULL,
            support         INTEGER,
            coverage        INTEGER,
            local_bps_peak  INTEGER,
            detail          TEXT,
            PRIMARY KEY (session_id, number)
        );

        CREATE TABLE IF NOT EXISTS traces (
            session_id         TEXT NOT NULL,
            incident_number    INTEGER NOT NULL,
            phase              TEXT NOT NULL,
            taken_utc          TEXT NOT NULL,
            target             TEXT NOT NULL,
            reached            INTEGER NOT NULL,
            private_hops       INTEGER NOT NULL,
            first_public_hop   TEXT,
            last_answering_ttl INTEGER,
            stops_at_home      INTEGER NOT NULL,
            hops               TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS gaps (
            session_id   TEXT NOT NULL,
            detected_utc TEXT NOT NULL,
            duration_ms  REAL NOT NULL,
            cause        TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_samples_state ON samples (session_id, state);
        CREATE INDEX IF NOT EXISTS ix_incidents_attribution ON incidents (session_id, attribution);
        """);

    public void BeginSession(string sessionId, SessionStartPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            FlushBatchCore();

            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sessions (id, started_utc, planned_duration_ms, tool_version, machine,
                                      interface_name, medium, link_speed_bps, gateway,
                                      schema_version, classifier_version, attribution_version,
                                      confidence_version)
                VALUES ($id, $started, $planned, $version, $machine, $interface, $medium, $speed, $gateway,
                        $schema, $classifier, $attribution, $confidence)
                ON CONFLICT(id) DO NOTHING;
                """;

            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$started", payload.StartedUtc.ToString("O"));
            command.Parameters.AddWithValue(
                "$planned",
                payload.PlannedDuration == Timeout.InfiniteTimeSpan
                    ? DBNull.Value
                    : payload.PlannedDuration.TotalMilliseconds);
            command.Parameters.AddWithValue("$version", payload.ToolVersion);
            command.Parameters.AddWithValue("$machine", payload.MachineName);
            command.Parameters.AddWithValue("$interface", payload.InterfaceName);
            command.Parameters.AddWithValue("$medium", payload.Medium.ToString());
            command.Parameters.AddWithValue("$speed", (object?)payload.LinkSpeedBitsPerSecond ?? DBNull.Value);
            command.Parameters.AddWithValue("$gateway", (object?)payload.GatewayAddress ?? DBNull.Value);

            // The reasoning this session was recorded under, taken from the payload rather
            // than from the current constants: an index rebuilt from an old chain has to
            // carry the old session's versions.
            command.Parameters.AddWithValue("$schema", payload.SchemaVersion);
            command.Parameters.AddWithValue("$classifier", payload.ClassifierVersion);
            command.Parameters.AddWithValue("$attribution", payload.AttributionModelVersion);
            command.Parameters.AddWithValue("$confidence", payload.ConfidenceModelVersion);

            command.ExecuteNonQuery();
        }
    }

    public void AddSample(string sessionId, SamplePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            _batch ??= _connection.BeginTransaction();

            using var command = _connection.CreateCommand();
            command.Transaction = _batch;
            command.CommandText =
                """
                INSERT INTO samples (session_id, n, utc, mono_ms, state, severity, phase, link, rtt_ms,
                                     gw_ok, gw_total, icmp_ok, icmp_total, tcp_ok, tcp_total, signal, overran)
                VALUES ($id, $n, $utc, $mono, $state, $severity, $phase, $link, $rtt,
                        $gwOk, $gwTotal, $icmpOk, $icmpTotal, $tcpOk, $tcpTotal, $signal, $overran)
                ON CONFLICT(session_id, n) DO NOTHING;
                """;

            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$n", payload.Sequence);
            command.Parameters.AddWithValue("$utc", payload.WallUtc.ToString("O"));
            command.Parameters.AddWithValue("$mono", payload.Monotonic.TotalMilliseconds);
            command.Parameters.AddWithValue("$state", payload.State.ToString());
            command.Parameters.AddWithValue("$severity", payload.Severity.ToString());
            command.Parameters.AddWithValue("$phase", payload.Phase);
            command.Parameters.AddWithValue("$link", payload.LinkStatus.ToString());
            command.Parameters.AddWithValue(
                "$rtt", (object?)payload.AverageRoundTrip?.TotalMilliseconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$gwOk", payload.Gateway.Succeeded);
            command.Parameters.AddWithValue("$gwTotal", payload.Gateway.Attempted);
            command.Parameters.AddWithValue("$icmpOk", payload.ExternalIcmp.Succeeded);
            command.Parameters.AddWithValue("$icmpTotal", payload.ExternalIcmp.Attempted);
            command.Parameters.AddWithValue("$tcpOk", payload.ExternalTcp.Succeeded);
            command.Parameters.AddWithValue("$tcpTotal", payload.ExternalTcp.Attempted);
            command.Parameters.AddWithValue("$signal", (object?)payload.SignalQualityPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$overran", payload.Overran ? 1 : 0);

            command.ExecuteNonQuery();

            if (++_pendingRows >= BatchSize)
            {
                FlushBatchCore();
            }
        }
    }

    public void AddIncident(string sessionId, IncidentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            // Incidents are the point of the whole exercise, so they land immediately
            // rather than waiting for a batch to fill.
            FlushBatchCore();

            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO incidents (session_id, number, started_utc, ended_utc, worst_state, attribution,
                                       duration_min_ms, duration_ms, duration_max_ms, sample_count,
                                       is_open, ended_by_gap, started_after_gap, route_changed,
                                       correlation_id, support, coverage, local_bps_peak, detail)
                VALUES ($id, $number, $started, $ended, $state, $attribution,
                        $min, $reported, $max, $samples, $open, $endedByGap, $startedAfterGap,
                        $routeChanged, $correlation, $support, $coverage, $localPeak, $detail)
                ON CONFLICT(session_id, number) DO UPDATE SET
                    ended_utc = excluded.ended_utc,
                    duration_ms = excluded.duration_ms,
                    is_open = excluded.is_open;
                """;

            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$number", payload.Number);
            command.Parameters.AddWithValue("$started", payload.StartedUtc.ToString("O"));
            command.Parameters.AddWithValue("$ended", payload.EndedUtc.ToString("O"));
            command.Parameters.AddWithValue("$state", payload.WorstState.ToString());
            command.Parameters.AddWithValue("$attribution", payload.Attribution.ToString());
            command.Parameters.AddWithValue("$min", payload.DurationMin.TotalMilliseconds);
            command.Parameters.AddWithValue("$reported", payload.DurationReported.TotalMilliseconds);
            command.Parameters.AddWithValue("$max", payload.DurationMax.TotalMilliseconds);
            command.Parameters.AddWithValue("$samples", payload.SampleCount);
            command.Parameters.AddWithValue("$open", payload.IsOpen ? 1 : 0);
            command.Parameters.AddWithValue("$endedByGap", payload.EndedByGap ? 1 : 0);
            command.Parameters.AddWithValue("$startedAfterGap", payload.StartedAfterGap ? 1 : 0);
            command.Parameters.AddWithValue("$routeChanged", payload.RouteChanged ? 1 : 0);
            command.Parameters.AddWithValue("$correlation", payload.CorrelationId.ToString());

            // Null rather than zero when nothing was scored: "not measured" and "measured
            // and came out at nothing" are different claims, and only one of them is bad news.
            command.Parameters.AddWithValue("$support", (object?)payload.Confidence?.Support ?? DBNull.Value);
            command.Parameters.AddWithValue("$coverage", (object?)payload.Confidence?.Coverage ?? DBNull.Value);

            // Same rule for our own traffic: absent means the counters were never read, which
            // is a different statement from a line that was quiet.
            command.Parameters.AddWithValue(
                "$localPeak", (object?)payload.PeakLocalTrafficBytesPerSecond ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", payload.TechnicalDetail);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Files a path trace.
    /// <para>
    /// The hops are stored as the same JSON the chain carries rather than as rows. They are
    /// only ever read back as a whole list belonging to one trace, and a second table would
    /// buy joins nobody needs while adding a second place for the format to drift.
    /// </para>
    /// </summary>
    public void AddTrace(string sessionId, TracePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            FlushBatchCore();

            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO traces (session_id, incident_number, phase, taken_utc, target, reached,
                                    private_hops, first_public_hop, last_answering_ttl, stops_at_home, hops)
                VALUES ($id, $incident, $phase, $utc, $target, $reached,
                        $privateHops, $firstPublic, $lastTtl, $stopsAtHome, $hops);
                """;

            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$incident", payload.IncidentNumber);
            command.Parameters.AddWithValue("$phase", payload.Phase);
            command.Parameters.AddWithValue("$utc", payload.TakenUtc.ToString("O"));
            command.Parameters.AddWithValue("$target", payload.Target);
            command.Parameters.AddWithValue("$reached", payload.ReachedTarget ? 1 : 0);
            command.Parameters.AddWithValue("$privateHops", payload.PrivateHopCount);
            command.Parameters.AddWithValue("$firstPublic", (object?)payload.FirstPublicHop ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastTtl", (object?)payload.LastAnsweringTtl ?? DBNull.Value);
            command.Parameters.AddWithValue("$stopsAtHome", payload.StopsInsideHomeNetwork ? 1 : 0);
            command.Parameters.AddWithValue("$hops", SerializeHops(payload.Hops));

            command.ExecuteNonQuery();
        }
    }

    private static string SerializeHops(IReadOnlyList<Core.Probes.TraceHop> hops)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var hop in hops)
            {
                writer.WriteStartObject();
                writer.WriteNumber("ttl", hop.Ttl);
                writer.WriteString("address", hop.Address);

                if (hop.RoundTrip is { } rtt)
                {
                    writer.WriteNumber("rttMs", Math.Round(rtt.TotalMilliseconds, 3));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public void AddGap(string sessionId, GapPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            FlushBatchCore();

            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO gaps (session_id, detected_utc, duration_ms, cause) VALUES ($id, $utc, $ms, $cause);";
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$utc", payload.DetectedUtc.ToString("O"));
            command.Parameters.AddWithValue("$ms", payload.Duration.TotalMilliseconds);
            command.Parameters.AddWithValue("$cause", payload.Cause.ToString());
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Fills in a running session's totals so a report asked for mid-test is not written
    /// against column defaults.
    /// <para>
    /// Every figure in the summary row is written once, by <see cref="CompleteSession"/>,
    /// from the engine's own accumulators. A session still running has no such entry, so a
    /// report built from it printed "Stvarno nadzirano 0,0 s" and "Dostupnost 100 %" beside
    /// a table of hundreds of samples and hours of recorded pauses. Both the console and the
    /// window offer a report mid-session, and a document that contradicts itself like that
    /// is worse than one that refuses to print.
    /// </para>
    /// <para>
    /// Monitored and gap time come from the last checkpoint the session wrote, never from
    /// the sample timestamps. That is not a preference: <c>IClock</c> records wall-clock
    /// time for display and correlation and forbids deriving a duration from it, because an
    /// NTP correction or a daylight-saving change would silently lengthen or shorten the
    /// result - and monitored time is the denominator of the availability figure that goes
    /// into the complaint. The checkpoint carries the engine's own totals, measured on the
    /// monotonic clock.
    /// </para>
    /// <para>
    /// Incident counts and downtime are taken from the indexed rows rather than from the
    /// checkpoint, because every incident the chain holds is already in the index while the
    /// checkpoint is up to a few minutes behind - and downtime is the sum of exactly those
    /// rows, so the table and the headline cannot disagree.
    /// </para>
    /// <para>
    /// With no checkpoint yet - a session only minutes old - the totals are left alone. A
    /// figure invented for a session too young to have written one down would be the same
    /// mistake in a new direction.
    /// </para>
    /// </summary>
    public void SummariseUnfinishedSession(string sessionId, CheckpointPayload? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            FlushBatchCore();

            var upstream = TimeSpan.FromMilliseconds(SumOf(
                """
                SELECT COALESCE(SUM(duration_ms), 0) FROM incidents
                WHERE session_id = $id AND attribution = 'Upstream';
                """, sessionId));

            // Everything that is not upstream counts as local, exactly as the engine does it -
            // including the undetermined ones, which must not quietly vanish from the downtime
            // total just because they could not be pinned to one side.
            var local = TimeSpan.FromMilliseconds(SumOf(
                """
                SELECT COALESCE(SUM(duration_ms), 0) FROM incidents
                WHERE session_id = $id AND attribution <> 'Upstream';
                """, sessionId));

            var incidents = (int)CountIncidents(sessionId);
            var upstreamIncidents = (int)SumOf(
                "SELECT COUNT(*) FROM incidents WHERE session_id = $id AND attribution = 'Upstream';",
                sessionId);

            var gap = checkpoint?.GapTime ?? RecordedGapTime(sessionId);

            // The checkpoint when there is one; otherwise the span the samples cover, less
            // the pauses inside it. Both readings are monotonic - the fallback reads mono_ms,
            // the engine's own counter carried across resumes, never the wall-clock column.
            // Sessions written in bursts shorter than the checkpoint interval, and every
            // session recorded before checkpoints were read back at all, have no anchor to
            // read and would otherwise report nothing at all.
            var monitored = checkpoint?.MonitoredTime ?? Elapsed(sessionId) - gap;

            if (monitored < TimeSpan.Zero)
            {
                monitored = TimeSpan.Zero;
            }

            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE sessions SET
                    monitored_ms = $monitored,
                    gap_ms = $gap,
                    upstream_downtime_ms = $upstream,
                    local_downtime_ms = $local,
                    availability = $availability,
                    upstream_availability = $upstreamAvailability,
                    incident_count = $incidents,
                    upstream_incident_count = $upstreamIncidents
                WHERE id = $id AND ended_utc IS NULL;
                """;

            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$monitored", monitored.TotalMilliseconds);
            command.Parameters.AddWithValue("$gap", gap.TotalMilliseconds);
            command.Parameters.AddWithValue("$upstream", upstream.TotalMilliseconds);
            command.Parameters.AddWithValue("$local", local.TotalMilliseconds);
            command.Parameters.AddWithValue("$availability", Availability(monitored, upstream + local));
            command.Parameters.AddWithValue("$upstreamAvailability", Availability(monitored, upstream));
            command.Parameters.AddWithValue("$incidents", incidents);
            command.Parameters.AddWithValue("$upstreamIncidents", upstreamIncidents);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Availability over monitored time. A hundred when nothing has been monitored yet,
    /// because any other figure drawn from no observations would be an invention.
    /// </summary>
    private static double Availability(TimeSpan monitored, TimeSpan downtime) =>
        monitored <= TimeSpan.Zero
            ? 100d
            : Math.Clamp(100d * (monitored - downtime) / monitored, 0d, 100d);

    /// <summary>
    /// How long the session has run, from the engine's monotonic counter.
    /// <para>
    /// <c>mono_ms</c> and not <c>utc</c>: <c>IClock</c> records wall-clock time for display
    /// and for correlation with the operator's logs, and forbids deriving a duration from
    /// it, because an NTP correction or a daylight-saving change would silently lengthen or
    /// shorten the result. Monitored time is the denominator of the availability figure that
    /// goes into a complaint, so it is the last number that may come from a clock somebody
    /// else can move.
    /// </para>
    /// </summary>
    private TimeSpan Elapsed(string sessionId) => TimeSpan.FromMilliseconds(SumOf(
        "SELECT COALESCE(MAX(mono_ms) - MIN(mono_ms), 0) FROM samples WHERE session_id = $id;",
        sessionId));

    private TimeSpan RecordedGapTime(string sessionId) => TimeSpan.FromMilliseconds(SumOf(
        "SELECT COALESCE(SUM(duration_ms), 0) FROM gaps WHERE session_id = $id;", sessionId));

    private double SumOf(string sql, string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", sessionId);

        return Convert.ToDouble(
            command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void CompleteSession(string sessionId, SessionEndPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            FlushBatchCore();

            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                UPDATE sessions SET
                    ended_utc = $ended,
                    monitored_ms = $monitored,
                    gap_ms = $gap,
                    upstream_downtime_ms = $upstream,
                    local_downtime_ms = $local,
                    availability = $availability,
                    upstream_availability = $upstreamAvailability,
                    incident_count = $incidents,
                    upstream_incident_count = $upstreamIncidents
                WHERE id = $id;
                """;

            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$ended", payload.EndedUtc.ToString("O"));
            command.Parameters.AddWithValue("$monitored", payload.MonitoredTime.TotalMilliseconds);
            command.Parameters.AddWithValue("$gap", payload.GapTime.TotalMilliseconds);
            command.Parameters.AddWithValue("$upstream", payload.UpstreamDowntime.TotalMilliseconds);
            command.Parameters.AddWithValue("$local", payload.LocalDowntime.TotalMilliseconds);
            command.Parameters.AddWithValue("$availability", payload.AvailabilityPercent);
            command.Parameters.AddWithValue("$upstreamAvailability", payload.UpstreamAvailabilityPercent);
            command.Parameters.AddWithValue("$incidents", payload.IncidentCount);
            command.Parameters.AddWithValue("$upstreamIncidents", payload.UpstreamIncidentCount);

            command.ExecuteNonQuery();
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            FlushBatchCore();
        }
    }

    public long CountSamples(string sessionId) => Scalar(
        "SELECT COUNT(*) FROM samples WHERE session_id = $id;", sessionId);

    public long CountIncidents(string sessionId) => Scalar(
        "SELECT COUNT(*) FROM incidents WHERE session_id = $id;", sessionId);

    private long Scalar(string sql, string sessionId)
    {
        lock (_gate)
        {
            FlushBatchCore();

            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", sessionId);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void FlushBatchCore()
    {
        if (_batch is null)
        {
            return;
        }

        _batch.Commit();
        _batch.Dispose();
        _batch = null;
        _pendingRows = 0;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            FlushBatchCore();
            _connection.Dispose();
        }
    }
}
