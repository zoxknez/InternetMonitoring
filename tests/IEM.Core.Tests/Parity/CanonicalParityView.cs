using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IEM.Core.Classification;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Evidence.Canonicalization;

namespace IEM.Core.Tests.Parity;

/// <summary>One tally reduced to what parity compares: a skipped probe is not an attempt.</summary>
public sealed record TallyView(int Attempted, int Succeeded);

public sealed record TalliesView(TallyView Gateway, TallyView ExternalIcmp, TallyView ExternalTcp);

/// <summary>
/// The meaning-bearing slice of one classified sample (roadmap 25.7). No <c>TechnicalDetail</c>
/// string, no ifindex, no native status text - <see cref="NetworkState"/> is the key.
/// </summary>
public sealed record CanonicalSampleView(
    long Seq,
    string NetworkState,
    bool IsOutage,
    bool AnyExternalReachability,
    TalliesView Tallies,
    bool PathProvesLink);

public sealed record CanonicalIncidentMonotonicView(
    long FirstBadMs,
    long LastBadMs,
    long? FirstGoodMs);

public sealed record CanonicalIncidentView(
    int Index,
    string WorstState,
    CanonicalIncidentMonotonicView Monotonic,
    bool EndedByGap,
    bool RouteChanged);

public sealed record CanonicalClaimsView(
    bool SupportsComplaint,
    bool NamesOperatorAsFault,
    bool WifiRadioBlamed);

/// <summary>
/// The comparable form of a Core run. Two projections of the same fixture (Windows, Linux)
/// must produce the same <see cref="Sha256"/> after RFC 8785 canonicalization for a symmetric
/// fixture (invariant 260); an asymmetric one differs only where <c>allowedDivergences</c> says.
/// </summary>
public sealed record CanonicalParityView(
    IReadOnlyList<CanonicalSampleView> Samples,
    IReadOnlyList<CanonicalIncidentView> Incidents,
    string SessionVerdictKind,
    CanonicalClaimsView Claims)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string CanonicalJson =>
        Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(
            JsonSerializer.Serialize(new { Samples }, SerializerOptions)));

    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)));

    public static CanonicalParityView From(
        IReadOnlyList<ProbeCycle> cycles,
        StateClassifier? classifier = null,
        TimeSpan? monitoredTime = null)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        classifier ??= new StateClassifier();

        var samples = new List<CanonicalSampleView>(cycles.Count);
        var incidents = new List<IncidentRecord>();
        var detector = new IncidentDetector();
        foreach (var cycle in cycles)
        {
            var verdict = classifier.Classify(cycle);
            samples.Add(new CanonicalSampleView(
                cycle.Sequence,
                verdict.State.ToString(),
                verdict.IsOutage,
                cycle.AnyExternalReachability,
                new TalliesView(
                    ToView(cycle.Gateway),
                    ToView(cycle.ExternalIcmp),
                    ToView(cycle.ExternalTcp)),
                cycle.AgreedInterfaceId is not null));

            var instant = new SampleInstant(TimeSpan.FromTicks(cycle.MonotonicTicks), cycle.WallUtc);
            var closed = detector.Observe(instant, verdict, cycle.AgreedInterfaceId);
            if (closed is not null)
            {
                incidents.Add(closed);
            }
        }

        var open = detector.CloseOpenIncident();
        if (open is not null)
        {
            incidents.Add(open);
        }

        var upstreamCount = incidents.Count(incident => incident.IsUpstream);
        var localIncidents = incidents.Where(incident => !incident.IsUpstream).ToArray();
        var localDowntime = TimeSpan.FromTicks(localIncidents.Sum(incident => incident.DurationReported.Ticks));

        // A single failing boundary sample proves occurrence but has a zero-duration lower
        // bound. SessionVerdict's TimeSpan input also carries the boolean "did a local event
        // occur", so retain that fact with the smallest representable positive duration.
        if (localIncidents.Length > 0 && localDowntime == TimeSpan.Zero)
        {
            localDowntime = TimeSpan.FromTicks(1);
        }

        var observedDuration = monitoredTime ?? DeriveObservedDuration(cycles);
        var sessionVerdict = SessionVerdict.Evaluate(observedDuration, upstreamCount, localDowntime);
        var incidentViews = incidents.Select(incident => new CanonicalIncidentView(
            incident.Number,
            incident.WorstState.ToString(),
            new CanonicalIncidentMonotonicView(
                ToMilliseconds(incident.FirstBad.Monotonic),
                ToMilliseconds(incident.LastBad.Monotonic),
                incident.FirstGood is { } firstGood ? ToMilliseconds(firstGood.Monotonic) : null),
            incident.EndedByGap,
            incident.RouteChanged)).ToArray();

        return new CanonicalParityView(
            samples,
            incidentViews,
            sessionVerdict.Kind.ToString(),
            new CanonicalClaimsView(
                sessionVerdict.SupportsComplaint,
                NamesOperatorAsFault: false,
                WifiRadioBlamed: samples.Any(sample => sample.NetworkState == NetworkState.WifiRadioDown.ToString())));
    }

    private static TallyView ToView(ProbeTally tally) => new(tally.Attempted, tally.Succeeded);

    private static TimeSpan DeriveObservedDuration(IReadOnlyList<ProbeCycle> cycles) => cycles.Count < 2
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Max(0, cycles[^1].MonotonicTicks - cycles[0].MonotonicTicks));

    private static long ToMilliseconds(TimeSpan value) => checked((long)value.TotalMilliseconds);
}
