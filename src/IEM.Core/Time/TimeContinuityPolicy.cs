using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.Time;

/// <summary>
/// Versioned policy governing clock continuity detection, tolerances, and suspend intervals.
/// Invariants:
/// 103. CLOCK_DISCONTINUITY_REQUIRES_COMPARISON_WITH_AN_INDEPENDENT_ELAPSED_TIME_SOURCE
/// 112. TIME_CONTINUITY_IS_REBUILDABLE_FROM_PERSISTED_TEMPORAL_EVIDENCE
/// </summary>
public sealed record TimeContinuityPolicy
{
    public int PolicyVersion { get; init; } = 1;

    /// <summary>Tolerance for wall-clock vs monotonic divergence before classifying adjustment.</summary>
    public TimeSpan WallVsMonotonicTolerance { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Tolerance for boot-elapsed vs active-elapsed difference to detect suspend.</summary>
    public TimeSpan SuspendDetectionTolerance { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Tolerance for monotonic counter regression (normally 0).</summary>
    public TimeSpan CounterRegressionTolerance { get; init; } = TimeSpan.Zero;

    public string PolicyHash => ComputePolicyHash();

    private string ComputePolicyHash()
    {
        var descriptor = $"v={PolicyVersion};tol_wall={WallVsMonotonicTolerance.TotalMilliseconds};tol_susp={SuspendDetectionTolerance.TotalMilliseconds}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }

    public static readonly TimeContinuityPolicy Default = new();
}
