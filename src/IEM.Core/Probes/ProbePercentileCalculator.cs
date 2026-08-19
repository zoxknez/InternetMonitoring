namespace IEM.Core.Probes;

/// <summary>
/// Deterministic percentile calculator using the standard Nearest-Rank method.
/// <para>
/// Algorithm: rank = ceil((P / 100) * N), bounded to [1, N].
/// Array index = rank - 1.
/// Every result explicitly carries its sample size N.
/// </para>
/// </summary>
public static class ProbePercentileCalculator
{
    public const string MethodName = "NearestRank";

    /// <summary>
    /// Computes exact percentile value for a non-empty collection of numbers.
    /// </summary>
    /// <param name="values">Collection of measured values.</param>
    /// <param name="percentile">Percentile rank between 0 and 100 (e.g. 50 for median, 95 for P95).</param>
    /// <exception cref="ArgumentException">Thrown if collection is empty or percentile is out of (0, 100].</exception>
    public static double ComputePercentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            throw new ArgumentException("Kolekcija vrednosti za računanje percentila ne sme biti prazna.", nameof(values));
        }

        if (percentile <= 0 || percentile > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentil mora biti u opsegu (0, 100].");
        }

        var sorted = values.OrderBy(v => v).ToList();
        var n = sorted.Count;

        // Nearest-rank formula: k = ceil(P/100 * N)
        var rank = (int)Math.Ceiling((percentile / 100.0) * n);
        rank = Math.Clamp(rank, 1, n);

        return sorted[rank - 1];
    }
}
