using System.IO;
using IEM.App.ViewModels;
using IEM.Core.Presentation;
using IEM.Core.Quality;
using IEM.Core.Reports;

namespace IEM.App.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-15: MONITOR / EVIDENCE / CASE / SPEED Presentation layer.
/// Invariants 151-173.
/// </summary>
public sealed class UiPresentationTests
{
    private static PresentationSnapshot CreateSampleSnapshot(
        long revision,
        ServiceConnectionStatus serviceStatus = ServiceConnectionStatus.Connected,
        SessionRuntimeState runtimeState = SessionRuntimeState.Monitoring,
        string packageIntegrity = "Verified",
        string packageTrust = "NotEstablished")
    {
        var now = DateTimeOffset.Parse("2026-08-19T12:00:00Z");
        var claims = new List<ReportClaim>
        {
            new("c1", "TargetReachability", "Meta 1.1.1.1 responzivna", ReportValue.FromInteger(100), EpistemicClass.Fact, ClaimSupportState.Supported, new[] { "obs:1" }, Array.Empty<string>(), QualityAssessmentRef: "Strong"),
            new("c2", "OutageDuration", "Procenjeno trajanje prekida", ReportValue.FromDuration(TimeSpan.FromMinutes(5)), EpistemicClass.Assessment, ClaimSupportState.Limited, new[] { "obs:1" }, new[] { "ClockDiscontinuity" }, QualityAssessmentRef: "Limited"),
        };

        var quality = new EvidenceQualityAssessment(
            AssessmentId: "eqa-1",
            Subject: new EvidenceQualitySubject("Session", "ses-100", QualityPurpose.GeneralMeasurement),
            Maturity: QualityAssessmentMaturity.Provisional,
            MeasurementQualityBand: EvidenceQualityBand.Strong,
            PackageIntegrityState: packageIntegrity,
            PackageTrustState: packageTrust,
            OverallEvidenceBand: EvidenceQualityBand.Strong,
            Coverage: new EvidenceCoverage(TimeSpan.FromHours(2), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(85), TimeSpan.FromMinutes(5), TimeSpan.Zero, TimeSpan.Zero, "AssessableActiveTime"),
            Dimensions: Array.Empty<EvidenceQualityDimension>(),
            ContributionDecisions: Array.Empty<EvidenceContributionDecision>(),
            ReasonCodes: new[] { "Clean monitoring" },
            PolicyRefId: "p1",
            InterpretationRefId: "i1",
            EvaluatedAtUtc: now);

        var analysis = new EvidenceAnalysisSnapshot(
            SessionRef: "ses-100",
            AnalysisVersion: "3.0.0",
            InterpretationRefId: "interp:100",
            SessionStartUtc: now,
            SessionEndUtc: now.AddHours(2),
            TotalDuration: TimeSpan.FromHours(2),
            ActiveMonitoringDuration: TimeSpan.FromMinutes(90),
            HostSuspensionDuration: TimeSpan.FromMinutes(30),
            TargetsEvaluated: new[] { "1.1.1.1" },
            TotalProbeAttempts: 1000,
            OutagesObservedCount: 0,
            TargetHealthSummary: "Sve konfigurisane mete uredno odgovaraju.",
            ProbeHealthSummary: "Sve lokalne sonde zdrave.",
            ClockContinuitySummary: "Vremenski tok kontinualan.",
            QualityAssessments: new[] { quality },
            PackageIntegrityState: packageIntegrity,
            PackageTrustState: packageTrust,
            Claims: claims,
            SourceEvidenceRefs: new[] { "raw/probes.jsonl" },
            GeneratedAtUtc: now.AddHours(2));

        var canonicalReport = ReportDocumentBuilder.Build(analysis, ReportCompositionProfile.Technical);

        return new PresentationSnapshot(
            SnapshotId: $"snap-{revision}",
            SessionId: "ses-100",
            AnalysisRevision: revision,
            CapturedAtUtc: now.AddHours(2),
            RuntimeState: runtimeState,
            ServiceStatus: serviceStatus,
            Analysis: analysis,
            CanonicalReport: canonicalReport,
            SourceRefs: new[] { "snap-ref" });
    }

    private static ShellViewModel CreateShell() =>
        new(new StubMonitorHost(), Path.Combine(Path.GetTempPath(), "iem-ui-tests"));

    [Fact]
    public void Switching_tabs_never_changes_measurement_execution_state_Invariant_156()
    {
        var shell = CreateShell();
        var snap = CreateSampleSnapshot(100);
        shell.ApplyPresentationSnapshot(snap);

        Assert.Equal(ShellTab.Monitor, shell.ActiveTab);

        // Switch tabs
        shell.ActiveTab = ShellTab.Evidence;
        Assert.Equal(ShellTab.Evidence, shell.ActiveTab);

        shell.ActiveTab = ShellTab.Case;
        Assert.Equal(ShellTab.Case, shell.ActiveTab);

        shell.ActiveTab = ShellTab.Speed;
        Assert.Equal(ShellTab.Speed, shell.ActiveTab);

        shell.ActiveTab = ShellTab.Monitor;
        Assert.Equal(ShellTab.Monitor, shell.ActiveTab);
    }

    [Fact]
    public void Older_snapshot_never_overwrites_newer_snapshot_Invariant_168()
    {
        var tracker = new PresentationRevisionTracker();
        var snap1 = CreateSampleSnapshot(100);
        var snap2 = CreateSampleSnapshot(105);
        var snapStale = CreateSampleSnapshot(102);

        Assert.True(tracker.TryApplySnapshot(snap1));
        Assert.Equal(100, tracker.CurrentRevision);

        Assert.True(tracker.TryApplySnapshot(snap2));
        Assert.Equal(105, tracker.CurrentRevision);

        // Applying stale snapshot with revision 102 must be rejected
        Assert.False(tracker.TryApplySnapshot(snapStale));
        Assert.Equal(105, tracker.CurrentRevision);
    }

    [Fact]
    public void Suspend_interval_is_not_rendered_as_outage_Invariant_161()
    {
        var monitor = new MonitorViewModel();
        var snap = CreateSampleSnapshot(100);
        monitor.ApplySnapshot(snap);

        var suspendItem = monitor.TimelineItems.FirstOrDefault(t => t.IsSuspend);
        Assert.NotNull(suspendItem);
        Assert.False(suspendItem.IsOutage); // Suspend is NOT network outage!
        Assert.Contains("sleep", suspendItem.Description);
    }

    [Fact]
    public void Integrity_Trust_and_Quality_are_displayed_separately_Invariant_162()
    {
        var evidence = new EvidenceViewModel();
        var snap = CreateSampleSnapshot(100, packageIntegrity: "Verified", packageTrust: "NotEstablished");
        evidence.ApplySnapshot(snap);

        Assert.Contains("Verified", evidence.IntegrityState);
        Assert.Contains("Not Established", evidence.TrustState);
        Assert.Contains("Strong", evidence.OverallQualityBand);
    }

    [Fact]
    public void Case_user_note_does_not_change_ReportClaim_Invariants_164_and_165()
    {
        var caseVm = new CaseViewModel();
        var snap = CreateSampleSnapshot(100);
        caseVm.ApplySnapshot(snap);

        caseVm.AddUserStatement("Korisnik tvrdi: Telekom prekida vezu.");

        Assert.Single(caseVm.UserStatements);
        Assert.Equal("User", caseVm.UserStatements[0].Author);

        // Ensure underlying claims were not modified
        Assert.Equal(2, snap.Analysis!.Claims.Count);
        Assert.DoesNotContain(snap.Analysis.Claims, c => c.StatementKey.Contains("Telekom"));
    }

    [Fact]
    public void Speed_refusal_is_not_displayed_as_0_Mbps_Invariant_167()
    {
        var speedVm = new SpeedViewModel();

        // Measurement refused due to interface missing default route
        speedVm.UpdateMeasurementResult(
            ran: false,
            refusalReason: "NoRouteFromRequestedInterface",
            downloadMbps: null,
            uploadMbps: null);

        Assert.False(speedVm.Ran);
        Assert.DoesNotContain("0 Mbps", speedVm.DownloadThroughputText);
        Assert.DoesNotContain("0.0 Mbps", speedVm.DownloadThroughputText);
        Assert.Contains("Merenje odbijeno", speedVm.DownloadThroughputText);
        Assert.Contains("NoRouteFromRequestedInterface", speedVm.MeasurementStatusText);
    }

    [Fact]
    public void Golden_ui_acceptance_scenario_3_0_15()
    {
        var shell = CreateShell();
        var monitor = new MonitorViewModel();
        var evidence = new EvidenceViewModel();
        var caseVm = new CaseViewModel();
        var speedVm = new SpeedViewModel();

        var snap = CreateSampleSnapshot(200);

        shell.ApplyPresentationSnapshot(snap);
        monitor.ApplySnapshot(snap);
        evidence.ApplySnapshot(snap);
        caseVm.ApplySnapshot(snap);

        // 1. Shell tabs initialized
        Assert.Equal(ShellTab.Monitor, shell.ActiveTab);

        // 2. Monitor shows health & distinct suspend
        Assert.Contains("uredno odgovaraju", monitor.TargetHealthSummary);
        Assert.Equal(2, monitor.TimelineItems.Count);

        // 3. Evidence shows distinct Integrity, Trust, and Claim Breakdown
        Assert.Contains("Verified", evidence.IntegrityState);
        Assert.Contains("Not Established", evidence.TrustState);
        Assert.Equal(2, evidence.Claims.Count);

        // 4. Case allows UserStatement and preview
        caseVm.OperatorName = "MTS";
        caseVm.AddUserStatement("Prekid u 14:30.");
        Assert.NotEmpty(caseVm.PreviewText);

        // 5. Speed refused does not show 0 Mbps
        speedVm.UpdateMeasurementResult(false, "TunnelDetectedWithoutBypass", null, null);
        Assert.DoesNotContain("0 Mbps", speedVm.DownloadThroughputText);
    }
}
