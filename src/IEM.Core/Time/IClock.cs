namespace IEM.Core.Time;

/// <summary>
/// Abstraction over the three clocks the evidence model depends on.
/// <para>
/// Wall-clock time is recorded for display and for correlation with the
/// operator's own logs, but it is never used to compute a duration. Every
/// elapsed time comes from the monotonic clock, so an NTP resync or a manual
/// clock change cannot silently lengthen or shorten a recorded outage.
/// </para>
/// </summary>
public interface IClock
{
    /// <summary>Wall-clock time. Subject to NTP correction and user adjustment.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Monotonic counter that never decreases and is unaffected by clock adjustments.
    /// Only differences between two readings are meaningful.
    /// </summary>
    long MonotonicTicks { get; }

    /// <summary>Frequency of <see cref="MonotonicTicks"/>.</summary>
    long MonotonicTicksPerSecond { get; }

    /// <summary>
    /// Time since the machine booted. A decrease across samples means the machine
    /// rebooted, which distinguishes a reboot gap from a sleep gap.
    /// </summary>
    TimeSpan SystemUptime { get; }
}
