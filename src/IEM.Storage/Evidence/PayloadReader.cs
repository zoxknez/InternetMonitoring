using System.Globalization;
using System.Text.Json;
using IEM.Core;
using IEM.Core.Classification;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Storage.Evidence;

/// <summary>
/// Reads payloads back out of the raw chain.
/// <para>
/// The mirror image of <see cref="IEvidencePayload.WriteTo"/>, and the reason the index can
/// be thrown away and rebuilt: whatever the database holds, the chain holds it too, in a
/// form that survives the database being deleted, corrupted, or quietly edited.
/// </para>
/// <para>
/// Missing fields fall back to a neutral value rather than throwing. A log written by an
/// older build is still evidence, and refusing to read it because it lacks a field added
/// later would destroy exactly what the format was designed to preserve.
/// </para>
/// </summary>
public static class PayloadReader
{
    public static SessionStartPayload? SessionStart(JsonElement p) =>
        Text(p, "sessionId") is { } sessionId
            ? new SessionStartPayload(
                sessionId,
                Text(p, "toolVersion") ?? "?",
                Date(p, "startedUtc") ?? default,
                Duration(p, "plannedDuration"),
                Text(p, "machine") ?? "?",
                Text(p, "interface") ?? "?",
                Enum(p, "medium", LinkMedium.Unknown),
                Integer(p, "linkSpeedBps"),
                Text(p, "gateway"),
                Text(p, "interfaceId"))
            {
                // What this session was actually recorded under, so a report rebuilt by a
                // newer build states the reasoning that produced the conclusions rather than
                // whichever one happens to be current.
                SchemaVersion = (int)(Integer(p, "schemaVersion") ?? EvidenceModelVersion.LegacySchemaVersion),
                ClassifierVersion = Text(p, "classifierVersion") ?? EvidenceModelVersion.ClassifierVersion,
                AttributionModelVersion =
                    Text(p, "attributionModelVersion") ?? EvidenceModelVersion.AttributionModelVersion,
                ConfidenceModelVersion =
                    Text(p, "confidenceModelVersion") ?? EvidenceModelVersion.ConfidenceModelVersion,
            }
            : null;

    public static SamplePayload? Sample(JsonElement p) =>
        Date(p, "utc") is { } wall && Milliseconds(p, "mono") is { } monotonic
            ? new SamplePayload(
                Integer(p, "n") ?? 0,
                wall,
                monotonic,
                Enum(p, "state", NetworkState.Ok),
                Enum(p, "severity", Severity.Ok),
                Text(p, "detail") ?? string.Empty,
                Text(p, "phase") ?? "Stable",
                Enum(p, "link", LinkStatus.Missing),
                Milliseconds(p, "rttMs"),
                Tally(p, "gw"),
                Tally(p, "icmp"),
                Tally(p, "tcp"),
                Tally(p, "tls"),
                Tally(p, "dnsIsp"),
                Tally(p, "dnsPub"),
                Tally(p, "dnsSys"),
                Tally(p, "http"),
                Flag(p, "overran"),
                (int?)Integer(p, "signal"),
                Text(p, "bssid"))
            {
                InterfaceId = Text(p, "iface"),
                SourceAddress = Text(p, "src"),
                BoundProbes = (int)(Integer(p, "bound") ?? 0),
                MultiplePaths = Flag(p, "multiPath"),

                // Absent in anything written before schema 3, and absent afterwards
                // whenever the counters could not be read. Both mean "not known", which is
                // why this is nullable rather than defaulted to zero.
                LocalTrafficBytesPerSecond = Integer(p, "localBps"),
            }
            : null;

    public static IncidentPayload? Incident(JsonElement p) =>
        Date(p, "startedUtc") is { } started && Date(p, "endedUtc") is { } ended
            ? new IncidentPayload(
                (int)(Integer(p, "number") ?? 0),
                Text(p, "correlationId") is { } id && Guid.TryParse(id, out var correlation)
                    ? correlation
                    : Guid.Empty,
                started,
                ended,
                Enum(p, "worstState", NetworkState.InternetDown),
                Enum(p, "attribution", FaultAttribution.Undetermined),
                Milliseconds(p, "durationMinMs") ?? TimeSpan.Zero,
                Milliseconds(p, "durationMs") ?? TimeSpan.Zero,
                Milliseconds(p, "durationMaxMs") ?? TimeSpan.Zero,
                (int)(Integer(p, "samples") ?? 0),
                Flag(p, "open"),
                Flag(p, "endedByGap"),
                Flag(p, "startedAfterGap"),
                Flag(p, "routeChanged"),
                Text(p, "interfaceAtLastGood"),
                Text(p, "interfaceAtFirstBad"),
                Text(p, "interfaceAtFirstGood"),
                States(p, "statesSeen"),
                Text(p, "detail") ?? string.Empty)
            {
                PeakLocalTrafficBytesPerSecond = Integer(p, "localBpsPeak"),

                // Both or neither. A support figure without coverage is exactly the
                // half-a-picture reading this model exists to stop.
                Confidence = Integer(p, "support") is { } support && Integer(p, "coverage") is { } coverage
                    ? new ConfidenceScore((int)support, (int)coverage, [])
                    : null,
            }
            : null;

    /// <summary>
    /// The environment as recorded, so a session picked up after a restart is still compared
    /// against the connection it started on.
    /// <para>
    /// Without this the comparison restarts from whatever is present after the interruption
    /// - and a router swapped out during that interruption, which is exactly when routers
    /// get swapped, would pass unremarked.
    /// </para>
    /// </summary>
    public static NetworkEnvironment? Environment(JsonElement p) =>
        Text(p, "iface") is { } interfaceId
            ? new NetworkEnvironment
            {
                InterfaceId = interfaceId,
                InterfaceName = Text(p, "ifaceName") ?? interfaceId,
                Medium = Enum(p, "medium", LinkMedium.Unknown),
                MacAddress = Text(p, "mac"),
                GatewayAddress = Text(p, "gateway"),
                SourceAddresses = Text(p, "src") is { } source ? [source] : [],
                DnsServers = Strings(p, "dns"),
                LinkSpeedBitsPerSecond = Integer(p, "linkSpeedBps"),
                Ssid = Text(p, "ssid"),
                Bssid = Text(p, "bssid"),
                VirtualAdapterPresent = Flag(p, "multiPath"),
            }
            : null;

    /// <summary>
    /// A path trace, hop by hop.
    /// <para>
    /// Read back in full rather than summarised, because the whole value of a trace is that
    /// a reader can check the conclusion against the hops instead of taking it on trust.
    /// </para>
    /// </summary>
    public static TracePayload? Trace(JsonElement p) =>
        Text(p, "target") is { } target && Date(p, "takenUtc") is { } taken
            ? new TracePayload(
                (int)(Integer(p, "incident") ?? 0),
                Text(p, "phase") ?? "DuringOutage",
                taken,
                target,
                Flag(p, "reached"),
                (int)(Integer(p, "privateHops") ?? 0),
                Text(p, "firstPublicHop"),
                (int?)Integer(p, "lastAnsweringTtl"),
                Flag(p, "stopsAtHome"),
                Hops(p))
            : null;

    private static IReadOnlyList<TraceHop> Hops(JsonElement p)
    {
        if (!p.TryGetProperty("hops", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hops = new List<TraceHop>(array.GetArrayLength());

        foreach (var item in array.EnumerateArray())
        {
            // TryGetInt32 throws on JSON null rather than returning false.
            if (item.TryGetProperty("ttl", out var ttl) &&
                ttl.ValueKind == JsonValueKind.Number &&
                ttl.TryGetInt32(out var hopTtl))
            {
                hops.Add(new TraceHop(hopTtl, Text(item, "address"), Milliseconds(item, "rttMs")));
            }
        }

        return hops;
    }

    public static GapPayload? Gap(JsonElement p) =>
        Date(p, "detectedUtc") is { } detected
            ? new GapPayload(detected, Milliseconds(p, "durationMs") ?? TimeSpan.Zero, Enum(p, "cause", GapCause.Unknown))
            : null;

    /// <summary>
    /// The running totals a session writes down as it goes.
    /// <para>
    /// Read for one reason: a session that has not been closed has no closing entry, and
    /// every total in its summary row is therefore still a column default. The checkpoint
    /// is the same arithmetic the closing entry would carry, written while the session runs.
    /// </para>
    /// </summary>
    public static CheckpointPayload? Checkpoint(JsonElement p) =>
        Date(p, "atUtc") is { } at
            ? new CheckpointPayload(
                at,
                Milliseconds(p, "elapsedMs") ?? TimeSpan.Zero,
                Integer(p, "lastSample") ?? 0,
                Milliseconds(p, "monitoredMs") ?? TimeSpan.Zero,
                Milliseconds(p, "gapMs") ?? TimeSpan.Zero,
                Milliseconds(p, "degradedMs") ?? TimeSpan.Zero,
                Milliseconds(p, "upstreamDowntimeMs") ?? TimeSpan.Zero,
                Milliseconds(p, "localDowntimeMs") ?? TimeSpan.Zero,
                (int)(Integer(p, "incidents") ?? 0),
                (int)(Integer(p, "upstreamIncidents") ?? 0),
                Milliseconds(p, "longestUpstreamMs") ?? TimeSpan.Zero)
            : null;

    public static SessionEndPayload? SessionEnd(JsonElement p) =>
        Date(p, "endedUtc") is { } ended
            ? new SessionEndPayload(
                ended,
                Milliseconds(p, "monitoredMs") ?? TimeSpan.Zero,
                Milliseconds(p, "gapMs") ?? TimeSpan.Zero,
                Milliseconds(p, "upstreamDowntimeMs") ?? TimeSpan.Zero,
                Milliseconds(p, "localDowntimeMs") ?? TimeSpan.Zero,
                Number(p, "availability") ?? 100d,
                Number(p, "upstreamAvailability") ?? 100d,
                (int)(Integer(p, "incidents") ?? 0),
                (int)(Integer(p, "upstreamIncidents") ?? 0))
            : null;

    // ---- Field readers -------------------------------------------------------

    private static string? Text(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>
    /// Nullable integers are written as JSON null (<see cref="SessionStartPayload.WriteNullableNumber"/>).
    /// <c>TryGetInt64</c> throws on Null instead of returning false, so the kind is checked first.
    /// A traceroute that nobody answered writes <c>lastAnsweringTtl: null</c>; treating that as
    /// a parse failure used to abort the whole report.
    /// </summary>
    private static long? Integer(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static bool Flag(JsonElement p, string name) =>
        p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static TimeSpan? Milliseconds(JsonElement p, string name) =>
        Number(p, name) is { } value ? TimeSpan.FromMilliseconds(value) : null;

    private static DateTimeOffset? Date(JsonElement p, string name) =>
        Text(p, name) is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static TimeSpan Duration(JsonElement p, string name) =>
        Text(p, name) switch
        {
            "infinite" => Timeout.InfiniteTimeSpan,
            { } text when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => Timeout.InfiniteTimeSpan,
        };

    private static T Enum<T>(JsonElement p, string name, T fallback) where T : struct, Enum =>
        Text(p, name) is { } text && System.Enum.TryParse<T>(text, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Tallies are written as <c>"2/3"</c>, or null when the family was never attempted.
    /// The distinction matters: a silent family must never read back as one that was tried
    /// and failed.
    /// </summary>
    private static ProbeTally Tally(JsonElement p, string name)
    {
        if (Text(p, name) is not { } text)
        {
            return default;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 ||
            !int.TryParse(text.AsSpan(0, slash), CultureInfo.InvariantCulture, out var succeeded) ||
            !int.TryParse(text.AsSpan(slash + 1), CultureInfo.InvariantCulture, out var attempted))
        {
            return default;
        }

        // Freshness is not carried in the log line, so the count of fresh successes is taken
        // as the count of successes. The tally was already computed from fresh results when
        // it was written; this is a read of a decision, not a chance to make it again.
        return new ProbeTally(attempted, succeeded, succeeded);
    }

    private static IReadOnlyList<string> Strings(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>(array.GetArrayLength());

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static IReadOnlyList<NetworkState> States(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var states = new List<NetworkState>(array.GetArrayLength());

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                System.Enum.TryParse<NetworkState>(item.GetString(), out var state))
            {
                states.Add(state);
            }
        }

        return states;
    }
}
