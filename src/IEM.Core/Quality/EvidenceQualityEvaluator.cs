namespace IEM.Core.Quality;

/// <summary>
/// Evaluates scoped evidence quality against versioned policies, hard gates, and interval eligibility.
/// Invariants 114-131.
/// </summary>
public static class EvidenceQualityEvaluator
{
    public static EvidenceQualityAssessment EvaluateQuality(
        EvidenceQualitySubject subject,
        EvidenceCoverage coverage,
        IReadOnlyList<EvidenceQualityDimension> dimensions,
        IReadOnlyList<EvidenceContributionDecision> decisions,
        QualityAssessmentMaturity maturity,
        string? packageIntegrityState = null,
        string? packageTrustState = null,
        EvidenceQualityPolicy? policy = null,
        string? reanalysisOf = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(decisions);
        policy ??= EvidenceQualityPolicy.Default;

        var reasons = new List<string>();
        var interpretationRefId = $"eqe:v{policy.PolicyVersion}:{policy.PolicyHash}";

        // 1. Initial measurement quality based on coverage
        var fullBps = (int)(coverage.FullEligibleRatio * 10000);
        var totalValidBps = (int)(coverage.TotalValidCoverageRatio * 10000);

        EvidenceQualityBand measurementBand;
        if (fullBps >= policy.MinFullCoverageBasisPointsForStrong && totalValidBps >= policy.MinTotalValidCoverageBasisPointsForStrong)
        {
            measurementBand = EvidenceQualityBand.Strong;
            reasons.Add($"Pokrivenost validnim dokazima je visoka ({coverage.FullEligibleRatio:P1} puni, {coverage.TotalValidCoverageRatio:P1} ukupno).");
        }
        else if (totalValidBps >= policy.MinTotalValidCoverageBasisPointsForModerate)
        {
            measurementBand = EvidenceQualityBand.Moderate;
            reasons.Add($"Pokrivenost validnim dokazima je umerena ({coverage.TotalValidCoverageRatio:P1} ukupno).");
        }
        else if (totalValidBps >= policy.MinTotalValidCoverageBasisPointsForLimited)
        {
            measurementBand = EvidenceQualityBand.Limited;
            reasons.Add($"Pokrivenost validnim dokazima je ograničena ({coverage.TotalValidCoverageRatio:P1} ukupno).");
        }
        else
        {
            measurementBand = EvidenceQualityBand.Insufficient;
            reasons.Add($"Nedovoljna pokrivenost validnim dokazima ({coverage.TotalValidCoverageRatio:P1} ukupno, ispod praga).");
        }

        // 2. Dimension checks and Hard Gates (Invariant 121: CRITICAL_QUALITY_FAILURE_CANNOT_BE_AVERAGED_AWAY)
        var temporalDim = dimensions.FirstOrDefault(d => d.Dimension == QualityDimensionType.TemporalQuality);
        var probeDim = dimensions.FirstOrDefault(d => d.Dimension == QualityDimensionType.ProbeExecutionQuality);

        if (subject.Purpose == QualityPurpose.OutageDuration && temporalDim != null)
        {
            if (temporalDim.State is QualityDimensionState.Weak or QualityDimensionState.Unavailable)
            {
                if (measurementBand < EvidenceQualityBand.Limited)
                {
                    measurementBand = EvidenceQualityBand.Limited;
                }
                reasons.Add("Tvrđenje o tačnom trajanju prekida je ograničeno (Limited) usled prekida vremenskog kontinuiteta (skok sata / diskontinuitet).");
            }
        }

        if (probeDim != null && probeDim.State == QualityDimensionState.Unavailable)
        {
            measurementBand = EvidenceQualityBand.Insufficient;
            reasons.Add("Kvalitet merenja je nedostatan (Insufficient) usled dominantnih lokalnih/internih grešaka mehanizma proba.");
        }

        // 3. Package Integrity & Trust evaluation (Invariants 123, 124, 125, 126)
        var overallBand = measurementBand;

        if (maturity == QualityAssessmentMaturity.Finalized)
        {
            if (string.Equals(packageIntegrityState, "Invalid", StringComparison.OrdinalIgnoreCase))
            {
                // Invariant 124: INVALID_PACKAGE_INTEGRITY_CANNOT_BE_AVERAGED_AWAY_BY_STRONG_MEASUREMENTS
                overallBand = EvidenceQualityBand.Insufficient;
                reasons.Add("Integritet dokaznog paketa je nevažeći (Invalid). Forenzička upotrebljivost paketa je Insufficient uprkos prethodnim merenjima.");
            }
            else if (string.Equals(packageTrustState, "NotEstablished", StringComparison.OrdinalIgnoreCase))
            {
                // Invariant 125: TRUST_NOT_ESTABLISHED_IS_NEVER_PRESENTED_AS_INVALID_MEASUREMENT_EVIDENCE
                reasons.Add("Integritet paketa je verifikovan, ali vremenski žig treće strane nije uspostavljen (Trust = NotEstablished).");
            }
        }
        else
        {
            // Invariant 126: PROVISIONAL_QUALITY_IS_NEVER_PRESENTED_AS_FINAL
            reasons.Add("Privremena procena kvaliteta tokom aktivne sesije (Provisional).");
        }

        var assessmentId = $"eqa-{subject.Purpose}-{Guid.NewGuid():N}";

        return new EvidenceQualityAssessment(
            AssessmentId: assessmentId,
            Subject: subject,
            Maturity: maturity,
            MeasurementQualityBand: measurementBand,
            PackageIntegrityState: packageIntegrityState,
            PackageTrustState: packageTrustState,
            OverallEvidenceBand: overallBand,
            Coverage: coverage,
            Dimensions: dimensions,
            ContributionDecisions: decisions,
            ReasonCodes: reasons,
            PolicyRefId: policy.PolicyHash,
            InterpretationRefId: interpretationRefId,
            EvaluatedAtUtc: DateTimeOffset.UtcNow,
            ReanalysisOf: reanalysisOf);
    }
}
