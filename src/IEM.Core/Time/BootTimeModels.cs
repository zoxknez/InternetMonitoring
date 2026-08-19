namespace IEM.Core.Time;

/// <summary>
/// Authoritative fact capturing boot markers and dual-elapsed timers at an instant.
/// Invariants:
/// 97. SUSPEND_TIME_IS_NEVER_INTERPRETED_AS_NETWORK_DOWNTIME
/// 98. WALL_CLOCK_NEVER_DEFINES_ELAPSED_DURATION
/// 99. MONOTONIC_TIME_IS_NEVER_PRESENTED_AS_ABSOLUTE_UTC
/// 102. BOOT_OBSERVATION_HISTORY_IS_APPEND_ONLY
/// </summary>
public sealed record BootObservation(
    string ObservationId,
    string BootInstanceId,
    string BootIdentityBasis,
    DateTimeOffset CapturedUtc,
    string WallClockSource,
    long MonotonicTimestamp,
    long MonotonicFrequency,
    string MonotonicSource,
    TimeSpan BootElapsedIncludingSuspend,
    TimeSpan ActiveElapsedExcludingSuspend,
    string ProviderId,
    string ProviderVersion);

/// <summary>
/// Operational factual clock sample.
/// Invariant 103: CLOCK_DISCONTINUITY_REQUIRES_COMPARISON_WITH_AN_INDEPENDENT_ELAPSED_TIME_SOURCE.
/// </summary>
public sealed record ClockSample(
    string SampleId,
    string BootInstanceId,
    DateTimeOffset CapturedUtc,
    long MonotonicTimestamp,
    long MonotonicFrequency,
    TimeSpan BootElapsedIncludingSuspend,
    TimeSpan ActiveElapsedExcludingSuspend);

public enum ClockContinuityState
{
    Unknown,
    Continuous,
    ForwardAdjustmentObserved,
    BackwardAdjustmentObserved,
    SuspendIntervalObserved,
    BootBoundaryObserved,
    CounterDiscontinuity,
    Ambiguous,
}

/// <summary>
/// Inferred clock continuity assessment between two chronological samples (INFERENCE).
/// Invariants:
/// 98. WALL_CLOCK_NEVER_DEFINES_ELAPSED_DURATION
/// 104. CLOCK_DISCONTINUITY_NEVER_IDENTIFIES_AN_UNPROVEN_ADJUSTMENT_CAUSE
/// 107. MONOTONIC_DURATION_IS_NEVER_COMPUTED_ACROSS_BOOT_INSTANCES
/// 108. HOST_SUSPENSION_GAP_NEVER_CONTRIBUTES_NETWORK_OUTAGE_DURATION
/// </summary>
public sealed record ClockContinuityAssessment(
    string PreviousSampleRef,
    string CurrentSampleRef,
    ClockContinuityState State,
    TimeSpan WallClockDelta,
    TimeSpan MonotonicDelta,
    TimeSpan ActiveElapsedDelta,
    TimeSpan BootElapsedDelta,
    TimeSpan Divergence,
    TimeSpan SuspendDuration,
    IReadOnlyList<string> ReasonCodes,
    string InterpretationRefId);

public enum BootContinuityState
{
    Established,
    Continued,
    Changed,
    Ambiguous,
}

/// <summary>
/// Inferred boot identity assessment (INFERENCE).
/// Invariants:
/// 100. BOOT_CONTINUITY_IS_NEVER_ASSUMED_WHEN_IDENTITY_EVIDENCE_IS_AMBIGUOUS
/// 101. BOOT_IDENTITY_CHANGE_SPLITS_TIME_CONTINUITY
/// 109. SUSPEND_RESUME_NEVER_CREATES_A_NEW_BOOT_INSTANCE_BY_DEFAULT
/// 110. SERVICE_RESTART_NEVER_IMPLIES_HOST_REBOOT
/// </summary>
public sealed record BootIdentityAssessment(
    string BootInstanceId,
    BootContinuityState State,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> SourceEvidenceRefs,
    string InterpretationRefId);

/// <summary>
/// Canonical composite time tuple for new evidence events.
/// Invariants:
/// 105. EVENT_ORDER_WITHIN_A_BOOT_IS_NEVER_DERIVED_FROM_WALL_CLOCK_ALONE
/// 106. CLOCK_ADJUSTMENT_NEVER_REWRITES_PREVIOUS_EVENT_TIMESTAMPS
/// </summary>
public sealed record EvidenceTime(
    DateTimeOffset CapturedUtc,
    string BootInstanceId,
    long MonotonicTimestamp,
    long MonotonicFrequency,
    string? ClockContinuityRef = null);
