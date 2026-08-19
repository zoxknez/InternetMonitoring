namespace IEM.Core.Model;

/// <summary>
/// Proves whether the kernel routing/link path remained unchanged during the probe execution window [T0, T1].
/// Invariants 247 &amp; §5.8 (TOCTOU route semantics).
/// </summary>
public enum PathContinuity
{
    /// <summary>Observer not active, polling fallback, or pre-3.1 behavior. Standard baseline attribution.</summary>
    Unknown = 0,

    /// <summary>Observer is Live and verified zero route/link change events in [T0, T1] matching the path.</summary>
    Held = 1,

    /// <summary>Observer detected a route, link, or address event during probe execution interval [T0, T1]. Attribution quality reduced.</summary>
    ChangedDuringExecution = 2,
}
