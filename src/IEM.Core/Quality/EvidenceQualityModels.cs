namespace IEM.Core.Quality;

public enum QualityPurpose
{
    GeneralMeasurement,
    TargetReachability,
    LossMeasurement,
    DelayMeasurement,
    PathAttribution,
    GatewayBehavior,
    OutageDuration,
    PackageEvidence,
}

/// <summary>
/// Scoped target/claim subject for which evidence quality is evaluated.
/// Invariant 115: EVIDENCE_QUALITY_IS_SCOPED_TO_THE_CLAIM_OR_ASSESSMENT_PURPOSE.
/// </summary>
public sealed record EvidenceQualitySubject(
    string SubjectType,
    string SubjectRefId,
    QualityPurpose Purpose,
    string? IntervalRef = null,
    string? TargetRef = null,
    string? ClaimRef = null);

public enum QualityEligibility
{
    Full,
    Reduced,
    Ineligible,
    Unknown,
    NotObservable,
    NotApplicable,
}

public enum EvidenceQualityBand
{
    Strong,
    Moderate,
    Limited,
    Insufficient,
}

public enum QualityDimensionState
{
    Satisfied,
    Degraded,
    Weak,
    Unavailable,
    Unknown,
}

public enum QualityDimensionType
{
    AcquisitionQuality,
    ProbeExecutionQuality,
    TargetQuality,
    PathQuality,
    TemporalQuality,
    CoverageQuality,
    PackageIntegrityContext,
}

/// <summary>
/// Status and evidence accounting for a specific quality dimension.
/// </summary>
public sealed record EvidenceQualityDimension(
    QualityDimensionType Dimension,
    QualityDimensionState State,
    int EligibleEvidenceCount,
    int ReducedEvidenceCount,
    int IneligibleEvidenceCount,
    int UnknownEvidenceCount,
    IReadOnlyList<string> SourceEvidenceRefs,
    IReadOnlyList<string> ReasonCodes);

/// <summary>
/// Quality interval segment on the session timeline.
/// Invariant 116: CURRENT_HEALTH_STATE_NEVER_REWEIGHTS_PRIOR_QUALITY_INTERVALS.
/// </summary>
public sealed record EvidenceQualityInterval(
    string IntervalId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string BootInstanceId,
    IReadOnlyList<string> SourceStateRefs,
    string InterpretationRefId);

/// <summary>
/// Explicit decision on how an interval or attempt contributes to the quality assessment.
/// Invariants:
/// 117. INELIGIBLE_EVIDENCE_NEVER_REENTERS_QUALITY_AGGREGATION
/// 120. REDUCED_OR_EXCLUDED_EVIDENCE_IS_ALWAYS_VISIBLE_AND_REASONED
/// </summary>
public sealed record EvidenceContributionDecision(
    string DecisionId,
    string SubjectRef,
    string IntervalRef,
    QualityEligibility Eligibility,
    int ContributionBasisPoints, // 0 - 10000
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> SourceEvidenceRefs,
    string PolicyRefId,
    string InterpretationRefId);

/// <summary>
/// Explicit accounting of assessable active monitoring time vs non-observable time.
/// Invariant 119: NON_OBSERVABLE_TIME_IS_NEVER_TREATED_AS_NEGATIVE_NETWORK_EVIDENCE.
/// Invariant 122: QUALITY_COVERAGE_DENOMINATOR_IS_ALWAYS_EXPLICIT.
/// </summary>
public sealed record EvidenceCoverage(
    TimeSpan ObservationWindow,
    TimeSpan NonObservableDuration,
    TimeSpan AssessableDuration,
    TimeSpan FullEligibleDuration,
    TimeSpan ReducedEligibleDuration,
    TimeSpan IneligibleDuration,
    TimeSpan UnknownDuration,
    string DenominatorDefinition)
{
    public double FullEligibleRatio => AssessableDuration.TotalSeconds > 0
        ? FullEligibleDuration.TotalSeconds / AssessableDuration.TotalSeconds
        : 0.0;

    public double ReducedEligibleRatio => AssessableDuration.TotalSeconds > 0
        ? ReducedEligibleDuration.TotalSeconds / AssessableDuration.TotalSeconds
        : 0.0;

    public double TotalValidCoverageRatio => AssessableDuration.TotalSeconds > 0
        ? (FullEligibleDuration + ReducedEligibleDuration).TotalSeconds / AssessableDuration.TotalSeconds
        : 0.0;
}

public enum QualityAssessmentMaturity
{
    Provisional,
    Finalized,
}

/// <summary>
/// Immutable claim-scoped evidence quality assessment (ASSESSMENT).
/// Invariants:
/// 114. EVIDENCE_QUALITY_IS_ASSESSMENT_NOT_FACT
/// 121. CRITICAL_QUALITY_FAILURE_CANNOT_BE_AVERAGED_AWAY
/// 123. PACKAGE_INTEGRITY_NEVER_PROVES_MEASUREMENT_TRUTH
/// 124. INVALID_PACKAGE_INTEGRITY_CANNOT_BE_AVERAGED_AWAY_BY_STRONG_MEASUREMENTS
/// 125. TRUST_NOT_ESTABLISHED_IS_NEVER_PRESENTED_AS_INVALID_MEASUREMENT_EVIDENCE
/// 126. PROVISIONAL_QUALITY_IS_NEVER_PRESENTED_AS_FINAL
/// 129. EVIDENCE_QUALITY_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE
/// </summary>
public sealed record EvidenceQualityAssessment(
    string AssessmentId,
    EvidenceQualitySubject Subject,
    QualityAssessmentMaturity Maturity,
    EvidenceQualityBand MeasurementQualityBand,
    string? PackageIntegrityState,
    string? PackageTrustState,
    EvidenceQualityBand OverallEvidenceBand,
    EvidenceCoverage Coverage,
    IReadOnlyList<EvidenceQualityDimension> Dimensions,
    IReadOnlyList<EvidenceContributionDecision> ContributionDecisions,
    IReadOnlyList<string> ReasonCodes,
    string PolicyRefId,
    string InterpretationRefId,
    DateTimeOffset EvaluatedAtUtc,
    string? ReanalysisOf = null);
