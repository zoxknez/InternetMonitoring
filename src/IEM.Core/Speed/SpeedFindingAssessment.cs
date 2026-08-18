namespace IEM.Core.Speed;

/// <summary>What can be said today about a recorded speed measurement.</summary>
public enum SpeedAssessmentState
{
    /// <summary>
    /// Cannot be settled under the current rules. Not the same as "fails them": a finding
    /// recorded before the rules were corrected carries an assessment made under the old
    /// ones, and neither adopting it nor inverting it would be honest.
    /// </summary>
    Undetermined,

    MeetsConditions,

    DoesNotMeetConditions,
}

/// <summary>
/// A recorded measurement read under today's rules.
/// <para>
/// The file beside a session stores conclusions as well as numbers - a band label as text, a
/// boolean saying the measurement could support a complaint. Those were computed by whichever
/// build wrote them, and 2.7 changed both: a path nobody had checked used to count as
/// verified, and the bands used to carry the regulator's terms for a criterion a single
/// measurement cannot meet. Reading those fields back and printing them would let the old
/// rules speak through the new presentation - and it did, until this type existed.
/// </para>
/// <para>
/// LEGACY_DERIVED_CONCLUSION_IS_NEVER_TRUSTED_AS_RAW_EVIDENCE. What a previous build measured
/// is evidence; what it concluded is not. The measured numbers are taken as written and every
/// conclusion is derived again.
/// </para>
/// </summary>
public sealed record SpeedFindingAssessment
{
    public required SpeedAssessmentState State { get; init; }

    /// <summary>Where the receiving figure falls, derived here rather than read from the file.</summary>
    public string? BandLabel { get; init; }

    public string? UploadBandLabel { get; init; }

    /// <summary>
    /// What the build that wrote the file concluded, kept so the history is not lost - and
    /// never presented as a current finding.
    /// </summary>
    public bool? RecordedAssessment { get; init; }

    /// <summary>Why the current rules cannot settle it, when they cannot.</summary>
    public string? Reason { get; init; }

    public IReadOnlyList<string> Defects { get; init; } = [];

    public bool IsLegacy => RecordedAssessment is not null;
}
