namespace IEM.Presentation.Models;

using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral timeline entry representing an active monitoring interval, suspension, or outage.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 161. NON_OBSERVABLE_HOST_INTERVAL_IS_NEVER_VISUALIZED_AS_NETWORK_OUTAGE
/// </summary>
public sealed record MonitorTimelinePresentationItem(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    TimelinePresentationCategory Category,
    string CategoryLabel,
    string Description)
{
    /// <summary>
    /// Derived strictly from Category: true only for HostSuspended intervals.
    /// Invariant 161: Non-observable intervals must never be visualized as network outage.
    /// </summary>
    public bool IsSuspend => Category == TimelinePresentationCategory.HostSuspended;

    /// <summary>
    /// Derived strictly from Category: true only for observed interruption intervals.
    /// </summary>
    public bool IsOutage => Category == TimelinePresentationCategory.InterruptionObserved;
}
