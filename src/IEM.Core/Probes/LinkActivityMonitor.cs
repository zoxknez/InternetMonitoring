using System.Net.NetworkInformation;
using IEM.Core.Time;

namespace IEM.Core.Probes;

/// <param name="BytesPerSecond">Combined send and receive rate over the sampling window.</param>
public sealed record LinkActivity(long BytesPerSecond, bool IsIdle)
{
    public double MegabitsPerSecond => BytesPerSecond * 8d / 1_000_000d;
}

/// <summary>
/// Watches how much traffic the machine itself is putting through the link.
/// <para>
/// This is what makes a speed measurement mean anything. A test run while the customer is
/// in a video call, or while Windows is downloading an update, measures whatever bandwidth
/// happened to be left over - and a figure like that in a complaint is worse than no
/// figure, because the operator only has to ask what else was running.
/// </para>
/// <para>
/// It only sees this machine's own traffic. Another device on the same connection is
/// invisible from here, which is exactly why the official procedure requires unplugging
/// them and why the checklist has to say so rather than pretending this check covers it.
/// </para>
/// </summary>
public sealed class LinkActivityMonitor(IClock? clock = null)
{
    /// <summary>
    /// Below this the link counts as quiet. Chosen well above the monitor's own traffic -
    /// a few pings and a small HTTP request per second - and well below anything a person
    /// would notice as usage.
    /// </summary>
    public const long IdleThresholdBytesPerSecond = 60_000;

    private readonly IClock _clock = clock ?? SystemClock.Instance;

    private long _previousBytes;
    private long _previousTicks;
    private bool _hasPrevious;

    /// <summary>
    /// Measures traffic since the previous call.
    /// Returns null on the first call, which has no interval to measure over.
    /// </summary>
    public LinkActivity? Sample(string interfaceId)
    {
        var total = ReadTotalBytes(interfaceId);
        if (total is null)
        {
            return null;
        }

        var now = _clock.MonotonicTicks;

        if (!_hasPrevious)
        {
            _previousBytes = total.Value;
            _previousTicks = now;
            _hasPrevious = true;
            return null;
        }

        var elapsed = _clock.MonotonicElapsed(_previousTicks, now);
        var delta = total.Value - _previousBytes;

        _previousBytes = total.Value;
        _previousTicks = now;

        if (elapsed <= TimeSpan.Zero || delta < 0)
        {
            // Counters reset when an adapter is disabled and re-enabled. A negative delta
            // is that, not negative traffic, and reporting it as a rate would be nonsense.
            return null;
        }

        var rate = (long)(delta / elapsed.TotalSeconds);
        return new LinkActivity(rate, rate < IdleThresholdBytesPerSecond);
    }

    /// <summary>Forgets history, so the next call starts a fresh interval.</summary>
    public void Reset() => _hasPrevious = false;

    private static long? ReadTotalBytes(string interfaceId)
    {
        try
        {
            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, interfaceId, StringComparison.OrdinalIgnoreCase));

            if (adapter is null)
            {
                return null;
            }

            var statistics = adapter.GetIPStatistics();
            return statistics.BytesReceived + statistics.BytesSent;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
