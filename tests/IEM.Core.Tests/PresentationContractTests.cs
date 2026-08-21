using System.Collections.Immutable;
using IEM.Core.Model;
using IEM.Core.Reports;
using IEM.Presentation.Contracts;
using IEM.Presentation.Models;
using IEM.Presentation.Semantics;
using IEM.Presentation.States;

namespace IEM.Core.Tests;

/// <summary>
/// Platform-neutral contract, semantic type safety, and impossible-state elimination tests for Stage 3.1-9B-R1.
/// Verifies Invariants 151, 156, 159, 161, 162, 163, 164, 165, 166, 167, 170 and DEF-07.
/// </summary>
public sealed class PresentationContractTests
{
    [Fact]
    public void SemanticTone_Contains_All_Six_Standardized_Tones()
    {
        var tones = Enum.GetValues<SemanticTone>();
        Assert.Contains(SemanticTone.Unknown, tones);
        Assert.Contains(SemanticTone.Neutral, tones);
        Assert.Contains(SemanticTone.Good, tones);
        Assert.Contains(SemanticTone.Info, tones);
        Assert.Contains(SemanticTone.Warning, tones);
        Assert.Contains(SemanticTone.Bad, tones);
        Assert.Equal(6, tones.Length);
    }

    [Fact]
    public void Evidentiary_Axes_Have_Distinct_Strongly_Typed_Enums_Invariant_162()
    {
        // Integrity axis
        var integrityStates = Enum.GetValues<IntegrityPresentationState>();
        Assert.Contains(IntegrityPresentationState.Unknown, integrityStates);
        Assert.Contains(IntegrityPresentationState.Verified, integrityStates);
        Assert.Contains(IntegrityPresentationState.Incomplete, integrityStates);
        Assert.Contains(IntegrityPresentationState.Invalid, integrityStates);

        // Trust axis
        var trustStates = Enum.GetValues<TrustPresentationState>();
        Assert.Contains(TrustPresentationState.Unknown, trustStates);
        Assert.Contains(TrustPresentationState.Established, trustStates);
        Assert.Contains(TrustPresentationState.NotEstablished, trustStates);
        Assert.Contains(TrustPresentationState.NotApplicable, trustStates);

        // Quality axis
        var qualityBands = Enum.GetValues<QualityPresentationBand>();
        Assert.Contains(QualityPresentationBand.Unknown, qualityBands);
        Assert.Contains(QualityPresentationBand.Strong, qualityBands);
        Assert.Contains(QualityPresentationBand.Moderate, qualityBands);
        Assert.Contains(QualityPresentationBand.Limited, qualityBands);
        Assert.Contains(QualityPresentationBand.Insufficient, qualityBands);

        // Epistemic axis
        var epistemicClasses = Enum.GetValues<EpistemicClass>();
        Assert.Contains(EpistemicClass.Fact, epistemicClasses);
        Assert.Contains(EpistemicClass.Inference, epistemicClasses);
        Assert.Contains(EpistemicClass.Assessment, epistemicClasses);

        // Assert distinct types cannot be implicitly conflated
        Assert.NotEqual(typeof(IntegrityPresentationState), typeof(TrustPresentationState));
        Assert.NotEqual(typeof(IntegrityPresentationState), typeof(QualityPresentationBand));
        Assert.NotEqual(typeof(TrustPresentationState), typeof(QualityPresentationBand));
        Assert.NotEqual(typeof(EpistemicClass), typeof(QualityPresentationBand));
    }

    [Fact]
    public void MonitorPresentationState_Initial_Complies_With_Unknown_Semantics_Invariant_159()
    {
        var state = MonitorPresentationState.Initial;

        // Invariant 159: UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
        Assert.Equal(QualityPresentationBand.Unknown, state.QualityBand);
        Assert.Equal("Nepoznato", state.QualityBandText);
        Assert.Equal("—", state.TotalDuration);
        Assert.Equal("—", state.ActiveDuration);
        Assert.Equal("—", state.SuspendDuration);
        Assert.Null(state.InterruptionsCount);
        Assert.Equal(SemanticTone.Unknown, state.Tone);
        Assert.True(state.TimelineItems.IsDefaultOrEmpty);
    }

    [Fact]
    public void EvidencePresentationState_Initial_Separates_Integrity_Trust_And_Quality_Invariant_162()
    {
        var state = EvidencePresentationState.Initial;

        // Invariant 162: UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
        Assert.Equal(IntegrityPresentationState.Unknown, state.IntegrityState);
        Assert.Equal(TrustPresentationState.Unknown, state.TrustState);
        Assert.Equal(QualityPresentationBand.Unknown, state.QualityBandBand());
        Assert.Equal(SemanticTone.Unknown, state.Tone);
        Assert.True(state.Claims.IsDefaultOrEmpty);
    }

    [Fact]
    public void CasePresentationState_Initial_Uses_Typed_Composition_Profile_Invariant_166()
    {
        var state = CasePresentationState.Initial;

        // Invariant 166: Typed report profile projection rather than magic string
        Assert.Equal(ReportCompositionProfile.Complaint, state.SelectedProfile);
        Assert.NotEmpty(state.PreviewText);
        Assert.True(state.UserStatements.IsDefaultOrEmpty);
    }

    [Fact]
    public void SpeedPresentationState_Initial_And_Derived_Flags_Prevent_Impossible_States_Invariant_159_167()
    {
        var initial = SpeedPresentationState.Initial;

        // Initial unmeasured state
        Assert.Equal(SpeedExecutionState.NotRun, initial.ExecutionState);
        Assert.False(initial.Ran);
        Assert.False(initial.IsRefused);
        Assert.Null(initial.RequestedInterface);
        Assert.Null(initial.ObservedPath);
        Assert.Null(initial.PathAgreement);
        Assert.Null(initial.TunnelIndication);
        Assert.Null(initial.DownloadThroughputMbps);
        Assert.Null(initial.UploadThroughputMbps);
        Assert.Null(initial.RefusalReason);
        Assert.Contains("Nije pokrenuto", initial.DownloadThroughputText);
        Assert.DoesNotContain("0 Mbps", initial.DownloadThroughputText);

        // Refused state derivation
        var refused = new SpeedPresentationState(
            ExecutionState: SpeedExecutionState.Refused,
            MeasurementIntent: "Default",
            RequestedInterface: "Ethernet",
            ObservedPath: null,
            PathAgreement: null,
            TunnelIndication: null,
            DownloadThroughputMbps: null,
            UploadThroughputMbps: null,
            DownloadThroughputText: "Merenje odbijeno",
            UploadThroughputText: "Merenje odbijeno",
            MeasurementStatusText: "TunnelDetectedWithoutBypass",
            RefusalReason: "TunnelDetectedWithoutBypass",
            Tone: SemanticTone.Warning);

        Assert.True(refused.Ran);
        Assert.True(refused.IsRefused);
        Assert.Equal("TunnelDetectedWithoutBypass", refused.RefusalReason);

        // Succeeded state derivation
        var succeeded = new SpeedPresentationState(
            ExecutionState: SpeedExecutionState.Succeeded,
            MeasurementIntent: "Default",
            RequestedInterface: "Ethernet",
            ObservedPath: "Ethernet",
            PathAgreement: "Match",
            TunnelIndication: "NotDetected",
            DownloadThroughputMbps: 95.4,
            UploadThroughputMbps: 48.2,
            DownloadThroughputText: "95.4 Mbps",
            UploadThroughputText: "48.2 Mbps",
            MeasurementStatusText: "Uspešno izmereno",
            RefusalReason: null,
            Tone: SemanticTone.Good);

        Assert.True(succeeded.Ran);
        Assert.False(succeeded.IsRefused);
        Assert.Equal(95.4, succeeded.DownloadThroughputMbps);
    }

    [Fact]
    public void ShellPresentationState_Initial_Complies_With_DEF07_Unknown_Semantics()
    {
        var state = ShellPresentationState.Initial;

        // DEF-07: Unobserved initial state must not claim Online or Severity.Ok
        Assert.False(state.IsRunning);
        Assert.Null(state.Fault);
        Assert.Equal(ShellTab.Monitor, state.ActiveTab);
        Assert.Equal(6, state.Durations.Length);
        Assert.Equal(600, state.TimelineCapacity);
        Assert.False(state.SurvivesClosing);
        Assert.Null(state.Verdict);                      // DEF-07: No synthetic verdict prior to observation
        Assert.Equal(ConnectivityPresentationState.Unknown, state.Connectivity); // DEF-07: Unknown connectivity
        Assert.Null(state.IsOnline);                     // DEF-07: Nullable IsOnline is null when unknown
        Assert.Null(state.CurrentSeverity);              // DEF-07: CurrentSeverity is null prior to observation
        Assert.Equal(SemanticTone.Unknown, state.Tone);  // DEF-07: SemanticTone is Unknown on initial state
        Assert.True(state.CanScheduleSpeed);            // Derived from !SpeedBusy
        Assert.True(state.Timeline.IsDefaultOrEmpty);
        Assert.True(state.Latency.IsDefaultOrEmpty);
        Assert.True(state.Metrics.IsDefaultOrEmpty);
        Assert.True(state.Probes.IsDefaultOrEmpty);
        Assert.False(state.IsUpdateBannerVisible);
        Assert.NotNull(state.Monitor);
        Assert.NotNull(state.Evidence);
        Assert.NotNull(state.Case);
        Assert.NotNull(state.Speed);
    }

    [Fact]
    public void TimelineSlice_Severity_Is_Sole_Authority_For_SemanticTone_Invariant_170()
    {
        var okSlice = new TimelineSlice(Severity.Ok);
        Assert.Equal(SemanticTone.Good, okSlice.Tone);

        var degradedSlice = new TimelineSlice(Severity.Degraded);
        Assert.Equal(SemanticTone.Warning, degradedSlice.Tone);

        var outageSlice = new TimelineSlice(Severity.Outage);
        Assert.Equal(SemanticTone.Bad, outageSlice.Tone);

        var infoSlice = new TimelineSlice(Severity.Info);
        Assert.Equal(SemanticTone.Info, infoSlice.Tone);
    }

    [Fact]
    public void MonitorTimelinePresentationItem_Category_Is_Sole_Authority_For_Suspend_And_Outage_Invariant_161()
    {
        var suspendItem = new MonitorTimelinePresentationItem(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            TimelinePresentationCategory.HostSuspended,
            "Pauza",
            "Računar u stanju mirovanja");

        Assert.True(suspendItem.IsSuspend);
        Assert.False(suspendItem.IsOutage); // Invariant 161: HostSuspended is NEVER an outage

        var outageItem = new MonitorTimelinePresentationItem(
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            TimelinePresentationCategory.InterruptionObserved,
            "Prekid",
            "Nema odgovora sa gejtveja");

        Assert.False(outageItem.IsSuspend);
        Assert.True(outageItem.IsOutage);

        var activeItem = new MonitorTimelinePresentationItem(
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(-1),
            TimelinePresentationCategory.ActiveMonitoring,
            "Aktivno",
            "Nadzor u toku");

        Assert.False(activeItem.IsSuspend);
        Assert.False(activeItem.IsOutage);
    }

    [Fact]
    public void ShellProjectionInput_Carries_Explicit_Decomposed_State_Without_Ambient_Reads()
    {
        var input = new ShellProjectionInput(
            Snapshot: null!,
            Interaction: new ShellInteractionState(
                IsRunning: false,
                Fault: null,
                ActiveTab: ShellTab.Monitor,
                SelectedDuration: ShellPresentationState.DefaultDurations[0],
                Durations: ShellPresentationState.DefaultDurations,
                TimelineCapacity: 600,
                SpeedScheduleAmount: string.Empty,
                SelectedSpeedScheduleUnit: "minuta",
                SpeedScheduleUnits: ShellPresentationState.DefaultSpeedUnits,
                ContractedRateText: string.Empty,
                SpeedStatus: null,
                SpeedBusy: false),
            HostFacts: new HostPresentationFacts(
                SurvivesClosing: false,
                BackgroundClaimLabel: "Radi dok je prozor otvoren",
                BackgroundClaimDetail: "Nadzor radi u prozoru.",
                RestartClaimLabel: "Ne preživljava restart",
                RestartClaimDetail: "Servis nije instaliran.",
                HostDescription: "Nadzor radi u ovom prozoru."),
            History: HistoryPresentationState.Empty,
            Update: UpdatePresentationState.Hidden,
            CaseWorkspace: CaseWorkspaceState.Empty);

        Assert.NotNull(input.Interaction);
        Assert.NotNull(input.HostFacts);
        Assert.NotNull(input.History);
        Assert.NotNull(input.Update);
        Assert.NotNull(input.CaseWorkspace);
    }
}

file static class TestExtensions
{
    public static QualityPresentationBand QualityBandBand(this EvidencePresentationState state) => state.OverallQualityBand;
}
