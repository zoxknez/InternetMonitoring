using System.Text.Json;
using IEM.Core;
using IEM.Core.Classification;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Core.Time;

namespace IEM.Storage.Evidence;

public enum EvidenceKind
{
    SessionStart,
    Sample,
    Incident,
    Gap,
    ClockAnomaly,
    Checkpoint,
    Trace,
    SessionEnd,

    /// <summary>The connection under test became a different connection.</summary>
    EnvironmentChange,
}

/// <summary>
/// One thing worth recording.
/// <para>
/// Every payload writes its own fields in a fixed order rather than going through
/// reflection-based serialisation. That is not stylistic: the hash chain is only
/// verifiable if the exact same record always produces the exact same bytes, and property
/// ordering that depends on reflection order, compiler version or serialiser settings
/// would silently break every chain written by an earlier build.
/// </para>
/// </summary>
public interface IEvidencePayload
{
    EvidenceKind Kind { get; }

    /// <summary>Writes the payload fields. The writer is already inside the record object.</summary>
    void WriteTo(Utf8JsonWriter writer);
}

/// <summary>Opens a session and pins down the conditions it was recorded under.</summary>
public sealed record SessionStartPayload(
    string SessionId,
    string ToolVersion,
    DateTimeOffset StartedUtc,
    TimeSpan PlannedDuration,
    string MachineName,
    string InterfaceName,
    LinkMedium Medium,
    long? LinkSpeedBitsPerSecond,
    string? GatewayAddress) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.SessionStart;

    /// <summary>
    /// Which reasoning this session was recorded under.
    /// <para>
    /// Carried as data rather than written straight from the constants, because a report can
    /// be rebuilt years later by a newer build - and printing today's version numbers over
    /// yesterday's session is exactly the confusion these fields exist to prevent. New
    /// sessions default to the current values; a session read back from the chain reports
    /// whatever it was recorded with.
    /// </para>
    /// </summary>
    public int SchemaVersion { get; init; } = EvidenceModelVersion.SchemaVersion;

    public string ClassifierVersion { get; init; } = EvidenceModelVersion.ClassifierVersion;

    public string AttributionModelVersion { get; init; } = EvidenceModelVersion.AttributionModelVersion;

    public string ConfidenceModelVersion { get; init; } = EvidenceModelVersion.ConfidenceModelVersion;

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("sessionId", SessionId);
        writer.WriteString("toolVersion", ToolVersion);
        writer.WriteString("startedUtc", StartedUtc.ToString("O"));
        writer.WriteString("plannedDuration", PlannedDuration == Timeout.InfiniteTimeSpan ? "infinite" : PlannedDuration.ToString("c"));
        writer.WriteString("machine", MachineName);
        writer.WriteString("interface", InterfaceName);
        writer.WriteString("medium", Medium.ToString());
        WriteNullableNumber(writer, "linkSpeedBps", LinkSpeedBitsPerSecond);
        writer.WriteString("gateway", GatewayAddress);

        // Which reasoning produced this session's conclusions. The measurements below mean
        // the same thing forever; the rules that interpret them do not, and a reader years
        // from now must be able to tell which set was applied.
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("classifierVersion", ClassifierVersion);
        writer.WriteString("attributionModelVersion", AttributionModelVersion);
        writer.WriteString("confidenceModelVersion", ConfidenceModelVersion);
    }

    internal static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    internal static void WriteNullableMilliseconds(Utf8JsonWriter writer, string name, TimeSpan? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, Math.Round(value.Value.TotalMilliseconds, 3));
        }
    }
}

/// <summary>
/// One sample. The bulk of the log.
/// <para>
/// Probe tallies are stored rather than every individual probe result. The tallies are
/// what the classification actually rests on, and at ten samples a second during an
/// incident the full detail would multiply the file size without adding anything a reader
/// could use.
/// </para>
/// </summary>
public sealed record SamplePayload(
    long Sequence,
    DateTimeOffset WallUtc,
    TimeSpan Monotonic,
    NetworkState State,
    Severity Severity,
    string TechnicalDetail,
    string Phase,
    LinkStatus LinkStatus,
    TimeSpan? AverageRoundTrip,
    ProbeTally Gateway,
    ProbeTally ExternalIcmp,
    ProbeTally ExternalTcp,
    ProbeTally ExternalTls,
    ProbeTally DnsIsp,
    ProbeTally DnsPublic,
    ProbeTally DnsSystem,
    ProbeTally Http,
    bool Overran,
    int? SignalQualityPercent,
    string? Bssid) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.Sample;

    /// <summary>
    /// Adapter every resolved probe agreed it left through, or null when they disagreed.
    /// <para>
    /// The single most important field for attribution, and the reason it is on every
    /// sample rather than stated once per session: Windows picks the adapter per
    /// destination and can change its mind mid-outage. A record that names the link only at
    /// the start cannot tell a failed Wi-Fi from a silent failover onto Ethernet.
    /// </para>
    /// </summary>
    public string? InterfaceId { get; init; }

    public string? SourceAddress { get; init; }

    /// <summary>Probes that went out pinned to that source rather than merely predicted to.</summary>
    public int BoundProbes { get; init; }

    /// <summary>Traffic was leaving through more than one adapter at this moment.</summary>
    public bool MultiplePaths { get; init; }

    /// <summary>
    /// How much traffic this machine itself was putting through the link, in bytes per
    /// second. Null when the adapter counters could not be read.
    /// <para>
    /// On every sample rather than summarised per incident, because it is the one figure that
    /// tells a genuine outage apart from the computer having used the whole line itself - and
    /// whoever checks the package later has to be able to see it for the exact second in
    /// question rather than take a verdict's word for it.
    /// </para>
    /// </summary>
    public long? LocalTrafficBytesPerSecond { get; init; }

    public static SamplePayload From(MonitorSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var cycle = sample.Cycle;
        var wireless = cycle.Link.Wireless;

        return new SamplePayload(
            sample.Sequence,
            sample.Instant.Wall,
            sample.Instant.Monotonic,
            sample.Verdict.State,
            sample.Verdict.Severity,
            sample.Verdict.TechnicalDetail,
            sample.Phase.ToString(),
            cycle.Link.Status,
            cycle.AverageExternalRoundTrip,
            cycle.Gateway,
            cycle.ExternalIcmp,
            cycle.ExternalTcp,
            cycle.ExternalTls,
            cycle.DnsIsp,
            cycle.DnsPublic,
            cycle.DnsSystem,
            cycle.Http,
            cycle.Overran,
            wireless?.SignalQualityPercent,
            wireless?.Bssid)
        {
            InterfaceId = cycle.AgreedInterfaceId,
            SourceAddress = cycle.AgreedSourceAddress,
            BoundProbes = cycle.BoundProbeCount,
            MultiplePaths = cycle.MultiplePathsInUse,
            LocalTrafficBytesPerSecond = cycle.LocalTrafficBytesPerSecond,
        };
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumber("n", Sequence);
        writer.WriteString("utc", WallUtc.ToString("O"));
        writer.WriteNumber("mono", Math.Round(Monotonic.TotalMilliseconds, 3));
        writer.WriteString("state", State.ToString());
        writer.WriteString("severity", Severity.ToString());
        writer.WriteString("phase", Phase);
        writer.WriteString("link", LinkStatus.ToString());
        SessionStartPayload.WriteNullableMilliseconds(writer, "rttMs", AverageRoundTrip);

        WriteTally(writer, "gw", Gateway);
        WriteTally(writer, "icmp", ExternalIcmp);
        WriteTally(writer, "tcp", ExternalTcp);
        WriteTally(writer, "tls", ExternalTls);
        WriteTally(writer, "dnsIsp", DnsIsp);
        WriteTally(writer, "dnsPub", DnsPublic);
        WriteTally(writer, "dnsSys", DnsSystem);
        WriteTally(writer, "http", Http);

        writer.WriteBoolean("overran", Overran);

        if (SignalQualityPercent is { } signal)
        {
            writer.WriteNumber("signal", signal);
        }

        if (Bssid is not null)
        {
            writer.WriteString("bssid", Bssid);
        }

        if (InterfaceId is not null)
        {
            writer.WriteString("iface", InterfaceId);
        }

        if (SourceAddress is not null)
        {
            writer.WriteString("src", SourceAddress);
        }

        if (BoundProbes > 0)
        {
            writer.WriteNumber("bound", BoundProbes);
        }

        if (MultiplePaths)
        {
            writer.WriteBoolean("multiPath", true);
        }

        // Written only when it was actually read. An absent field means "not known", which
        // is a different statement from a quiet line and must stay distinguishable in a
        // record somebody may read years later.
        if (LocalTrafficBytesPerSecond is { } localBps)
        {
            writer.WriteNumber("localBps", localBps);
        }

        writer.WriteString("detail", TechnicalDetail);
    }

    private static void WriteTally(Utf8JsonWriter writer, string name, ProbeTally tally)
    {
        // Silent families are written as null rather than 0/0, so "not attempted" cannot
        // be misread as "attempted and failed" by anything reading the log later.
        if (tally.IsSilent)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteString(name, $"{tally.Succeeded}/{tally.Attempted}");
    }
}

/// <summary>
/// A closed outage segment, with its duration bounds intact.
/// <para>
/// <c>correlationId</c> ties together segments that a pause in monitoring split apart, so a
/// reader can present them as one event without any duration ever spanning the pause.
/// </para>
/// </summary>
public sealed record IncidentPayload(
    int Number,
    Guid CorrelationId,
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
    string? InterfaceAtLastGood,
    string? InterfaceAtFirstBad,
    string? InterfaceAtFirstGood,
    IReadOnlyList<NetworkState> StatesSeen,
    string TechnicalDetail) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.Incident;

    /// <summary>
    /// How far the evidence goes towards this segment being what it was called, as support
    /// and coverage. Both are written; a single number would hide the case this whole model
    /// exists to catch - perfect support over a sliver of the picture.
    /// </summary>
    public ConfidenceScore? Confidence { get; init; }

    /// <summary>
    /// Busiest second of the machine's own traffic during this segment, in bytes per second.
    /// Absent when the counters were never read, which is not the same as a quiet line.
    /// </summary>
    public long? PeakLocalTrafficBytesPerSecond { get; init; }

    public static IncidentPayload From(IncidentRecord incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        return WithConfidence(incident, new IncidentPayload(
            incident.Number,
            incident.CorrelationId,
            incident.StartedAtUtc,
            incident.EndedAtUtc,
            incident.WorstState,
            incident.WorstState.AttributionOf(),
            incident.DurationMin,
            incident.DurationReported,
            incident.DurationMax,
            incident.SampleCount,
            incident.IsOpen,
            incident.EndedByGap,
            incident.StartedAfterGap,
            incident.RouteChanged,
            incident.InterfaceAtLastGood,
            incident.InterfaceAtFirstBad,
            incident.InterfaceAtFirstGood,
            incident.StatesSeen,
            incident.TechnicalDetail));
    }

    private static IncidentPayload WithConfidence(IncidentRecord incident, IncidentPayload payload) =>
        payload with
        {
            Confidence = incident.Confidence,
            PeakLocalTrafficBytesPerSecond = incident.PeakLocalTrafficBytesPerSecond,
        };

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumber("number", Number);
        writer.WriteString("correlationId", CorrelationId);
        writer.WriteString("startedUtc", StartedUtc.ToString("O"));
        writer.WriteString("endedUtc", EndedUtc.ToString("O"));
        writer.WriteString("worstState", WorstState.ToString());
        writer.WriteString("attribution", Attribution.ToString());
        writer.WriteNumber("durationMinMs", Math.Round(DurationMin.TotalMilliseconds, 3));
        writer.WriteNumber("durationMs", Math.Round(DurationReported.TotalMilliseconds, 3));
        writer.WriteNumber("durationMaxMs", Math.Round(DurationMax.TotalMilliseconds, 3));
        writer.WriteNumber("samples", SampleCount);
        writer.WriteBoolean("open", IsOpen);
        writer.WriteBoolean("endedByGap", EndedByGap);
        writer.WriteBoolean("startedAfterGap", StartedAfterGap);
        writer.WriteBoolean("routeChanged", RouteChanged);

        if (InterfaceAtLastGood is not null)
        {
            writer.WriteString("interfaceAtLastGood", InterfaceAtLastGood);
        }

        if (InterfaceAtFirstBad is not null)
        {
            writer.WriteString("interfaceAtFirstBad", InterfaceAtFirstBad);
        }

        if (InterfaceAtFirstGood is not null)
        {
            writer.WriteString("interfaceAtFirstGood", InterfaceAtFirstGood);
        }

        writer.WriteStartArray("statesSeen");
        foreach (var state in StatesSeen)
        {
            writer.WriteStringValue(state.ToString());
        }

        writer.WriteEndArray();

        // Written only when it was measured. Absent means the counters were never read, and
        // that has to stay distinguishable from a line that was quiet.
        if (PeakLocalTrafficBytesPerSecond is { } peakLocal)
        {
            writer.WriteNumber("localBpsPeak", peakLocal);
        }

        if (Confidence is { } confidence)
        {
            writer.WriteNumber("support", confidence.Support);
            writer.WriteNumber("coverage", confidence.Coverage);
            writer.WriteString("confidenceBand", confidence.Band.ToString());

            // The individual signals, so a reader can check the band rather than take it on
            // trust - including the ones that could not be checked, which is the half a
            // bare number would quietly omit.
            writer.WriteStartObject("signals");

            foreach (var item in confidence.Evidence)
            {
                if (item.Outcome != EvidenceOutcome.NotApplicable)
                {
                    writer.WriteString(item.Key, item.Outcome.ToString());
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteString("detail", TechnicalDetail);
    }
}

/// <summary>
/// The environment the session is measuring, and any later change to it.
/// <para>
/// Written once at the start and again whenever something material changes. The fingerprint
/// is what lets a reader check in one line whether a forty-eight hour recording covers one
/// connection or several - and the differences are spelled out in words so the answer does
/// not depend on trusting the hash.
/// </para>
/// </summary>
public sealed record EnvironmentPayload(
    DateTimeOffset AtUtc,
    TimeSpan Monotonic,
    string Fingerprint,
    string InterfaceId,
    string InterfaceName,
    LinkMedium Medium,
    string? MacAddress,
    string? GatewayAddress,
    string? SourceAddress,
    IReadOnlyList<string> DnsServers,
    long? LinkSpeedBitsPerSecond,
    string? Ssid,
    string? Bssid,
    bool MultiplePaths,
    IReadOnlyList<string> Differences) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.EnvironmentChange;

    /// <summary>True for the opening record, which has nothing to differ from.</summary>
    public bool IsBaseline => Differences.Count == 0;

    public static EnvironmentPayload From(
        NetworkEnvironment environment,
        DateTimeOffset atUtc,
        TimeSpan monotonic,
        IReadOnlyList<string>? differences = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return new EnvironmentPayload(
            atUtc,
            monotonic,
            environment.Fingerprint,
            environment.InterfaceId,
            environment.InterfaceName,
            environment.Medium,
            environment.MacAddress,
            environment.GatewayAddress,
            environment.SourceAddresses.FirstOrDefault(),
            environment.DnsServers,
            environment.LinkSpeedBitsPerSecond,
            environment.Ssid,
            environment.Bssid,
            environment.VirtualAdapterPresent,
            differences ?? []);
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("atUtc", AtUtc.ToString("O"));
        writer.WriteNumber("mono", Math.Round(Monotonic.TotalMilliseconds, 3));
        writer.WriteString("fingerprint", Fingerprint);
        writer.WriteString("iface", InterfaceId);
        writer.WriteString("ifaceName", InterfaceName);
        writer.WriteString("medium", Medium.ToString());
        writer.WriteString("mac", MacAddress);
        writer.WriteString("gateway", GatewayAddress);
        writer.WriteString("src", SourceAddress);
        SessionStartPayload.WriteNullableNumber(writer, "linkSpeedBps", LinkSpeedBitsPerSecond);
        writer.WriteString("ssid", Ssid);
        writer.WriteString("bssid", Bssid);
        writer.WriteBoolean("multiPath", MultiplePaths);

        writer.WriteStartArray("dns");
        foreach (var server in DnsServers)
        {
            writer.WriteStringValue(server);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("changes");
        foreach (var difference in Differences)
        {
            writer.WriteStringValue(difference);
        }

        writer.WriteEndArray();
    }
}

/// <summary>A stretch where nothing was observed. Recorded so it can never pass as uptime.</summary>
public sealed record GapPayload(DateTimeOffset DetectedUtc, TimeSpan Duration, GapCause Cause) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.Gap;

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("detectedUtc", DetectedUtc.ToString("O"));
        writer.WriteNumber("durationMs", Math.Round(Duration.TotalMilliseconds, 3));
        writer.WriteString("cause", Cause.ToString());
    }
}

/// <summary>
/// The wall clock was corrected or the machine rebooted. Recorded openly, because an
/// unexplained jump in the timestamps is exactly what an operator would seize on.
/// </summary>
public sealed record ClockAnomalyPayload(
    DateTimeOffset ObservedUtc,
    ClockAnomaly Anomaly,
    TimeSpan Skew,
    TimeSpan MonotonicDelta) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.ClockAnomaly;

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("observedUtc", ObservedUtc.ToString("O"));
        writer.WriteString("anomaly", Anomaly.ToString());
        writer.WriteNumber("skewMs", Math.Round(Skew.TotalMilliseconds, 3));
        writer.WriteNumber("monotonicDeltaMs", Math.Round(MonotonicDelta.TotalMilliseconds, 3));
    }
}

/// <summary>
/// Periodic snapshot of the running totals.
/// <para>
/// Exists so a session interrupted by a crash or a service restart can be picked up
/// without re-deriving its history. Re-deriving from the index would be approximate -
/// the index does not record how each interval was attributed - and approximation is
/// exactly what a figure in a complaint cannot be built on. Resuming instead reads the
/// last checkpoint and replays only the entries after it.
/// </para>
/// <para>
/// A useful side effect: the totals are themselves inside the tamper-evident chain, so
/// they are periodically anchored rather than only asserted at the end.
/// </para>
/// </summary>
/// <param name="SessionElapsed">Time since the session began, across every run of it.</param>
public sealed record CheckpointPayload(
    DateTimeOffset AtUtc,
    TimeSpan SessionElapsed,
    long LastSampleSequence,
    TimeSpan MonitoredTime,
    TimeSpan GapTime,
    TimeSpan DegradedTime,
    TimeSpan UpstreamDowntime,
    TimeSpan LocalDowntime,
    int IncidentCount,
    int UpstreamIncidentCount,
    TimeSpan LongestUpstreamOutage) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.Checkpoint;

    public static CheckpointPayload From(
        SessionStatistics statistics,
        DateTimeOffset atUtc,
        TimeSpan sessionElapsed,
        long lastSampleSequence)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        return new CheckpointPayload(
            atUtc,
            sessionElapsed,
            lastSampleSequence,
            statistics.MonitoredTime,
            statistics.GapTime,
            statistics.DegradedTime,
            statistics.UpstreamDowntime,
            statistics.LocalDowntime,
            statistics.IncidentCount,
            statistics.UpstreamIncidentCount,
            statistics.LongestUpstreamOutage);
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("atUtc", AtUtc.ToString("O"));
        writer.WriteNumber("elapsedMs", Math.Round(SessionElapsed.TotalMilliseconds, 3));
        writer.WriteNumber("lastSample", LastSampleSequence);
        writer.WriteNumber("monitoredMs", Math.Round(MonitoredTime.TotalMilliseconds, 3));
        writer.WriteNumber("gapMs", Math.Round(GapTime.TotalMilliseconds, 3));
        writer.WriteNumber("degradedMs", Math.Round(DegradedTime.TotalMilliseconds, 3));
        writer.WriteNumber("upstreamDowntimeMs", Math.Round(UpstreamDowntime.TotalMilliseconds, 3));
        writer.WriteNumber("localDowntimeMs", Math.Round(LocalDowntime.TotalMilliseconds, 3));
        writer.WriteNumber("incidents", IncidentCount);
        writer.WriteNumber("upstreamIncidents", UpstreamIncidentCount);
        writer.WriteNumber("longestUpstreamMs", Math.Round(LongestUpstreamOutage.TotalMilliseconds, 3));
    }
}

/// <summary>
/// A path trace taken at an incident boundary.
/// <para>
/// The one piece of evidence that says where a connection stops rather than merely that
/// it stopped. A trace that dies at the router points at the customer's own equipment; one
/// that dies at the operator's first hop points at them. Recorded in full, hop by hop, so
/// a reader can check the conclusion instead of taking it on trust.
/// </para>
/// </summary>
public sealed record TracePayload(
    int IncidentNumber,
    string Phase,
    DateTimeOffset TakenUtc,
    string Target,
    bool ReachedTarget,
    int PrivateHopCount,
    string? FirstPublicHop,
    int? LastAnsweringTtl,
    bool StopsInsideHomeNetwork,
    IReadOnlyList<TraceHop> Hops) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.Trace;

    public static TracePayload From(IncidentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var result = trace.Result;

        return new TracePayload(
            trace.IncidentNumber,
            trace.Phase.ToString(),
            trace.TakenUtc,
            result.Target,
            result.ReachedTarget,
            result.PrivateHopCount,
            result.FirstPublicHop?.Address,
            result.LastAnsweringTtl,
            result.StopsInsideHomeNetwork,
            result.Hops);
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumber("incident", IncidentNumber);
        writer.WriteString("phase", Phase);
        writer.WriteString("takenUtc", TakenUtc.ToString("O"));
        writer.WriteString("target", Target);
        writer.WriteBoolean("reached", ReachedTarget);
        writer.WriteNumber("privateHops", PrivateHopCount);
        writer.WriteString("firstPublicHop", FirstPublicHop);
        SessionStartPayload.WriteNullableNumber(writer, "lastAnsweringTtl", LastAnsweringTtl);
        writer.WriteBoolean("stopsAtHome", StopsInsideHomeNetwork);

        writer.WriteStartArray("hops");

        foreach (var hop in Hops)
        {
            writer.WriteStartObject();
            writer.WriteNumber("ttl", hop.Ttl);
            writer.WriteString("address", hop.Address);
            SessionStartPayload.WriteNullableMilliseconds(writer, "rttMs", hop.RoundTrip);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

/// <summary>Closes a session with the totals as computed at the time.</summary>
public sealed record SessionEndPayload(
    DateTimeOffset EndedUtc,
    TimeSpan MonitoredTime,
    TimeSpan GapTime,
    TimeSpan UpstreamDowntime,
    TimeSpan LocalDowntime,
    double AvailabilityPercent,
    double UpstreamAvailabilityPercent,
    int IncidentCount,
    int UpstreamIncidentCount) : IEvidencePayload
{
    public EvidenceKind Kind => EvidenceKind.SessionEnd;

    public static SessionEndPayload From(SessionStatistics statistics, DateTimeOffset endedUtc)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        return new SessionEndPayload(
            endedUtc,
            statistics.MonitoredTime,
            statistics.GapTime,
            statistics.UpstreamDowntime,
            statistics.LocalDowntime,
            statistics.AvailabilityPercent,
            statistics.UpstreamAvailabilityPercent,
            // Whole-session counts, so a resumed session reports its full history rather
            // than only what the final run happened to see.
            statistics.IncidentCount,
            statistics.UpstreamIncidentCount);
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("endedUtc", EndedUtc.ToString("O"));
        writer.WriteNumber("monitoredMs", Math.Round(MonitoredTime.TotalMilliseconds, 3));
        writer.WriteNumber("gapMs", Math.Round(GapTime.TotalMilliseconds, 3));
        writer.WriteNumber("upstreamDowntimeMs", Math.Round(UpstreamDowntime.TotalMilliseconds, 3));
        writer.WriteNumber("localDowntimeMs", Math.Round(LocalDowntime.TotalMilliseconds, 3));
        writer.WriteNumber("availability", Math.Round(AvailabilityPercent, 6));
        writer.WriteNumber("upstreamAvailability", Math.Round(UpstreamAvailabilityPercent, 6));
        writer.WriteNumber("incidents", IncidentCount);
        writer.WriteNumber("upstreamIncidents", UpstreamIncidentCount);
    }
}
