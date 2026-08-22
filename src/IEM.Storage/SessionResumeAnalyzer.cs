using System.Text.Json;
using IEM.Core;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Storage.Evidence;

namespace IEM.Storage;

public enum ResumeDecision
{
    /// <summary>No session found to continue.</summary>
    NothingToResume,

    /// <summary>The session already has a closing entry. Nothing to do.</summary>
    AlreadyClosed,

    /// <summary>
    /// The session was interrupted but its planned duration has since run out. It should
    /// be closed off with what it collected, not extended.
    /// </summary>
    Expired,

    /// <summary>The session was interrupted and still has time left on it.</summary>
    Resumable,

    /// <summary>
    /// The raw chain no longer verifies. The session is not continued: appending to a chain
    /// that has already been altered would produce a longer record with the same flaw buried
    /// deeper in it, and every later entry would inherit the doubt.
    /// </summary>
    IntegrityCompromised,
}

/// <param name="Remaining">Time left on the original plan. Meaningful only when resumable.</param>
public sealed record ResumeAnalysis(
    ResumeDecision Decision,
    SessionPaths? Paths,
    SessionStartRecord? Start,
    ResumeContext? Context,
    TimeSpan Remaining,
    TimeSpan Interruption)
{
    public static ResumeAnalysis None(ResumeDecision decision, SessionPaths? paths = null, SessionStartRecord? start = null) =>
        new(decision, paths, start, null, TimeSpan.Zero, TimeSpan.Zero);
}

/// <summary>
/// Decides whether an interrupted session can be picked up, and reconstructs the totals
/// it had reached.
/// <para>
/// Everything here is read from the raw chain rather than the index, because the chain is
/// the record. Reconstruction works from the most recent checkpoint and then replays only
/// the entries after it, so the result is exact rather than an estimate - and a report
/// built on estimated history would be indefensible the moment anyone checked it against
/// the log.
/// </para>
/// </summary>
public static class SessionResumeAnalyzer
{
    /// <summary>
    /// A gap longer than this between the last recorded sample and now means the machine
    /// was off or the monitor was not running, rather than a brief service restart. Both
    /// are recorded the same way; the distinction only affects the wording.
    /// </summary>
    public static readonly TimeSpan MaximumInterruption = TimeSpan.FromDays(7);

    /// <summary>Examines the most recent session under <paramref name="root"/>.</summary>
    public static ResumeAnalysis Analyze(string root, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var paths = SessionPaths.FindLatest(root);
        if (paths is null || !File.Exists(paths.RawLog))
        {
            return ResumeAnalysis.None(ResumeDecision.NothingToResume);
        }

        return Analyze(paths, now);
    }

    /// <summary>Examines one specific session directory.</summary>
    public static ResumeAnalysis Analyze(SessionPaths paths, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var start = ChainReader.ReadSessionStart(paths.RawLog);
        if (start is null)
        {
            return ResumeAnalysis.None(ResumeDecision.NothingToResume, paths);
        }

        if (ChainReader.IsClosed(paths.RawLog))
        {
            return ResumeAnalysis.None(ResumeDecision.AlreadyClosed, paths, start);
        }

        // Checked before anything is reconstructed from it. A chain that has been altered
        // must not be extended: every entry appended afterwards would chain from a record
        // already in doubt, and the doubt would spread to the part that was still sound.
        //
        // A half-written final line is a different thing entirely - it is what a killed
        // process leaves behind every time, and recovering from it is the reason resume
        // exists. Only a break that recovery cannot honestly repair stops the session.
        if (!ChainVerifier.Recover(paths.RawLog).CanContinue)
        {
            return ResumeAnalysis.None(ResumeDecision.IntegrityCompromised, paths, start);
        }

        var state = Reconstruct(paths.RawLog);

        // Measured from the last sample rather than from the session start, because that is
        // the last moment anything was actually observed.
        var interruption = state.LastSampleUtc is { } lastSample
            ? now - lastSample
            : now - start.StartedUtc;

        if (interruption < TimeSpan.Zero)
        {
            // The wall clock moved backwards between runs. Claiming a negative pause would
            // be nonsense, so it is treated as no measurable pause at all.
            interruption = TimeSpan.Zero;
        }

        var remaining = start.PlannedDuration == Timeout.InfiniteTimeSpan
            ? Timeout.InfiniteTimeSpan
            : start.PlannedDuration - (state.SessionElapsed + interruption);

        // The reconstruction travels with the Expired decision as well. The session will
        // not be continued, but the closer that seals it writes its closing entry from these
        // totals - and a decision that threw them away made every abandoned session close
        // with zeros: a hundred-percent availability asserted, in the hash chain, over a
        // session that may have died mid-outage.
        if (interruption > MaximumInterruption)
        {
            return Expired(paths, start, state, interruption);
        }

        if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
        {
            return Expired(paths, start, state, interruption);
        }

        var context = new ResumeContext(
            state.Prior,
            state.Prior.IncidentCount,
            state.LastSampleSequence,
            interruption)
        {
            ElapsedBefore = state.SessionElapsed + interruption,
            OpenIncident = state.OpenIncident,
            Environment = state.Environment,
        };

        return new ResumeAnalysis(ResumeDecision.Resumable, paths, start, context, remaining, interruption);
    }

    /// <summary>The same reconstruction the Resumable path carries, attached to an Expired decision.</summary>
    private static ResumeAnalysis Expired(
        SessionPaths paths,
        SessionStartRecord start,
        ReconstructedState state,
        TimeSpan interruption) =>
        new(
            ResumeDecision.Expired,
            paths,
            start,
            new ResumeContext(
                state.Prior,
                state.Prior.IncidentCount,
                state.LastSampleSequence,
                interruption)
            {
                ElapsedBefore = state.SessionElapsed + interruption,
                OpenIncident = state.OpenIncident,
                Environment = state.Environment,
            },
            TimeSpan.Zero,
            interruption);

    /// <summary>
    /// Rebuilds the totals from the last checkpoint plus every entry after it.
    /// <para>
    /// Falls back to replaying the whole chain when there is no checkpoint - a session
    /// that died in its first few minutes, for instance. Slower, but correct, and the
    /// alternative would be to start such a session over.
    /// </para>
    /// </summary>
    private static ReconstructedState Reconstruct(string rawLogPath)
    {
        var prior = PriorTotals.None;
        var elapsed = TimeSpan.Zero;
        long lastSequence = 0;
        DateTimeOffset? lastSampleUtc = null;

        // Incidents and gaps that landed after the last checkpoint, which the checkpoint
        // therefore does not yet account for.
        var extraUpstreamDowntime = TimeSpan.Zero;
        var extraLocalDowntime = TimeSpan.Zero;
        var extraGap = TimeSpan.Zero;
        var extraDegraded = TimeSpan.Zero;
        var extraIncidents = 0;
        var extraUpstreamIncidents = 0;
        var extraLongestUpstream = TimeSpan.Zero;

        // The interval between two samples is booked, by the engine, to the previous
        // sample's severity. The same rule is applied here so the reconstruction cannot
        // disagree with a live run over the same samples.
        //
        // Survives a checkpoint, deliberately: a checkpoint is written on the sample path
        // right after the sample it follows, so it folds every interval up to and including
        // that one - the interval still pending across it was booked only when the next
        // sample arrived, and belongs to the tail. A gap breaks the run instead: that
        // interval was booked as gap time and must not be counted as degraded as well.
        (TimeSpan Mono, Severity Severity)? pendingIntervalFrom = null;

        // An outage in progress when the process died. Its failing samples are in the chain,
        // but the segment that summarises them is only written when it closes - so without
        // this the evidence would sit in the log and never reach a single statistic.
        var openBuilder = new OpenIncidentBuilder();
        NetworkEnvironment? environment = null;

        foreach (var entry in ChainReader.Read(rawLogPath))
        {
            switch (entry.Kind)
            {
                case EvidenceKind.Checkpoint:
                    prior = ReadCheckpoint(entry.Payload, out elapsed, out lastSequence);

                    // Everything before this point is now folded into the checkpoint.
                    extraUpstreamDowntime = TimeSpan.Zero;
                    extraLocalDowntime = TimeSpan.Zero;
                    extraGap = TimeSpan.Zero;
                    extraDegraded = TimeSpan.Zero;
                    extraIncidents = 0;
                    extraUpstreamIncidents = 0;
                    extraLongestUpstream = TimeSpan.Zero;
                    break;

                case EvidenceKind.Sample:
                    if (entry.Payload.TryGetProperty("n", out var n) && n.TryGetInt64(out var sequence))
                    {
                        lastSequence = Math.Max(lastSequence, sequence);
                    }

                    if (ReadDate(entry.Payload, "utc") is { } sampleUtc)
                    {
                        lastSampleUtc = sampleUtc;
                    }

                    var mono = ReadMilliseconds(entry.Payload, "mono");

                    if (mono is { } sampleElapsed && sampleElapsed > elapsed)
                    {
                        elapsed = sampleElapsed;
                    }

                    AccountDegradedInterval(entry.Payload, mono);
                    openBuilder.Observe(entry.Payload);
                    break;

                case EvidenceKind.Incident:
                    AccumulateIncident(entry.Payload);

                    // The segment reached the log, so it is no longer open.
                    openBuilder.Reset();
                    break;

                case EvidenceKind.EnvironmentChange:
                    // The connection the session is about, as last recorded. Carried across
                    // so a router swapped during the interruption is still noticed.
                    environment = PayloadReader.Environment(entry.Payload) ?? environment;
                    break;

                case EvidenceKind.Gap:
                    if (ReadMilliseconds(entry.Payload, "durationMs") is { } gap)
                    {
                        extraGap += gap;
                    }

                    // Nothing was observed across the pause, so a run of failing samples on
                    // either side of it is not one continuous stretch.
                    openBuilder.Reset();

                    // The interval spanning the pause was booked as gap time, not monitored
                    // time, so it carries no degraded time either.
                    pendingIntervalFrom = null;
                    break;

                default:
                    break;
            }
        }

        var totals = prior with
        {
            GapTime = prior.GapTime + extraGap,
            DegradedTime = prior.DegradedTime + extraDegraded,
            UpstreamDowntime = prior.UpstreamDowntime + extraUpstreamDowntime,
            LocalDowntime = prior.LocalDowntime + extraLocalDowntime,
            IncidentCount = prior.IncidentCount + extraIncidents,
            UpstreamIncidentCount = prior.UpstreamIncidentCount + extraUpstreamIncidents,
            LongestUpstreamOutage = extraLongestUpstream > prior.LongestUpstreamOutage
                ? extraLongestUpstream
                : prior.LongestUpstreamOutage,
        };

        // Monitored time is the observed span minus everything that was not observed.
        // Derived rather than accumulated so it can never drift from the other two.
        var monitored = elapsed - totals.GapTime;
        if (monitored < TimeSpan.Zero)
        {
            monitored = TimeSpan.Zero;
        }

        totals = totals with { MonitoredTime = monitored };

        return new ReconstructedState(totals, elapsed, lastSequence, lastSampleUtc, openBuilder.Build(), environment);

        void AccountDegradedInterval(JsonElement payload, TimeSpan? mono)
        {
            if (mono is not { } at || TryReadSeverity(payload) is not { } severity)
            {
                // An interval whose bookend cannot be read is left unattributed rather
                // than guessed at.
                pendingIntervalFrom = null;
                return;
            }

            if (pendingIntervalFrom is { } from && from.Severity == Severity.Degraded)
            {
                var delta = at - from.Mono;

                if (delta > TimeSpan.Zero)
                {
                    extraDegraded += delta;
                }
            }

            pendingIntervalFrom = (at, severity);
        }

        void AccumulateIncident(JsonElement payload)
        {
            var duration = ReadMilliseconds(payload, "durationMs") ?? TimeSpan.Zero;
            var attribution = payload.TryGetProperty("attribution", out var value) ? value.GetString() : null;
            var isUpstream = string.Equals(attribution, nameof(FaultAttribution.Upstream), StringComparison.Ordinal);

            extraIncidents++;

            if (isUpstream)
            {
                extraUpstreamIncidents++;
                extraUpstreamDowntime += duration;

                if (duration > extraLongestUpstream)
                {
                    extraLongestUpstream = duration;
                }
            }
            else
            {
                extraLocalDowntime += duration;
            }
        }
    }

    private static PriorTotals ReadCheckpoint(JsonElement payload, out TimeSpan elapsed, out long lastSequence)
    {
        elapsed = ReadMilliseconds(payload, "elapsedMs") ?? TimeSpan.Zero;
        lastSequence = payload.TryGetProperty("lastSample", out var value) && value.TryGetInt64(out var sequence)
            ? sequence
            : 0;

        return new PriorTotals(
            ReadMilliseconds(payload, "monitoredMs") ?? TimeSpan.Zero,
            ReadMilliseconds(payload, "gapMs") ?? TimeSpan.Zero,
            ReadMilliseconds(payload, "degradedMs") ?? TimeSpan.Zero,
            ReadMilliseconds(payload, "upstreamDowntimeMs") ?? TimeSpan.Zero,
            ReadMilliseconds(payload, "localDowntimeMs") ?? TimeSpan.Zero,
            ReadInt(payload, "incidents"),
            ReadInt(payload, "upstreamIncidents"),
            ReadMilliseconds(payload, "longestUpstreamMs") ?? TimeSpan.Zero);
    }

    private static TimeSpan? ReadMilliseconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromMilliseconds(value.GetDouble())
            : null;

    /// <summary>
    /// The severity recorded with the sample, which is what the engine booked the following
    /// interval against. Derived from the state when the field is absent, so an older chain
    /// still replays.
    /// </summary>
    private static Severity? TryReadSeverity(JsonElement payload)
    {
        if (payload.TryGetProperty("severity", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<Severity>(value.GetString(), out var severity))
        {
            return severity;
        }

        return payload.TryGetProperty("state", out var state) &&
               state.ValueKind == JsonValueKind.String &&
               Enum.TryParse<NetworkState>(state.GetString(), out var parsed)
            ? parsed.SeverityOf()
            : null;
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static DateTimeOffset? ReadDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(
            value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private sealed record ReconstructedState(
        PriorTotals Prior,
        TimeSpan SessionElapsed,
        long LastSampleSequence,
        DateTimeOffset? LastSampleUtc,
        OpenIncidentState? OpenIncident,
        NetworkEnvironment? Environment);

    /// <summary>
    /// Walks the samples and keeps hold of the trailing run of failing ones, so an outage
    /// that was still in progress when the process died can be closed off properly instead
    /// of vanishing from the statistics.
    /// </summary>
    private sealed class OpenIncidentBuilder
    {
        private readonly List<NetworkState> _statesSeen = [];

        private SampleInstant? _lastGood;
        private SampleInstant? _firstBad;
        private SampleInstant _lastBad;
        private string _detail = string.Empty;
        private int _sampleCount;

        public void Observe(JsonElement payload)
        {
            if (!payload.TryGetProperty("state", out var value) ||
                !Enum.TryParse<NetworkState>(value.GetString(), out var state))
            {
                return;
            }

            if (ReadDate(payload, "utc") is not { } wall || ReadMilliseconds(payload, "mono") is not { } monotonic)
            {
                return;
            }

            var instant = new SampleInstant(monotonic, wall);

            // A pause recorded as a sample is neither, so it ends the run without becoming
            // part of it.
            if (state == NetworkState.MonitoringGap)
            {
                Reset();
                return;
            }

            if (!state.IsOutage())
            {
                Reset();
                _lastGood = instant;
                return;
            }

            if (_firstBad is null)
            {
                _firstBad = instant;
                _detail = payload.TryGetProperty("detail", out var detail) ? detail.GetString() ?? string.Empty : string.Empty;
                _sampleCount = 0;
                _statesSeen.Clear();
            }

            _lastBad = instant;
            _sampleCount++;

            if (!_statesSeen.Contains(state))
            {
                _statesSeen.Add(state);
            }
        }

        public void Reset()
        {
            _firstBad = null;
            _sampleCount = 0;
            _statesSeen.Clear();
        }

        public OpenIncidentState? Build() => _firstBad is { } firstBad
            ? new OpenIncidentState(_lastGood, firstBad, _lastBad, [.. _statesSeen], _sampleCount, _detail)
            : null;
    }
}
