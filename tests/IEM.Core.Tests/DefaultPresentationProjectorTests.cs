using System.Collections.Immutable;
using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Presentation;
using IEM.Presentation.Contracts;
using IEM.Presentation.Models;
using IEM.Presentation.Semantics;
using IEM.Presentation.States;

namespace IEM.Core.Tests;

public sealed class DefaultPresentationProjectorTests
{
    private readonly DefaultPresentationProjector _projector = new();

    [Fact]
    public void Unobserved_live_state_never_becomes_success_zero_or_one_hundred_percent()
    {
        var state = _projector.ProjectShell(CreateInput(MonitorSnapshot.Empty, isRunning: false));

        Assert.Equal(ConnectivityPresentationState.Unknown, state.Connectivity);
        Assert.Null(state.CurrentSeverity);
        Assert.Equal("—", state.AvailabilityText);
        Assert.Equal("—", state.UpstreamAvailabilityText);
        Assert.Equal("—", state.RemainingValue);
        Assert.Equal("nije mereno", state.UnreachableTargetsText);
        Assert.Null(state.Verdict);
        Assert.Empty(state.Probes);
    }

    [Fact]
    public void Observed_outage_preserves_domain_severity_and_authoritative_values()
    {
        var live = new MonitorSnapshot
        {
            SessionId = "session-1",
            SampleCount = 7,
            CurrentState = NetworkState.CpeUpstreamUnreachable,
            AvailabilityPercent = 92.5,
            UpstreamAvailabilityPercent = 95,
            UnreachableTargetShare = 66.666,
            MonitoredTime = TimeSpan.FromMinutes(15),
            UpstreamIncidentCount = 2,
            IncidentCount = 2,
            Gateway = new ProbeTally(1, 1),
            ExternalIcmp = new ProbeTally(3, 0),
        };

        var state = _projector.ProjectShell(CreateInput(live, isRunning: true));

        Assert.Equal(ConnectivityPresentationState.Outage, state.Connectivity);
        Assert.Equal(Severity.Outage, state.CurrentSeverity);
        Assert.Equal(SemanticTone.Bad, state.Tone);
        Assert.Contains("92,5", state.AvailabilityText, StringComparison.Ordinal);
        Assert.Contains("66,7", state.UnreachableTargetsText, StringComparison.Ordinal);
        Assert.Equal(5, state.Probes.Length);
        Assert.Equal(SemanticTone.Neutral, state.Probes.Single(item => item.Name == "DNS").Tone);
        Assert.Equal(SemanticTone.Bad, state.Probes.Single(item => item.Name == "Ping").Tone);
    }

    [Fact]
    public void Refused_speed_measurement_cannot_render_zero_throughput()
    {
        var state = _projector.ProjectSpeed(new SpeedProjectionInput(
            CreateSnapshot(),
            new SpeedExecutionFacts.Refused(
                "ObserveSystemPath",
                "eth0",
                "NoRouteFromRequestedInterface")));

        Assert.False(state.Ran);
        Assert.True(state.IsRefused);
        Assert.DoesNotContain("0 Mbps", state.DownloadThroughputText, StringComparison.Ordinal);
        Assert.Contains("odbijeno", state.DownloadThroughputText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Active_deadline_is_anchored_to_authoritative_session_start()
    {
        var startedUtc = DateTimeOffset.Parse("2026-08-29T01:00:00Z");
        var live = new MonitorSnapshot
        {
            StartedUtc = startedUtc,
            PlannedDuration = TimeSpan.FromHours(3),
            Elapsed = TimeSpan.FromMinutes(30),
        };

        var state = _projector.ProjectShell(CreateInput(live, isRunning: true));

        Assert.Contains(SerbianText.DateTime(startedUtc + TimeSpan.FromHours(3)), state.EndsAtText, StringComparison.Ordinal);
        Assert.Equal(SerbianText.Duration(TimeSpan.FromHours(2.5)), state.RemainingValue);
    }

    private static ShellProjectionInput CreateInput(MonitorSnapshot live, bool isRunning) => new(
        CreateSnapshot(isRunning ? SessionRuntimeState.Monitoring : SessionRuntimeState.Idle),
        new ShellInteractionState(
            isRunning,
            Fault: null,
            ShellTab.Monitor,
            ShellPresentationState.DefaultDurations[0],
            ShellPresentationState.DefaultDurations,
            TimelineCapacity: 600,
            SpeedScheduleAmount: string.Empty,
            SelectedSpeedScheduleUnit: "minuta",
            ShellPresentationState.DefaultSpeedUnits,
            ContractedRateText: string.Empty,
            SpeedStatus: null,
            SpeedBusy: false),
        new HostPresentationFacts(
            SurvivesClosing: false,
            "Radi dok je prozor otvoren",
            "Prenosivi režim",
            "Ne nastavlja posle restarta",
            "Prenosivi režim",
            "Prenosivi režim"),
        HistoryPresentationState.Empty,
        UpdatePresentationState.Hidden,
        IEM.Presentation.Contracts.CaseWorkspaceState.Empty,
        SpeedExecutionFacts.None,
        live);

    private static PresentationSnapshot CreateSnapshot(
        SessionRuntimeState runtimeState = SessionRuntimeState.Idle) => new(
        "snapshot-1",
        string.Empty,
        1,
        DateTimeOffset.Parse("2026-08-29T10:00:00Z"),
        runtimeState,
        ServiceConnectionStatus.ServiceUnavailable,
        Analysis: null,
        CanonicalReport: null,
        SourceRefs: Array.Empty<string>());
}
