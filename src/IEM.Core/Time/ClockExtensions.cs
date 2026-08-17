namespace IEM.Core.Time;

public static class ClockExtensions
{
    /// <summary>
    /// Converts a difference between two <see cref="IClock.MonotonicTicks"/> readings
    /// into a <see cref="TimeSpan"/>. This is the only sanctioned way to measure a
    /// duration anywhere in the evidence pipeline.
    /// </summary>
    public static TimeSpan MonotonicElapsed(this IClock clock, long fromTicks, long toTicks)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var delta = toTicks - fromTicks;
        return TimeSpan.FromSeconds((double)delta / clock.MonotonicTicksPerSecond);
    }

    /// <summary>Monotonic time elapsed between <paramref name="fromTicks"/> and now.</summary>
    public static TimeSpan MonotonicElapsedSince(this IClock clock, long fromTicks)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.MonotonicElapsed(fromTicks, clock.MonotonicTicks);
    }
}
