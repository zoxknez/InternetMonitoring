namespace IEM.Presentation.Models;

/// <summary>
/// Latency spread in a single slice of session time.
/// </summary>
/// <param name="Minimum">Fastest response in this slice (ms), or null if nothing answered.</param>
/// <param name="Average">Average response in this slice (ms), or null if nothing answered.</param>
/// <param name="Maximum">Slowest response in this slice (ms), or null if nothing answered.</param>
public readonly record struct LatencyPoint(double? Minimum, double? Average, double? Maximum)
{
    public bool HasData => Minimum.HasValue || Average.HasValue || Maximum.HasValue;
}
