using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.ProbeHealth;

/// <summary>
/// Versioned policy governing probe failure domain classification and probe health thresholds.
/// Invariant 58: NATIVE_ERROR_CODE_IS_EVIDENCE_INPUT_NOT_FINAL_SEMANTIC_CLASSIFICATION.
/// </summary>
public sealed record ProbeFailurePolicy
{
    public int PolicyVersion { get; init; } = 1;

    /// <summary>Consecutive local/internal failures required to mark probe engine Degraded.</summary>
    public int LocalFailuresToDegrade { get; init; } = 2;

    /// <summary>Consecutive local/internal failures required to mark probe engine Unusable.</summary>
    public int LocalFailuresToUnusable { get; init; } = 4;

    /// <summary>Consecutive successful executions required to recover a degraded/unusable probe engine.</summary>
    public int RecoveryAttemptsRequired { get; init; } = 3;

    public string PolicyHash => ComputePolicyHash();

    private string ComputePolicyHash()
    {
        var descriptor = $"v={PolicyVersion};fail_deg={LocalFailuresToDegrade};fail_unusable={LocalFailuresToUnusable};rec={RecoveryAttemptsRequired}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }

    public static readonly ProbeFailurePolicy Default = new();
}
