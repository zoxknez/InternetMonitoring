using IEM.Core.Model;

namespace IEM.Core.Classification;

public enum EvidenceOutcome
{
    /// <summary>Checked, and it backs the conclusion.</summary>
    Supports,

    /// <summary>Checked, and it argues against the conclusion.</summary>
    Contradicts,

    /// <summary>Could not be checked. Shown explicitly rather than quietly assumed either way.</summary>
    Unavailable,

    /// <summary>Not part of proving this particular conclusion, so it is not counted at all.</summary>
    NotApplicable,
}

/// <param name="Key">Stable identifier; the Serbian label is rendered from this at presentation time.</param>
/// <param name="Weight">How much this signal counts relative to the others.</param>
public sealed record EvidenceItem(string Key, EvidenceOutcome Outcome, int Weight)
{
    public string Marker => Outcome switch
    {
        EvidenceOutcome.Supports => "✓",
        EvidenceOutcome.Contradicts => "✗",
        EvidenceOutcome.NotApplicable => "-",
        _ => "!",
    };
}

public enum ConfidenceBand
{
    VeryLow,
    Low,
    Moderate,
    High,
    VeryHigh,
}

/// <summary>
/// How far the evidence goes towards a particular conclusion.
/// <para>
/// Two numbers, not one. <b>Support</b> is how much of what was checked backs the
/// conclusion; <b>coverage</b> is how much of what mattered could be checked at all.
/// Collapsing them was the flaw in the previous model: two signals out of nineteen, both
/// supporting, produced a hundred out of a hundred and the label VERY HIGH. That is exactly
/// how a report loses an argument - an operator only has to point out that seventeen checks
/// never ran.
/// </para>
/// <para>
/// The band is what a reader sees. Ninety-four percent looks like a probability and is not
/// one; it is a weighted sum of heuristics. The number stays for sorting and comparison, out
/// of sight.
/// </para>
/// </summary>
/// <param name="Support">Share of checked weight that backs the conclusion, 0-100.</param>
/// <param name="Coverage">Share of relevant weight that could be checked, 0-100.</param>
public sealed record ConfidenceScore(int Support, int Coverage, IReadOnlyList<EvidenceItem> Evidence)
{
    /// <summary>
    /// Combined 0-100 figure, kept internal to the model for ranking one incident against
    /// another. Never shown as a percentage: see the class remarks.
    /// </summary>
    public int Value => (int)Math.Round(Support * (Coverage / 100d), MidpointRounding.AwayFromZero);

    /// <summary>
    /// The band a reader is shown.
    /// <para>
    /// Coverage caps it. However convincing the checks that ran, a conclusion drawn from a
    /// tenth of the picture is not a strong conclusion, and saying otherwise is the specific
    /// failure this type exists to prevent.
    /// </para>
    /// </summary>
    public ConfidenceBand Band
    {
        get
        {
            var fromSupport = Support switch
            {
                >= 90 => ConfidenceBand.VeryHigh,
                >= 75 => ConfidenceBand.High,
                >= 50 => ConfidenceBand.Moderate,
                >= 25 => ConfidenceBand.Low,
                _ => ConfidenceBand.VeryLow,
            };

            var ceiling = Coverage switch
            {
                >= 80 => ConfidenceBand.VeryHigh,
                >= 60 => ConfidenceBand.High,
                >= 40 => ConfidenceBand.Moderate,
                >= 20 => ConfidenceBand.Low,
                _ => ConfidenceBand.VeryLow,
            };

            return fromSupport < ceiling ? fromSupport : ceiling;
        }
    }

    public IEnumerable<EvidenceItem> Supporting => Evidence.Where(e => e.Outcome == EvidenceOutcome.Supports);

    public IEnumerable<EvidenceItem> Contradicting => Evidence.Where(e => e.Outcome == EvidenceOutcome.Contradicts);

    /// <summary>Signals that mattered but could not be checked. Their absence caps the band.</summary>
    public IEnumerable<EvidenceItem> Missing => Evidence.Where(e => e.Outcome == EvidenceOutcome.Unavailable);

    /// <summary>Nothing relevant could be checked, so no conclusion is supported at all.</summary>
    public static ConfidenceScore None { get; } = new(0, 0, []);
}
