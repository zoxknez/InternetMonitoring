namespace IEM.Presentation.Contracts;

using System.Collections.Immutable;
using IEM.Presentation.Models;
using IEM.Presentation.Semantics;

/// <summary>
/// User interaction controls and configuration for the shell dashboard.
/// Invariants:
/// 156. SWITCHING_TABS_NEVER_CHANGES_MEASUREMENT_EXECUTION_STATE
/// </summary>
public sealed record ShellInteractionState(
    bool IsRunning,
    string? Fault,
    ShellTab ActiveTab,
    DurationChoice SelectedDuration,
    ImmutableArray<DurationChoice> Durations,
    int TimelineCapacity,
    string SpeedScheduleAmount,
    string SelectedSpeedScheduleUnit,
    ImmutableArray<string> SpeedScheduleUnits,
    string ContractedRateText,
    string? SpeedStatus,
    bool SpeedBusy);
