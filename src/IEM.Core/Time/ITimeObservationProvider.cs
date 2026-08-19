namespace IEM.Core.Time;

/// <summary>
/// Platform-neutral contract for sampling clock and boot continuity facts.
/// Invariants:
/// 111. UNAVAILABLE_TIME_SOURCE_NEVER_SYNTHESIZES_TIME_OR_CONTINUITY
/// 113. PLATFORM_TIME_SOURCE_IS_PROVENANCE_NOT_TEMPORAL_SEMANTICS
/// </summary>
public interface ITimeObservationProvider
{
    string ProviderId { get; }
    string ProviderVersion { get; }

    BootObservation CaptureBootObservation(string? knownBootInstanceId = null);

    ClockSample CaptureClockSample(string bootInstanceId);
}
