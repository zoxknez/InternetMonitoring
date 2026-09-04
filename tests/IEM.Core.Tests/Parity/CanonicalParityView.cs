using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IEM.Core.Classification;
using IEM.Core.Model;
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

/// <summary>
/// The comparable form of a Core run. Two projections of the same fixture (Windows, Linux)
/// must produce the same <see cref="Sha256"/> after RFC 8785 canonicalization for a symmetric
/// fixture (invariant 260); an asymmetric one differs only where <c>allowedDivergences</c> says.
/// </summary>
public sealed record CanonicalParityView(IReadOnlyList<CanonicalSampleView> Samples)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string CanonicalJson =>
        Encoding.UTF8.GetString(JsonCanonicalizer.Canonicalize(
            JsonSerializer.Serialize(new { Samples }, SerializerOptions)));

    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)));

    public static CanonicalParityView From(IReadOnlyList<ProbeCycle> cycles, StateClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        classifier ??= new StateClassifier();

        var samples = new List<CanonicalSampleView>(cycles.Count);
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
        }

        return new CanonicalParityView(samples);
    }

    private static TallyView ToView(ProbeTally tally) => new(tally.Attempted, tally.Succeeded);
}
