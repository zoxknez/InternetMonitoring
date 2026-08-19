using IEM.Core.Quality;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-13: Evidence Quality Engine.
/// Invariants 114-131.
/// </summary>
public sealed class EvidenceQualityEngineTests
{
    private static EvidenceCoverage CreateCoverage(
        TimeSpan totalWindow,
        TimeSpan nonObservable,
        TimeSpan fullEligible,
        TimeSpan reducedEligible,
        TimeSpan ineligible,
        TimeSpan unknown)
    {
        var assessable = totalWindow - nonObservable;
        return new EvidenceCoverage(
            ObservationWindow: totalWindow,
            NonObservableDuration: nonObservable,
            AssessableDuration: assessable,
            FullEligibleDuration: fullEligible,
            ReducedEligibleDuration: reducedEligible,
            IneligibleDuration: ineligible,
            UnknownDuration: unknown,
            DenominatorDefinition: "AssessableActiveTime");
    }

    [Fact]
    public void TargetReachability_scoped_quality_evaluation_Invariant_115()
    {
        // Invariant 115: EVIDENCE_QUALITY_IS_SCOPED_TO_THE_CLAIM_OR_ASSESSMENT_PURPOSE
        var subject = new EvidenceQualitySubject("Target", "1.1.1.1", QualityPurpose.TargetReachability);
        var coverage = CreateCoverage(
            totalWindow: TimeSpan.FromMinutes(60),
            nonObservable: TimeSpan.Zero,
            fullEligible: TimeSpan.FromMinutes(55),
            reducedEligible: TimeSpan.FromMinutes(3),
            ineligible: TimeSpan.FromMinutes(1),
            unknown: TimeSpan.FromMinutes(1));

        var dimensions = new List<EvidenceQualityDimension>
        {
            new(QualityDimensionType.TargetQuality, QualityDimensionState.Satisfied, 55, 3, 1, 1, Array.Empty<string>(), new[] { "All targets normally responding" }),
            new(QualityDimensionType.ProbeExecutionQuality, QualityDimensionState.Satisfied, 55, 3, 1, 1, Array.Empty<string>(), new[] { "Local execution clean" }),
        };

        var assessment = EvidenceQualityEvaluator.EvaluateQuality(
            subject, coverage, dimensions, Array.Empty<EvidenceContributionDecision>(), QualityAssessmentMaturity.Finalized,
            packageIntegrityState: "Verified", packageTrustState: "Established");

        Assert.Equal(EvidenceQualityBand.Strong, assessment.MeasurementQualityBand);
        Assert.Equal(EvidenceQualityBand.Strong, assessment.OverallEvidenceBand);
    }

    [Fact]
    public void Critical_temporal_failure_caps_outage_duration_claim_Invariant_121()
    {
        // Invariant 121: CRITICAL_QUALITY_FAILURE_CANNOT_BE_AVERAGED_AWAY
        var subject = new EvidenceQualitySubject("Outage", "Incident-1", QualityPurpose.OutageDuration);
        var coverage = CreateCoverage(
            totalWindow: TimeSpan.FromMinutes(60),
            nonObservable: TimeSpan.Zero,
            fullEligible: TimeSpan.FromMinutes(55),
            reducedEligible: TimeSpan.FromMinutes(5),
            ineligible: TimeSpan.Zero,
            unknown: TimeSpan.Zero);

        var dimensions = new List<EvidenceQualityDimension>
        {
            new(QualityDimensionType.TemporalQuality, QualityDimensionState.Weak, 50, 0, 10, 0, Array.Empty<string>(), new[] { "Clock jump backward observed" }),
            new(QualityDimensionType.TargetQuality, QualityDimensionState.Satisfied, 55, 5, 0, 0, Array.Empty<string>(), new[] { "Target loss recorded" }),
        };

        var assessment = EvidenceQualityEvaluator.EvaluateQuality(
            subject, coverage, dimensions, Array.Empty<EvidenceContributionDecision>(), QualityAssessmentMaturity.Finalized,
            packageIntegrityState: "Verified", packageTrustState: "Established");

        Assert.Equal(EvidenceQualityBand.Limited, assessment.MeasurementQualityBand); // Capped at Limited due to temporal failure
        Assert.Contains("ograničeno (Limited)", assessment.ReasonCodes.Last());
    }

    [Fact]
    public void Invalid_package_integrity_overrides_overall_band_to_insufficient_Invariant_124()
    {
        // Invariant 124: INVALID_PACKAGE_INTEGRITY_CANNOT_BE_AVERAGED_AWAY_BY_STRONG_MEASUREMENTS
        var subject = new EvidenceQualitySubject("Session", "ses-1", QualityPurpose.GeneralMeasurement);
        var coverage = CreateCoverage(
            totalWindow: TimeSpan.FromMinutes(60),
            nonObservable: TimeSpan.Zero,
            fullEligible: TimeSpan.FromMinutes(58),
            reducedEligible: TimeSpan.FromMinutes(2),
            ineligible: TimeSpan.Zero,
            unknown: TimeSpan.Zero);

        var dimensions = new List<EvidenceQualityDimension>
        {
            new(QualityDimensionType.TargetQuality, QualityDimensionState.Satisfied, 58, 2, 0, 0, Array.Empty<string>(), Array.Empty<string>()),
        };

        var assessment = EvidenceQualityEvaluator.EvaluateQuality(
            subject, coverage, dimensions, Array.Empty<EvidenceContributionDecision>(), QualityAssessmentMaturity.Finalized,
            packageIntegrityState: "Invalid", packageTrustState: "NotApplicable");

        Assert.Equal(EvidenceQualityBand.Strong, assessment.MeasurementQualityBand); // Measurements were strong
        Assert.Equal(EvidenceQualityBand.Insufficient, assessment.OverallEvidenceBand); // Overall package is Insufficient
        Assert.Contains("Invalid", assessment.ReasonCodes.Last());
    }

    [Fact]
    public void Valid_measurements_remain_strong_even_if_trust_not_established_Invariant_125()
    {
        // Invariant 125: TRUST_NOT_ESTABLISHED_IS_NEVER_PRESENTED_AS_INVALID_MEASUREMENT_EVIDENCE
        var subject = new EvidenceQualitySubject("Session", "ses-1", QualityPurpose.GeneralMeasurement);
        var coverage = CreateCoverage(
            totalWindow: TimeSpan.FromMinutes(60),
            nonObservable: TimeSpan.Zero,
            fullEligible: TimeSpan.FromMinutes(58),
            reducedEligible: TimeSpan.FromMinutes(2),
            ineligible: TimeSpan.Zero,
            unknown: TimeSpan.Zero);

        var dimensions = new List<EvidenceQualityDimension>
        {
            new(QualityDimensionType.TargetQuality, QualityDimensionState.Satisfied, 58, 2, 0, 0, Array.Empty<string>(), Array.Empty<string>()),
        };

        var assessment = EvidenceQualityEvaluator.EvaluateQuality(
            subject, coverage, dimensions, Array.Empty<EvidenceContributionDecision>(), QualityAssessmentMaturity.Finalized,
            packageIntegrityState: "Verified", packageTrustState: "NotEstablished");

        Assert.Equal(EvidenceQualityBand.Strong, assessment.MeasurementQualityBand);
        Assert.Equal(EvidenceQualityBand.Strong, assessment.OverallEvidenceBand);
        Assert.Contains(assessment.ReasonCodes, r => r.Contains("NotEstablished"));
    }

    [Fact]
    public void Package_integrity_never_proves_measurement_truth_when_probes_failed_Invariant_123()
    {
        // Invariant 123: PACKAGE_INTEGRITY_NEVER_PROVES_MEASUREMENT_TRUTH
        var subject = new EvidenceQualitySubject("Session", "ses-1", QualityPurpose.GeneralMeasurement);
        var coverage = CreateCoverage(
            totalWindow: TimeSpan.FromMinutes(60),
            nonObservable: TimeSpan.Zero,
            fullEligible: TimeSpan.FromMinutes(5),
            reducedEligible: TimeSpan.FromMinutes(5),
            ineligible: TimeSpan.FromMinutes(50), // 90% failed local executions
            unknown: TimeSpan.Zero);

        var dimensions = new List<EvidenceQualityDimension>
        {
            new(QualityDimensionType.ProbeExecutionQuality, QualityDimensionState.Unavailable, 5, 5, 50, 0, Array.Empty<string>(), new[] { "90% local socket error" }),
        };

        var assessment = EvidenceQualityEvaluator.EvaluateQuality(
            subject, coverage, dimensions, Array.Empty<EvidenceContributionDecision>(), QualityAssessmentMaturity.Finalized,
            packageIntegrityState: "Verified", packageTrustState: "Established");

        Assert.Equal(EvidenceQualityBand.Insufficient, assessment.MeasurementQualityBand);
        Assert.Equal(EvidenceQualityBand.Insufficient, assessment.OverallEvidenceBand);
    }

    [Fact]
    public void Non_observable_suspend_time_does_not_count_as_outage_or_poor_evidence_Invariant_119()
    {
        // Invariant 119: NON_OBSERVABLE_TIME_IS_NEVER_TREATED_AS_NEGATIVE_NETWORK_EVIDENCE
        var subject = new EvidenceQualitySubject("Target", "1.1.1.1", QualityPurpose.TargetReachability);

        // 2-hour session with 1 hour of sleep
        var coverage = CreateCoverage(
            totalWindow: TimeSpan.FromHours(2),
            nonObservable: TimeSpan.FromHours(1),
            fullEligible: TimeSpan.FromMinutes(55),
            reducedEligible: TimeSpan.FromMinutes(5),
            ineligible: TimeSpan.Zero,
            unknown: TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(1), coverage.AssessableDuration);
        Assert.True(coverage.TotalValidCoverageRatio >= 0.99); // 100% of assessable active time!

        var dimensions = new List<EvidenceQualityDimension>
        {
            new(QualityDimensionType.TargetQuality, QualityDimensionState.Satisfied, 55, 5, 0, 0, Array.Empty<string>(), Array.Empty<string>()),
        };

        var assessment = EvidenceQualityEvaluator.EvaluateQuality(
            subject, coverage, dimensions, Array.Empty<EvidenceContributionDecision>(), QualityAssessmentMaturity.Finalized,
            packageIntegrityState: "Verified", packageTrustState: "Established");

        Assert.Equal(EvidenceQualityBand.Strong, assessment.MeasurementQualityBand);
    }

    [Fact]
    public void Golden_acceptance_scenario_3_0_13()
    {
        // Total session: 180 min
        // Suspend (T60-T120): 60 min (NonObservable)
        // Assessable active monitoring: 120 min
        // Clean segments (T0-T20, T25-T50, T130-T180): 95 min Full
        // Reduced segment (T50-T60): 10 min Reduced
        // Ineligible segment (T20-T25): 5 min Ineligible
        // Ambiguous clock jump (T120-T130): 10 min
        var coverage = CreateCoverage(
            totalWindow: TimeSpan.FromMinutes(180),
            nonObservable: TimeSpan.FromMinutes(60),
            fullEligible: TimeSpan.FromMinutes(95),
            reducedEligible: TimeSpan.FromMinutes(10),
            ineligible: TimeSpan.FromMinutes(5),
            unknown: TimeSpan.FromMinutes(10));

        var decisions = new List<EvidenceContributionDecision>
        {
            new("d1", "Target:1.1.1.1", "T0-T20", QualityEligibility.Full, 10000, new[] { "Clean execution" }, Array.Empty<string>(), "p1", "i1"),
            new("d2", "Target:1.1.1.1", "T20-T25", QualityEligibility.Ineligible, 0, new[] { "Local resolver failure" }, Array.Empty<string>(), "p1", "i1"),
            new("d3", "Target:1.1.1.1", "T60-T120", QualityEligibility.NotObservable, 0, new[] { "Host in sleep state" }, Array.Empty<string>(), "p1", "i1"),
        };

        var dimensions = new List<EvidenceQualityDimension>
        {
            new(QualityDimensionType.TargetQuality, QualityDimensionState.Satisfied, 95, 10, 5, 10, Array.Empty<string>(), Array.Empty<string>()),
            new(QualityDimensionType.TemporalQuality, QualityDimensionState.Weak, 95, 0, 0, 10, Array.Empty<string>(), new[] { "Clock jump observed" }),
        };

        // 1. TargetReachability claim
        var subjectReachability = new EvidenceQualitySubject("Target", "1.1.1.1", QualityPurpose.TargetReachability);
        var assessReachability = EvidenceQualityEvaluator.EvaluateQuality(
            subjectReachability, coverage, dimensions, decisions, QualityAssessmentMaturity.Finalized,
            packageIntegrityState: "Verified", packageTrustState: "NotEstablished");

        Assert.Equal(EvidenceQualityBand.Strong, assessReachability.MeasurementQualityBand);
        Assert.Equal(EvidenceQualityBand.Strong, assessReachability.OverallEvidenceBand);

        // 2. OutageDuration claim -> Capped at Limited due to temporal discontinuity
        var subjectOutage = new EvidenceQualitySubject("Outage", "Inc-1", QualityPurpose.OutageDuration);
        var assessOutage = EvidenceQualityEvaluator.EvaluateQuality(
            subjectOutage, coverage, dimensions, decisions, QualityAssessmentMaturity.Finalized,
            packageIntegrityState: "Verified", packageTrustState: "NotEstablished");

        Assert.Equal(EvidenceQualityBand.Limited, assessOutage.MeasurementQualityBand);
        Assert.Equal(EvidenceQualityBand.Limited, assessOutage.OverallEvidenceBand);
    }
}
