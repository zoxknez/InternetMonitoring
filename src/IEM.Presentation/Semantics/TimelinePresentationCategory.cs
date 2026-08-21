namespace IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral categorization of timeline intervals.
/// Invariant 161: NON_OBSERVABLE_HOST_INTERVAL_IS_NEVER_VISUALIZED_AS_NETWORK_OUTAGE.
/// </summary>
public enum TimelinePresentationCategory
{
    ActiveMonitoring,
    InterruptionObserved,
    HostSuspended,
    ClockAdjustment,
    BootBoundary,
    ProbeDegraded,
    Planned,
    NotObserved,
    Unknown
}
