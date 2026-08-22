namespace IEM.Presentation.States;

using System.Collections.Immutable;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Presentation.Models;
using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral, immutable presentation state for top-level shell dashboard metrics, banner, and child tabs.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 156. SWITCHING_TABS_NEVER_CHANGES_MEASUREMENT_EXECUTION_STATE
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// </summary>
public sealed record ShellPresentationState(
    bool IsRunning,
    string? Fault,
    ShellTab ActiveTab,
    DurationChoice SelectedDuration,
    ImmutableArray<DurationChoice> Durations,
    int TimelineCapacity,
    bool SurvivesClosing,
    string BackgroundClaimLabel,
    string BackgroundClaimDetail,
    string RestartClaimLabel,
    string RestartClaimDetail,
    string HostDescription,
    SessionVerdict? Verdict,
    string StateLabel,
    string StateExplanation,
    ConnectivityPresentationState Connectivity,
    Severity? CurrentSeverity,
    SemanticTone Tone,
    string LatencyText,
    string ElapsedText,
    string AvailabilityText,
    string UpstreamAvailabilityText,
    string DowntimeText,
    string LocalDowntimeText,
    string UnreachableTargetsText,
    bool ShowWirelessWarning,
    string StatusPill,
    double ProgressPercent,
    bool HasProgress,
    string MediumText,
    ImmutableArray<MetricPresentationItem> Metrics,
    string EndsAtText,
    ImmutableArray<ProbePresentationState> Probes,
    string RemainingValue,
    string FactsLine,
    string? CaseText,
    ImmutableArray<TimelineSlice> Timeline,
    ImmutableArray<LatencyPoint> Latency,
    string SpeedScheduleAmount,
    string SelectedSpeedScheduleUnit,
    ImmutableArray<string> SpeedScheduleUnits,
    string ContractedRateText,
    string? SpeedStatus,
    bool SpeedBusy,
    bool IsUpdateBannerVisible,
    string UpdateVersionText,
    string UpdateSummaryText,
    string UpdateReleaseNotesUrl,
    string UpdateDownloadUrl,
    MonitorPresentationState Monitor,
    EvidencePresentationState Evidence,
    CasePresentationState Case,
    SpeedPresentationState Speed)
{
    /// <summary>
    /// Derived from Connectivity: null when unobserved, true when online/degraded, false when outage.
    /// Invariant 159: Unobserved connectivity is never represented as true/false success.
    /// </summary>
    public bool? IsOnline => Connectivity switch
    {
        ConnectivityPresentationState.Online => true,
        ConnectivityPresentationState.Degraded => true,
        ConnectivityPresentationState.Outage => false,
        _ => null,
    };

    /// <summary>
    /// Derived strictly from !SpeedBusy to prevent impossible contradicting scheduling states.
    /// </summary>
    public bool CanScheduleSpeed => !SpeedBusy;

    public static ImmutableArray<DurationChoice> DefaultDurations { get; } =
    [
        new("1 sat", TimeSpan.FromHours(1), "brza provera"),
        new("6 sati", TimeSpan.FromHours(6), "popodne ili veče"),
        new("24 sata", TimeSpan.FromHours(24), "ceo dan i noć"),
        new("48 sati", TimeSpan.FromHours(48), "dva dana i dve noći"),
        new("72 sata", TimeSpan.FromHours(72), "vikend"),
        new("Do zaustavljanja", Timeout.InfiniteTimeSpan, "zaustavljate ga ručno"),
    ];

    public static ImmutableArray<string> DefaultSpeedUnits { get; } = ["minuta", "sati"];

    public static ShellPresentationState Initial { get; } = new(
        IsRunning: false,
        Fault: null,
        ActiveTab: ShellTab.Monitor,
        SelectedDuration: DefaultDurations[3],
        Durations: DefaultDurations,
        TimelineCapacity: 600,
        SurvivesClosing: false,
        BackgroundClaimLabel: "Radi dok je prozor otvoren",
        BackgroundClaimDetail: "Nadzor radi u ovom prozoru. Ako ga zatvorite, test se zaustavlja i sesija se zatvara.",
        RestartClaimLabel: "Ne preživljava restart",
        RestartClaimDetail: "Servis nije instaliran, pa restart računara prekida test. Za dvodnevni nadzor instalirajte servis.",
        HostDescription: "Nadzor radi u ovom prozoru. Ako ga zatvorite, test se zaustavlja. Za dvodnevni test instalirajte servis.",
        Verdict: null,
        StateLabel: "Spremno",
        StateExplanation: "Nadzor nije pokrenut.",
        Connectivity: ConnectivityPresentationState.Unknown,
        CurrentSeverity: null,
        Tone: SemanticTone.Unknown,
        LatencyText: "-",
        ElapsedText: "0 sekundi",
        AvailabilityText: "—",
        UpstreamAvailabilityText: "—",
        DowntimeText: "—",
        LocalDowntimeText: "—",
        UnreachableTargetsText: "nije mereno",
        ShowWirelessWarning: false,
        StatusPill: "NADZOR NIJE POKRENUT",
        ProgressPercent: 0d,
        HasProgress: false,
        MediumText: "nepoznato",
        Metrics: ImmutableArray<MetricPresentationItem>.Empty,
        EndsAtText: "Test se završava...",
        Probes: ImmutableArray<ProbePresentationState>.Empty,
        RemainingValue: "bez roka",
        FactsLine: string.Empty,
        CaseText: null,
        Timeline: ImmutableArray<TimelineSlice>.Empty,
        Latency: ImmutableArray<LatencyPoint>.Empty,
        SpeedScheduleAmount: string.Empty,
        SelectedSpeedScheduleUnit: "minuta",
        SpeedScheduleUnits: DefaultSpeedUnits,
        ContractedRateText: string.Empty,
        SpeedStatus: null,
        SpeedBusy: false,
        IsUpdateBannerVisible: false,
        UpdateVersionText: string.Empty,
        UpdateSummaryText: "Dostupna su nova poboljšanja i ispravke.",
        UpdateReleaseNotesUrl: string.Empty,
        UpdateDownloadUrl: string.Empty,
        Monitor: MonitorPresentationState.Initial,
        Evidence: EvidencePresentationState.Initial,
        Case: CasePresentationState.Initial,
        Speed: SpeedPresentationState.Initial);
}
