using IEM.Core.Time;

namespace IEM.Core.Tests;

/// <summary>
/// A clock the test drives by hand. Wall time, monotonic time and uptime move
/// independently, which is the whole point: it can reproduce an NTP correction or a
/// reboot without waiting for one to happen.
/// </summary>
internal sealed class ManualClock : IClock
{
    public const long TicksPerSecondValue = 10_000_000;

    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    public long MonotonicTicks { get; private set; }

    public long MonotonicTicksPerSecond => TicksPerSecondValue;

    public TimeSpan SystemUptime { get; private set; } = TimeSpan.FromHours(5);

    /// <summary>Normal passage of time: every clock advances together.</summary>
    public void Advance(TimeSpan amount)
    {
        UtcNow += amount;
        MonotonicTicks += (long)(amount.TotalSeconds * TicksPerSecondValue);
        SystemUptime += amount;
    }

    /// <summary>An NTP correction or a user changing the clock: only wall time moves.</summary>
    public void JumpWallClock(TimeSpan offset) => UtcNow += offset;

    /// <summary>A reboot: uptime resets and the monotonic counter starts over.</summary>
    public void Reboot(TimeSpan wallTimeSpentDown)
    {
        UtcNow += wallTimeSpentDown;
        MonotonicTicks = 0;
        SystemUptime = TimeSpan.Zero;
    }
}
