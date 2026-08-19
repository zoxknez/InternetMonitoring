namespace IEM.Core.Probes;

/// <summary>
/// Result of delay variation calculation.
/// Invariant 36: DELAY_VARIATION_ALWAYS_NAMES_ITS_METHOD.
/// </summary>
public sealed record DelayVariationResult(
    string Method,
    int SampleCount,
    double? MedianMs,
    double? P95Ms);

/// <summary>
/// Calculates Round-Trip Delay Variation across consecutive successful probe replies.
/// Invariant 36: DELAY_VARIATION_ALWAYS_NAMES_ITS_METHOD.
/// </summary>
public static class RoundTripDelayVariationCalculator
{
    public const string MethodName = "ConsecutiveReplyAbsoluteDifference";

    /// <summary>
    /// Computes delay variation from an ordered sequence of successful RTT measurements.
    /// </summary>
    public static DelayVariationResult Compute(IReadOnlyList<double> chronologicalRtts)
    {
        ArgumentNullException.ThrowIfNull(chronologicalRtts);

        if (chronologicalRtts.Count < 2)
        {
            return new DelayVariationResult(MethodName, 0, null, null);
        }

        var variations = new List<double>(chronologicalRtts.Count - 1);
        for (var i = 1; i < chronologicalRtts.Count; i++)
        {
            var diff = Math.Abs(chronologicalRtts[i] - chronologicalRtts[i - 1]);
            variations.Add(diff);
        }

        var median = ProbePercentileCalculator.ComputePercentile(variations, 50);
        var p95 = ProbePercentileCalculator.ComputePercentile(variations, 95);

        return new DelayVariationResult(MethodName, variations.Count, median, p95);
    }
}
