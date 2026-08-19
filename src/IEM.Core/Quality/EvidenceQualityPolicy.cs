using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.Quality;

/// <summary>
/// Versioned policy defining weights, thresholds, hard gates, and band caps for evidence quality evaluation.
/// Invariants:
/// 121. CRITICAL_QUALITY_FAILURE_CANNOT_BE_AVERAGED_AWAY
/// 124. INVALID_PACKAGE_INTEGRITY_CANNOT_BE_AVERAGED_AWAY_BY_STRONG_MEASUREMENTS
/// 127. EVIDENCE_QUALITY_POLICY_IS_VERSIONED_AND_HASHED
/// </summary>
public sealed record EvidenceQualityPolicy
{
    public int PolicyVersion { get; init; } = 1;

    public int MinFullCoverageBasisPointsForStrong { get; init; } = 7500; // 75%
    public int MinTotalValidCoverageBasisPointsForStrong { get; init; } = 8500; // 85%
    public int MinTotalValidCoverageBasisPointsForModerate { get; init; } = 6000; // 60%
    public int MinTotalValidCoverageBasisPointsForLimited { get; init; } = 3500; // 35%

    public string PolicyHash => ComputePolicyHash();

    private string ComputePolicyHash()
    {
        var descriptor = $"v={PolicyVersion};str_f={MinFullCoverageBasisPointsForStrong};str_t={MinTotalValidCoverageBasisPointsForStrong};mod_t={MinTotalValidCoverageBasisPointsForModerate};lim_t={MinTotalValidCoverageBasisPointsForLimited}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }

    public static readonly EvidenceQualityPolicy Default = new();
}
