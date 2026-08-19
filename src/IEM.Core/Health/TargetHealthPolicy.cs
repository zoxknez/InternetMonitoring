using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.Health;

/// <summary>
/// Versioned policy governing target health evaluation thresholds, hysteresis, and weighting.
/// </summary>
public sealed record TargetHealthPolicy
{
    public int PolicyVersion { get; init; } = 1;

    /// <summary>Minimum eligible samples in a window required to evaluate health.</summary>
    public int MinEligibleSamplesPerWindow { get; init; } = 5;

    /// <summary>Maximum NoReplyRatio considered completely healthy (e.g. 0.05 = 5%).</summary>
    public double HealthyLossThreshold { get; init; } = 0.05;

    /// <summary>NoReplyRatio threshold above which degradation is registered (e.g. 0.30 = 30%).</summary>
    public double DegradedLossThreshold { get; init; } = 0.30;

    /// <summary>Consecutive bad windows required to transition from Healthy to Degraded.</summary>
    public int FailureWindowsToDegrade { get; init; } = 2;

    /// <summary>Consecutive failed/silent windows required to transition to Unresponsive.</summary>
    public int FailureWindowsToUnresponsive { get; init; } = 3;

    /// <summary>Consecutive clean windows required for a full recovery (hysteresis).</summary>
    public int RecoveryWindowsRequired { get; init; } = 3;

    public string PolicyHash => ComputePolicyHash();

    private string ComputePolicyHash()
    {
        var descriptor = $"v={PolicyVersion};min_n={MinEligibleSamplesPerWindow};h_loss={HealthyLossThreshold:F3};deg_loss={DegradedLossThreshold:F3};fail_deg={FailureWindowsToDegrade};fail_unresp={FailureWindowsToUnresponsive};rec_win={RecoveryWindowsRequired}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }

    public static readonly TargetHealthPolicy Default = new();
}
