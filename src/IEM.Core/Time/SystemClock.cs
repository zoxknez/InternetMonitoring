using System.Diagnostics;

namespace IEM.Core.Time;

/// <summary>Production <see cref="IClock"/> backed by the OS.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long MonotonicTicks => Stopwatch.GetTimestamp();

    public long MonotonicTicksPerSecond => Stopwatch.Frequency;

    // Environment.TickCount64 is milliseconds since boot and does not wrap.
    public TimeSpan SystemUptime => TimeSpan.FromMilliseconds(Environment.TickCount64);
}
