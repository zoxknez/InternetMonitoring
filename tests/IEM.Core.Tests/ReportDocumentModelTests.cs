using System.Globalization;
using IEM.Core.Quality;
using IEM.Core.Reports;
using IEM.Core.Reports.Renderers;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-14: Unified Report Document Model.
/// Invariants 132-150.
/// </summary>
public sealed class ReportDocumentModelTests
{
    private static EvidenceAnalysisSnapshot CreateSampleSnapshot(
        string packageIntegrity = "Verified",
        string packageTrust = "NotEstablished",
        ClaimSupportState rootCauseSupport = ClaimSupportState.Unknown)
    {
        var now = DateTimeOffset.Parse("2026-08-19T12:00:00Z");
        var claims = new List<ReportClaim>
        {
            new("c1", "TargetReachability", "Meta 1.1.1.1 nije odgovarala u 20 uzastopnih pokušaja", ReportValue.FromInteger(20), EpistemicClass.Fact, ClaimSupportState.Supported, new[] { "obs:probe-1" }, new[] { "Timeout" }),
            new("c2", "OutageDuration", "Procenjeno trajanje prekida dostupnosti", ReportValue.FromDuration(TimeSpan.FromMinutes(10)), EpistemicClass.Assessment, ClaimSupportState.Limited, new[] { "obs:probe-1", "tcont:1" }, new[] { "ClockDiscontinuity" }),
            new("c3", "RootCauseAttribution", "Uzrok prekida na strani operatora", ReportValue.Unknown(), EpistemicClass.Inference, rootCauseSupport, new[] { "obs:gw-1" }, new[] { "AmbiguousFailure" }),
        };

        var qualitySubject = new EvidenceQualitySubject("Session", "ses-100", QualityPurpose.GeneralMeasurement);
        var coverage = new EvidenceCoverage(TimeSpan.FromHours(3), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromMinutes(110), TimeSpan.FromMinutes(10), TimeSpan.Zero, TimeSpan.Zero, "AssessableActiveTime");
        var dimensions = new List<EvidenceQualityDimension>
        {
            new(QualityDimensionType.TargetQuality, QualityDimensionState.Satisfied, 110, 10, 0, 0, Array.Empty<string>(), Array.Empty<string>()),
        };
        var quality = EvidenceQualityEvaluator.EvaluateQuality(qualitySubject, coverage, dimensions, Array.Empty<EvidenceContributionDecision>(), QualityAssessmentMaturity.Finalized, packageIntegrity, packageTrust);

        return new EvidenceAnalysisSnapshot(
            SessionRef: "ses-100",
            AnalysisVersion: "3.0.0",
            InterpretationRefId: "interp:ses-100",
            SessionStartUtc: now,
            SessionEndUtc: now.AddHours(3),
            TotalDuration: TimeSpan.FromHours(3),
            ActiveMonitoringDuration: TimeSpan.FromHours(2),
            HostSuspensionDuration: TimeSpan.FromHours(1),
            TargetsEvaluated: new[] { "1.1.1.1", "8.8.8.8" },
            TotalProbeAttempts: 1200,
            OutagesObservedCount: 1,
            TargetHealthSummary: "Zabeležen jedan interval prekida dostupnosti.",
            ProbeHealthSummary: "Sve lokalne sonde funkcionisale ispravno.",
            ClockContinuitySummary: "Zabeležen jedan skok sistemskog sata unazad.",
            QualityAssessments: new[] { quality },
            PackageIntegrityState: packageIntegrity,
            PackageTrustState: packageTrust,
            Claims: claims,
            SourceEvidenceRefs: new[] { "raw/probes.jsonl", "evidence/manifest.json" },
            GeneratedAtUtc: now.AddHours(3));
    }

    [Fact]
    public void Same_analysis_produces_identical_canonical_ReportDocumentModel_Invariant_146()
    {
        var snapshot = CreateSampleSnapshot();
        var model1 = ReportDocumentBuilder.Build(snapshot, ReportCompositionProfile.Technical);
        var model2 = ReportDocumentBuilder.Build(snapshot, ReportCompositionProfile.Technical);

        Assert.Equal(model1.Title, model2.Title);
        Assert.Equal(model1.Sections.Count, model2.Sections.Count);
        Assert.Equal(model1.OverallQualityBand, model2.OverallQualityBand);
        Assert.Equal(model1.Provenance.CompositionProfileRef, model2.Provenance.CompositionProfileRef);
    }

    [Fact]
    public void Complaint_profile_does_not_strengthen_Unknown_root_cause_Invariant_144()
    {
        var snapshot = CreateSampleSnapshot(rootCauseSupport: ClaimSupportState.Unknown);
        var model = ReportDocumentBuilder.Build(snapshot, ReportCompositionProfile.Complaint);

        var composer = new ComplaintNarrativeComposer();
        var output = composer.RenderToString(model);

        Assert.Contains("Nije utvrđeno (Unknown)", output);
        Assert.DoesNotContain("Operator je izazvao", output);
        Assert.DoesNotContain("Krivica operatora", output);
    }

    [Fact]
    public void CSV_projection_never_recalculates_measurement_values_Invariants_132_and_143()
    {
        var snapshot = CreateSampleSnapshot();
        var model = ReportDocumentBuilder.Build(snapshot, ReportCompositionProfile.Technical);

        var projection = new CsvReportProjection();
        var csv = projection.RenderToString(model);

        Assert.Contains("\"c1\",\"TargetReachability\",\"Fact\"", csv);
        Assert.Contains("\"c2\",\"OutageDuration\",\"Assessment\"", csv);
        Assert.Contains("\"c3\",\"RootCauseAttribution\",\"Inference\"", csv);
    }

    [Fact]
    public void Integrity_Verified_and_Trust_NotEstablished_remain_distinct_Invariant_140()
    {
        var snapshot = CreateSampleSnapshot(packageIntegrity: "Verified", packageTrust: "NotEstablished");
        var model = ReportDocumentBuilder.Build(snapshot);

        Assert.Equal("Verified", model.IntegrityState);
        Assert.Equal("NotEstablished", model.TrustState);

        var html = new HtmlReportRenderer().RenderToString(model);
        Assert.Contains("Integritet: <strong>Verified</strong>", html);
        Assert.Contains("Poverenje: <strong>NotEstablished</strong>", html);
    }

    [Fact]
    public void Localization_changes_format_not_numeric_semantics_Invariant_137()
    {
        var val = ReportValue.FromNumeric(1234.56, "Mbps");

        var sr = val.Format(new CultureInfo("sr-Latn-RS"));
        var en = val.Format(CultureInfo.InvariantCulture);

        Assert.Equal("1.234,56 Mbps", sr);
        Assert.Equal("1,234.56 Mbps", en);
        Assert.Equal(1234.56, val.NumericValue); // Underlying numeric value is exact and immutable!
    }

    [Fact]
    public void Golden_cross_renderer_acceptance_scenario_3_0_14()
    {
        var snapshot = CreateSampleSnapshot();
        var modelTechnical = ReportDocumentBuilder.Build(snapshot, ReportCompositionProfile.Technical);
        var modelComplaint = ReportDocumentBuilder.Build(snapshot, ReportCompositionProfile.Complaint);
        var modelRatel = ReportDocumentBuilder.Build(snapshot, ReportCompositionProfile.Ratel);

        var htmlRenderer = new HtmlReportRenderer();
        var csvProjection = new CsvReportProjection();
        var complaintComposer = new ComplaintNarrativeComposer();
        var ratelComposer = new RatelRegulatoryComposer();

        var htmlOutput = htmlRenderer.RenderToString(modelTechnical);
        var csvOutput = csvProjection.RenderToString(modelTechnical);
        var complaintOutput = complaintComposer.RenderToString(modelComplaint);
        var ratelOutput = ratelComposer.RenderToString(modelRatel);

        // 1. All preserve epistemic class Fact for c1
        Assert.Contains("[FACT]", htmlOutput);
        Assert.Contains("\"Fact\"", csvOutput);
        Assert.Contains("[Fact]", complaintOutput);
        Assert.Contains("[RATEL-EVIDENCE-Fact]", ratelOutput);

        // 2. All preserve Unknown / not blamed on ISP for c3
        Assert.DoesNotContain("Operator je kriv", htmlOutput);
        Assert.DoesNotContain("Operator je kriv", complaintOutput);
        Assert.DoesNotContain("Operator je kriv", ratelOutput);

        // 3. All preserve Integrity Verified vs Trust NotEstablished
        Assert.Contains("Verified", htmlOutput);
        Assert.Contains("NotEstablished", htmlOutput);
        Assert.Contains("Verified", complaintOutput);
        Assert.Contains("NotEstablished", complaintOutput);
        Assert.Contains("INTEGRITET: Verified | TRUST: NotEstablished", ratelOutput);
    }
}
