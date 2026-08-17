using IEM.Core.Time;

namespace IEM.Core.Tests;

public sealed class ClockIntegrityMonitorTests
{
    [Fact]
    public void First_reading_has_nothing_to_compare_against()
    {
        var clock = new ManualClock();
        var monitor = new ClockIntegrityMonitor();

        Assert.Null(monitor.Observe(clock));
    }

    [Fact]
    public void Normal_passage_of_time_is_not_an_anomaly()
    {
        var clock = new ManualClock();
        var monitor = new ClockIntegrityMonitor();
        monitor.Observe(clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        var observation = monitor.Observe(clock)!;

        Assert.Equal(ClockAnomaly.None, observation.Anomaly);
        Assert.False(observation.IsAnomalous);
    }

    [Fact]
    public void Wall_clock_correction_is_detected_and_measured()
    {
        // An operator could otherwise argue the timestamps were edited. Recording the
        // correction, with its size, closes that off.
        var clock = new ManualClock();
        var monitor = new ClockIntegrityMonitor();
        monitor.Observe(clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.JumpWallClock(TimeSpan.FromHours(2));
        var observation = monitor.Observe(clock)!;

        Assert.Equal(ClockAnomaly.WallClockJump, observation.Anomaly);
        Assert.Equal(TimeSpan.FromHours(2), observation.Skew);
    }

    [Fact]
    public void Backward_wall_clock_correction_is_also_detected()
    {
        var clock = new ManualClock();
        var monitor = new ClockIntegrityMonitor();
        monitor.Observe(clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.JumpWallClock(TimeSpan.FromMinutes(-30));
        var observation = monitor.Observe(clock)!;

        Assert.Equal(ClockAnomaly.WallClockJump, observation.Anomaly);
        Assert.True(observation.Skew < TimeSpan.Zero);
    }

    [Fact]
    public void Elapsed_time_stays_correct_across_a_wall_clock_jump()
    {
        // The reason durations never come from the wall clock.
        var clock = new ManualClock();
        var monitor = new ClockIntegrityMonitor();
        monitor.Observe(clock);

        clock.Advance(TimeSpan.FromSeconds(8));
        clock.JumpWallClock(TimeSpan.FromHours(2));
        var observation = monitor.Observe(clock)!;

        Assert.Equal(TimeSpan.FromSeconds(8), observation.MonotonicDelta);
        Assert.NotEqual(observation.WallDelta, observation.MonotonicDelta);
    }

    [Fact]
    public void Reboot_is_reported_as_a_reboot_rather_than_a_clock_jump()
    {
        // A reboot resets the monotonic counter too, so the skew reading is meaningless
        // and would otherwise be misreported.
        var clock = new ManualClock();
        var monitor = new ClockIntegrityMonitor();
        monitor.Observe(clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.Reboot(TimeSpan.FromMinutes(3));
        var observation = monitor.Observe(clock)!;

        Assert.Equal(ClockAnomaly.Reboot, observation.Anomaly);
        Assert.True(observation.UptimeDelta < TimeSpan.Zero);
    }

    [Fact]
    public void Small_scheduling_noise_does_not_trip_the_detector()
    {
        var clock = new ManualClock();
        var monitor = new ClockIntegrityMonitor();
        monitor.Observe(clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.JumpWallClock(TimeSpan.FromMilliseconds(120));
        var observation = monitor.Observe(clock)!;

        Assert.Equal(ClockAnomaly.None, observation.Anomaly);
    }
}
