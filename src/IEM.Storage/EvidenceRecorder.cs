using System.Diagnostics;
using IEM.Core;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Storage.Evidence;

namespace IEM.Storage;

/// <summary>File layout of one recorded session.</summary>
public sealed record SessionPaths(string Directory)
{
    /// <summary>The evidence itself: append-only, hash-chained.</summary>
    public string RawLog => Path.Combine(Directory, "SirovaEvidencija.jsonl");

    /// <summary>Derived index. Rebuildable from <see cref="RawLog"/>.</summary>
    public string Database => Path.Combine(Directory, "sesija.db");

    public string ChainVerification => Path.Combine(Directory, "Provera-lanca.txt");

    /// <summary>
    /// Names a session directory after its start time. Sortable, unambiguous, and free of
    /// diacritics so the folder survives being zipped and emailed to an operator.
    /// </summary>
    public static SessionPaths ForNewSession(string root, DateTimeOffset startedAt) =>
        new(Path.Combine(root, $"Sesija_{startedAt.ToLocalTime():yyyyMMdd_HHmmss}"));

    /// <summary>Most recent session directory under <paramref name="root"/>, if any.</summary>
    public static SessionPaths? FindLatest(string root)
    {
        if (!System.IO.Directory.Exists(root))
        {
            return null;
        }

        var latest = new DirectoryInfo(root)
            .GetDirectories("Sesija_*")
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        return latest is null ? null : new SessionPaths(latest.FullName);
    }
}

/// <summary>
/// How often the recorder forces data to disk and anchors its totals.
/// <para>
/// Configurable mainly so the checkpoint path can be exercised by tests. Left on its real
/// interval it would only ever run during multi-hour sessions, and untested recovery code
/// is precisely what fails at hour thirty of a two-day test.
/// </para>
/// </summary>
public sealed record RecorderOptions
{
    public static readonly RecorderOptions Default = new();

    /// <summary>
    /// How long an uneventful stretch may go before the log is forced to physical disk.
    /// Bounds what an abrupt power loss can cost; anything interesting syncs immediately.
    /// </summary>
    public TimeSpan IdleSyncInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often the running totals are anchored into the chain.
    /// <para>
    /// This is the granularity a resumed session rebuilds from. Entries after the last
    /// checkpoint are replayed exactly, so the interval trades file size against startup
    /// work rather than against accuracy.
    /// </para>
    /// </summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long before the first one is written.
    /// <para>
    /// Shorter than the rest on purpose. The checkpoint is also the only place a session
    /// that has not been closed keeps its totals, so until one exists a report asked for
    /// mid-test has no monitored time to divide by. A minute is the same threshold below
    /// which the shared verdict refuses to conclude anything at all, so every session the
    /// tool is willing to draw a conclusion from has written its figures down.
    /// </para>
    /// </summary>
    public TimeSpan FirstCheckpointDelay { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Writes everything the engine observes to the raw chain and the index.
/// <para>
/// Chain first, database second, always. If the process dies between the two the chain
/// still holds the fact and the index is merely behind - the reverse would mean the
/// index asserting something the evidence cannot support.
/// </para>
/// </summary>
public sealed class EvidenceRecorder : IDisposable
{
    /// <summary>
    /// How often to force the log to physical disk during uneventful stretches. Anything
    /// interesting is synced the moment it happens; this only bounds what an abrupt power
    /// loss can cost during quiet periods.
    /// </summary>
    private readonly RecorderOptions _options;
    private readonly MonitorEngine _engine;
    private readonly HashChainWriter _chain;
    private readonly SqliteSessionStore _store;

    private NetworkState? _lastState;
    private long _lastSyncTicks;
    private long _lastCheckpointTicks;
    private int _checkpointsWritten;
    private bool _completed;
    private bool _disposed;

    private EvidenceRecorder(
        MonitorEngine engine,
        HashChainWriter chain,
        SqliteSessionStore store,
        SessionPaths paths,
        string sessionId,
        RecorderOptions options)
    {
        _engine = engine;
        _chain = chain;
        _store = store;
        Paths = paths;
        SessionId = sessionId;
        _options = options;
        _lastSyncTicks = Stopwatch.GetTimestamp();
        _lastCheckpointTicks = _lastSyncTicks;

        _engine.SampleRecorded += OnSample;
        _engine.IncidentClosed += OnIncident;
        _engine.GapDetected += OnGap;
        _engine.ClockAnomalyDetected += OnClockAnomaly;
        _engine.NetworkEnvironmentChanged += OnEnvironmentChanged;
    }

    public SessionPaths Paths { get; }

    public string SessionId { get; }

    public long EntriesWritten => _chain.EntriesWritten;

    /// <summary>
    /// Opens a session directory and records its opening entry.
    /// <para>
    /// Reopening an existing directory continues the chain rather than starting a new one,
    /// which is what makes a session survive the service being restarted mid-test.
    /// </para>
    /// </summary>
    public static EvidenceRecorder Start(
        SessionPaths paths,
        MonitorEngine engine,
        SessionStartPayload start,
        RecorderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(start);

        Directory.CreateDirectory(paths.Directory);

        var chain = HashChainWriter.Open(paths.RawLog);
        var store = SqliteSessionStore.Open(paths.Database);

        var recorder = new EvidenceRecorder(
            engine, chain, store, paths, start.SessionId, options ?? RecorderOptions.Default);

        chain.Append(start);
        chain.FlushToDisk();
        store.BeginSession(start.SessionId, start);

        return recorder;
    }

    /// <summary>
    /// Attaches to a session that already exists, without opening a second one.
    /// <para>
    /// The chain keeps its original opening entry, so the resumed session remains one
    /// session rather than becoming two that happen to share a folder. The interruption
    /// itself is recorded separately, by the engine, as a gap.
    /// </para>
    /// </summary>
    public static EvidenceRecorder Resume(
        SessionPaths paths,
        MonitorEngine engine,
        string sessionId,
        RecorderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!File.Exists(paths.RawLog))
        {
            throw new FileNotFoundException("Nema sesije za nastavak u ovom folderu.", paths.RawLog);
        }

        var chain = HashChainWriter.Open(paths.RawLog);
        var store = SqliteSessionStore.Open(paths.Database);

        return new EvidenceRecorder(
            engine, chain, store, paths, sessionId, options ?? RecorderOptions.Default);
    }

    /// <summary>
    /// Whether anything may still be written to the chain.
    /// <para>
    /// Once the session is closed the chain is finished, and a record appended after the
    /// closing entry would read as "the session ended, and then more things happened" - a
    /// contradiction on the face of the evidence, and the first thing anyone checking it
    /// would seize on. A probe that answers late is common enough that this cannot be left
    /// to the shutdown happening to run in the right order.
    /// </para>
    /// <para>
    /// Refused records are counted rather than merely dropped, so the fact reaches the
    /// service log instead of vanishing.
    /// </para>
    /// </summary>
    private bool Accepts(string kind)
    {
        if (!_completed)
        {
            return true;
        }

        _refusedAfterClose++;
        _lastRefusedKind = kind;
        return false;
    }

    private long _refusedAfterClose;
    private string? _lastRefusedKind;

    /// <summary>Records that arrived after the session was closed and were refused.</summary>
    public long RefusedAfterClose => _refusedAfterClose;

    /// <summary>Kind of the most recently refused record, for the service log.</summary>
    public string? LastRefusedKind => _lastRefusedKind;

    private void OnSample(MonitorSample sample)
    {
        if (!Accepts(nameof(EvidenceKind.Sample)))
        {
            return;
        }

        var payload = SamplePayload.From(sample);

        _chain.Append(payload);
        _store.AddSample(SessionId, payload);

        // A state change is the part of the log a reader will actually look at, so it is
        // worth the cost of a real disk sync. Steady stretches sync on a timer instead.
        var stateChanged = _lastState != sample.Verdict.State;
        _lastState = sample.Verdict.State;

        if (stateChanged || Stopwatch.GetElapsedTime(_lastSyncTicks) >= _options.IdleSyncInterval)
        {
            _chain.FlushToDisk();
            _lastSyncTicks = Stopwatch.GetTimestamp();
        }

        var due = _checkpointsWritten == 0 ? _options.FirstCheckpointDelay : _options.CheckpointInterval;

        if (Stopwatch.GetElapsedTime(_lastCheckpointTicks) >= due)
        {
            WriteCheckpoint();
        }
    }

    /// <summary>
    /// Anchors the running totals into the chain, so an interrupted session can be resumed
    /// from here rather than from the beginning.
    /// </summary>
    private void WriteCheckpoint()
    {
        _chain.Append(CheckpointPayload.From(
            _engine.Statistics,
            DateTimeOffset.UtcNow,
            _engine.SessionElapsed,
            _engine.LastSampleSequence));

        _chain.FlushToDisk();
        _lastCheckpointTicks = Stopwatch.GetTimestamp();
        _lastSyncTicks = _lastCheckpointTicks;
        _checkpointsWritten++;
    }

    private void OnIncident(IncidentRecord incident)
    {
        if (!Accepts(nameof(EvidenceKind.Incident)))
        {
            return;
        }

        var payload = IncidentPayload.From(incident);

        _chain.Append(payload);
        _chain.FlushToDisk();
        _store.AddIncident(SessionId, payload);
        _lastSyncTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Records that the connection under test became a different connection.
    /// <para>
    /// Forced to disk immediately. A change of network mid-session decides what the whole
    /// report is about, so it must not be the entry lost to a crash a second later.
    /// </para>
    /// </summary>
    private void OnEnvironmentChanged(NetworkEnvironmentChange change)
    {
        if (!Accepts(nameof(EvidenceKind.EnvironmentChange)))
        {
            return;
        }

        _chain.Append(EnvironmentPayload.From(
            change.Current, change.At.Wall, change.At.Monotonic, change.Differences));

        _chain.FlushToDisk();
        _lastSyncTicks = Stopwatch.GetTimestamp();
    }

    private void OnGap(MonitoringGapEvent gap)
    {
        if (!Accepts(nameof(EvidenceKind.Gap)))
        {
            return;
        }

        var payload = new GapPayload(gap.DetectedAt.Wall, gap.Duration, gap.Cause);

        _chain.Append(payload);
        _chain.FlushToDisk();
        _store.AddGap(SessionId, payload);
        _lastSyncTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Records a path trace. Called from the tracer's own thread once a trace finishes,
    /// which may be seconds after the incident that prompted it.
    /// </summary>
    public void RecordTrace(Core.Probes.IncidentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        // The likeliest late write in the whole system: a trace is started when an incident
        // boundary is seen and can take tens of seconds to walk the path, so one begun just
        // before the user pressed stop will routinely finish after the session is closed.
        if (!Accepts(nameof(EvidenceKind.Trace)))
        {
            return;
        }

        var payload = TracePayload.From(trace);

        _chain.Append(payload);
        _chain.FlushToDisk();
        _store.AddTrace(SessionId, payload);
    }

    private void OnClockAnomaly(Core.Time.ClockObservation observation)
    {
        if (!Accepts(nameof(EvidenceKind.ClockAnomaly)))
        {
            return;
        }

        _chain.Append(new ClockAnomalyPayload(
            DateTimeOffset.UtcNow, observation.Anomaly, observation.Skew, observation.MonotonicDelta));
        _chain.FlushToDisk();
    }

    /// <summary>
    /// Closes the session with its totals and writes the chain verification alongside it,
    /// so whoever receives the package can check it without running this tool.
    /// </summary>
    public ChainVerification Complete(SessionStatistics statistics, DateTimeOffset endedUtc)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        if (_completed)
        {
            return ChainVerifier.Verify(Paths.RawLog);
        }

        _completed = true;

        var payload = SessionEndPayload.From(statistics, endedUtc);
        _chain.Append(payload);
        _chain.FlushToDisk();
        _store.CompleteSession(SessionId, payload);
        _store.Flush();

        var verification = ChainVerifier.Verify(Paths.RawLog);
        WriteVerificationReport(verification);
        return verification;
    }

    private void WriteVerificationReport(ChainVerification verification)
    {
        var status = verification.Valid ? "ISPRAVAN" : "NARUŠEN";

        var lines = new List<string>
        {
            "PROVERA INTEGRITETA SIROVE EVIDENCIJE",
            "",
            $"Fajl:            {Path.GetFileName(Paths.RawLog)}",
            $"Sesija:          {SessionId}",
            $"Provereno zapisa: {verification.EntriesChecked}",
            $"Rezultat:        {status}",
        };

        if (!verification.Valid)
        {
            lines.Add($"Prvi narušen red: {verification.FirstBrokenLine}");
            lines.Add($"Razlog:           {verification.Reason}");
        }

        lines.AddRange(
        [
            $"Završni otisak:  {verification.HeadHash}",
            "",
            "Svaki zapis sadrži otisak prethodnog, pa izmena bilo kog ranijeg reda",
            "narušava sve otiske posle njega.",
            "",
            .. TextWrap.Lines(ChainText.NotProofOfOrigin),
        ]);

        File.WriteAllLines(Paths.ChainVerification, lines, new System.Text.UTF8Encoding(true));
    }

    /// <summary>
    /// Unsubscribes and closes both stores. Safe to call more than once: the shutdown path
    /// runs from several places and a second call must not turn a finished session into a
    /// crash after the evidence was already safely written.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _engine.SampleRecorded -= OnSample;
        _engine.IncidentClosed -= OnIncident;
        _engine.GapDetected -= OnGap;
        _engine.ClockAnomalyDetected -= OnClockAnomaly;
        _engine.NetworkEnvironmentChanged -= OnEnvironmentChanged;

        _chain.Dispose();
        _store.Dispose();
    }
}
