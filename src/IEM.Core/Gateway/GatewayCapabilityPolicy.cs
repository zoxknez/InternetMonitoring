using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.Gateway;

/// <summary>
/// Versioned policy governing gateway capability learning and behavioral assessment thresholds.
/// Invariant 52: INITIAL_LEARNING_WINDOW_NEVER_FREEZES_UNKNOWN_AS_UNSUPPORTED.
/// </summary>
public sealed record GatewayCapabilityPolicy
{
    public int PolicyVersion { get; init; } = 1;

    /// <summary>Initial bootstrap learning window duration.</summary>
    public TimeSpan InitialLearningWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Number of positive observations required to establish ObservedSupported state.</summary>
    public int MinimumPositiveObservations { get; init; } = 1;

    /// <summary>Consecutive failure windows required before declaring a previously observed capability missing.</summary>
    public int MissingCapabilityConsecutiveWindows { get; init; } = 2;

    /// <summary>Consecutive successful windows required for full recovery of a missing capability.</summary>
    public int RecoveryWindowsRequired { get; init; } = 2;

    public string PolicyHash => ComputePolicyHash();

    private string ComputePolicyHash()
    {
        var descriptor = $"v={PolicyVersion};init_win_s={InitialLearningWindow.TotalSeconds};min_pos={MinimumPositiveObservations};miss_win={MissingCapabilityConsecutiveWindows};rec_win={RecoveryWindowsRequired}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }

    public static readonly GatewayCapabilityPolicy Default = new();
}
