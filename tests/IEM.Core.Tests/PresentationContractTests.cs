using IEM.Core.Reports;
using IEM.Presentation.Contracts;
using IEM.Presentation.Models;
using IEM.Presentation.Semantics;
using IEM.Presentation.States;

namespace IEM.Core.Tests;

/// <summary>
/// Platform-neutral contract and semantic state tests for Stage 3.1-9B.
/// Verifies Invariants 151, 156, 159, 161, 162, 163, 164, 165, 166, 167, 170.
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
    public void BadgeKind_Covers_Epistemic_Integrity_Trust_And_Quality_Bands()
    {
        var badges = Enum.GetValues<BadgeKind>();
        // Epistemic
        Assert.Contains(BadgeKind.Fact, badges);
        Assert.Contains(BadgeKind.Inference, badges);
        Assert.Contains(BadgeKind.Assessment, badges);

        // Integrity
        Assert.Contains(BadgeKind.Verified, badges);
        Assert.Contains(BadgeKind.Incomplete, badges);
        Assert.Contains(BadgeKind.Invalid, badges);

        // Trust
        Assert.Contains(BadgeKind.Established, badges);
        Assert.Contains(BadgeKind.NotEstablished, badges);
        Assert.Contains(BadgeKind.NotApplicable, badges);

        // Quality
        Assert.Contains(BadgeKind.Strong, badges);
        Assert.Contains(BadgeKind.Moderate, badges);
        Assert.Contains(BadgeKind.Limited, badges);
        Assert.Contains(BadgeKind.Insufficient, badges);

        // Unknown
        Assert.Contains(BadgeKind.Unknown, badges);
    }

    [Fact]
    public void MonitorPresentationState_Initial_Complies_With_Unknown_Semantics_Invariant_159()
    {
        var state = MonitorPresentationState.Initial;

        // Invariant 159: UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
        Assert.Equal(BadgeKind.Unknown, state.QualityBand);
        Assert.Equal("Nepoznato", state.QualityBandText);
        Assert.Equal("—", state.TotalDuration);
        Assert.Equal("—", state.ActiveDuration);
        Assert.Equal("—", state.SuspendDuration);
        Assert.Null(state.InterruptionsCount);
        Assert.Equal(SemanticTone.Unknown, state.Tone);
        Assert.Empty(state.TimelineItems);
    }

    [Fact]
    public void EvidencePresentationState_Initial_Separates_Integrity_Trust_And_Quality_Invariant_162()
    {
        var state = EvidencePresentationState.Initial;

        // Invariant 162: UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
        Assert.Equal(BadgeKind.Unknown, state.IntegrityState);
        Assert.Equal(BadgeKind.Unknown, state.TrustState);
        Assert.Equal(BadgeKind.Unknown, state.OverallQualityBand);
        Assert.Equal(SemanticTone.Unknown, state.Tone);
        Assert.Empty(state.Claims);
    }

    [Fact]
    public void CasePresentationState_Initial_Uses_Typed_Composition_Profile_Invariant_166()
    {
        var state = CasePresentationState.Initial;

        // Invariant 166: Typed report profile projection rather than magic string
        Assert.Equal(ReportCompositionProfile.Complaint, state.SelectedProfile);
        Assert.NotEmpty(state.PreviewText);
        Assert.Empty(state.UserStatements);
    }

    [Fact]
    public void SpeedPresentationState_Initial_Complies_With_Unknown_Semantics_Invariant_159_And_167()
    {
        var state = SpeedPresentationState.Initial;

        // Invariant 159 & 167: Unmeasured speed fields must be null/unmeasured, not false active defaults
        Assert.Null(state.RequestedInterface);
        Assert.Null(state.ObservedPath);
        Assert.Null(state.PathAgreement);
        Assert.Null(state.TunnelIndication);
        Assert.Null(state.DownloadThroughputMbps);
        Assert.Null(state.UploadThroughputMbps);
        Assert.False(state.Ran);
        Assert.Contains("Nije pokrenuto", state.DownloadThroughputText);
        Assert.DoesNotContain("0 Mbps", state.DownloadThroughputText);
    }

    [Fact]
    public void ShellPresentationState_Initial_Is_Initialized_Cleanly()
    {
        var state = ShellPresentationState.Initial;

        Assert.False(state.IsRunning);
        Assert.Null(state.Fault);
        Assert.Equal(ShellTab.Monitor, state.ActiveTab);
        Assert.Equal(6, state.Durations.Count);
        Assert.Equal(600, state.TimelineCapacity);
        Assert.False(state.SurvivesClosing);
        Assert.Equal("Spremno", state.StateLabel);
        Assert.Equal(SemanticTone.Neutral, state.Tone);
        Assert.True(state.CanScheduleSpeed);
        Assert.Empty(state.Timeline);
        Assert.Empty(state.Latency);
        Assert.False(state.IsUpdateBannerVisible);
        Assert.NotNull(state.Monitor);
        Assert.NotNull(state.Evidence);
        Assert.NotNull(state.Case);
        Assert.NotNull(state.Speed);
    }

    [Fact]
    public void TimelineSlice_And_LatencyPoint_Are_Platform_Neutral_Record_Structs()
    {
        var slice = new TimelineSlice(Model.Severity.Degraded);
        Assert.Equal(Model.Severity.Degraded, slice.Severity);
        Assert.Equal(SemanticTone.Warning, slice.Tone);

        var point = new LatencyPoint(10.5, 20.0, 35.2);
        Assert.True(point.HasData);
        Assert.Equal(10.5, point.Minimum);
        Assert.Equal(20.0, point.Average);
        Assert.Equal(35.2, point.Maximum);

        var emptyPoint = new LatencyPoint(null, null, null);
        Assert.False(emptyPoint.HasData);
    }
}
