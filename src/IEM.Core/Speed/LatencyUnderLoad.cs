using System.Diagnostics;
using IEM.Core.Time;

namespace IEM.Core.Speed;

/// <summary>
/// A set of round-trip readings reduced to the three figures worth reporting.
/// <para>
/// The median rather than the mean, because one stalled probe in a set of twenty moves a
/// mean far more than it moves the experience it is meant to describe. The 95th percentile
/// is kept beside it precisely so that tail is not lost: on a connection with a full buffer
/// it is the tail, not the middle, that breaks calls and games.
/// </para>
/// </summary>
/// <param name="Samples">How many probes the figures rest on. Two readings are not a percentile.</param>
/// <param name="TimedOut">
/// Probes that never answered inside the per-probe ceiling, counted at the ceiling rather
/// than discarded: dropping them would quietly improve the very reading they describe.
/// </param>
public sealed record LatencyReading(
    TimeSpan Median,
    TimeSpan Min,
    TimeSpan P95,
    int Samples,
    int TimedOut = 0)
{
    /// <summary>Reduces raw readings, or null when there were none to reduce.</summary>
    public static LatencyReading? From(IReadOnlyList<TimeSpan> samples, int timedOut = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return null;
        }

        var ordered = samples.Order().ToList();

        return new LatencyReading(
            Percentile(ordered, 0.50),
            ordered[0],
            Percentile(ordered, 0.95),
            ordered.Count,
            timedOut);
    }

    /// <summary>
    /// Nearest-rank percentile on an already ordered list. Deliberately not interpolated:
    /// an interpolated figure is a number nobody measured, and with twenty samples the
    /// difference is smaller than the measurement noise anyway.
    /// </summary>
    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> ordered, double share)
    {
        var rank = (int)Math.Ceiling(share * ordered.Count) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Count - 1)];
    }
}

/// <summary>How much the connection's own buffers delay everything else while it is busy.</summary>
public enum LoadedLatencyGrade
{
    /// <summary>Calls and games are unaffected while the line is in use.</summary>
    Slight,

    /// <summary>Noticeable: conversation talks over itself, a game stutters.</summary>
    Noticeable,

    /// <summary>The connection becomes unusable for anything interactive while it is busy.</summary>
    Severe,
}

/// <summary>
/// Round-trip time sampled while the line is loaded - the one measured quantity the
/// regulators' tools have and a plain download test does not.
/// <para>
/// A connection that reaches its contracted rate can still be unusable for calls and games,
/// and the reason is almost always the same: an oversized buffer somewhere on the path fills
/// up under load, and every packet behind it waits. The figure that shows it is not the speed
/// but the latency measured <em>during</em> the transfer, against the same connection's
/// latency when idle. FCC's Measuring Broadband America and Ofcom's methodology both load
/// the line in each direction and report exactly this.
/// </para>
/// <para>
/// Measured as an HTTP round trip to the same measurement endpoint rather than as ICMP,
/// deliberately: an echo request travels a different queue than the customer's traffic on
/// networks that prioritise or drop it, and this tool already refuses to build findings on
/// ICMP alone elsewhere. The figure therefore includes a little server-side handling, which
/// is why what is reported is the <em>increase</em> over the same measurement taken idle -
/// whatever the endpoint adds is present in both halves and cancels out.
/// </para>
/// </summary>
public sealed class LoadedLatencySampler(
    HttpClient httpClient,
    LoadedLatencyOptions? options = null,
    IClock? clock = null)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly LoadedLatencyOptions _options = options ?? LoadedLatencyOptions.Default;
    private readonly IClock _clock = clock ?? SystemClock.Instance;

    /// <summary>
    /// Samples until the token is cancelled - which the caller does when the transfer it is
    /// measuring alongside has finished.
    /// </summary>
    /// <param name="settleFor">
    /// Opening stretch whose readings are thrown away. During it the transfer is still
    /// filling the buffers, so a probe answered then describes a line that is not yet loaded.
    /// </param>
    public async Task<LatencyReading?> SampleWhileAsync(TimeSpan settleFor, CancellationToken stop)
    {
        var samples = new List<TimeSpan>();
        var timedOut = 0;
        var started = _clock.MonotonicTicks;

        while (!stop.IsCancellationRequested)
        {
            var probe = await ProbeAsync(stop).ConfigureAwait(false);

            if (probe is { } reading && _clock.MonotonicElapsedSince(started) >= settleFor)
            {
                if (reading >= _options.ProbeCeiling)
                {
                    timedOut++;
                }

                samples.Add(reading);
            }

            try
            {
                await Task.Delay(_options.Interval, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return LatencyReading.From(samples, timedOut);
    }

    /// <summary>
    /// Samples for a fixed window, for the idle baseline the loaded figures are read against.
    /// <para>
    /// The first probe is taken and discarded: it pays for the connection being opened, and
    /// a handshake counted as latency would inflate the baseline and hide exactly the
    /// increase this measurement exists to show.
    /// </para>
    /// </summary>
    public async Task<LatencyReading?> SampleForAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        await ProbeAsync(cancellationToken).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(window);

        return await SampleWhileAsync(TimeSpan.Zero, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>The idle baseline, sampled for the window this sampler was configured with.</summary>
    public Task<LatencyReading?> SampleBaselineAsync(CancellationToken cancellationToken) =>
        SampleForAsync(_options.BaselineWindow, cancellationToken);

    /// <summary>
    /// One round trip, or null when it failed for a reason that is not delay.
    /// <para>
    /// A probe that runs past the ceiling is reported <em>as</em> the ceiling rather than as
    /// nothing. Under a full buffer the slowest probes are the whole finding, and discarding
    /// them would report the connection as better than it is - the one direction this tool
    /// must never be wrong in.
    /// </para>
    /// </summary>
    private async Task<TimeSpan?> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ProbeCeiling);

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await _httpClient
                .GetAsync(_options.ProbeUrl, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            return Stopwatch.GetElapsedTime(started);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Ran past the ceiling. Counted at the ceiling, as above.
            return _options.ProbeCeiling;
        }
#pragma warning disable CA1031 // A refused or reset probe is a missing reading, not a failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}

public sealed record LoadedLatencyOptions
{
    public static readonly LoadedLatencyOptions Default = new();

    /// <summary>
    /// A response with no payload, so what is timed is the round trip rather than a transfer.
    /// The same host the throughput runs against, so both halves travel the same path.
    /// </summary>
    public string ProbeUrl { get; init; } = "https://speed.cloudflare.com/__down?bytes=0";

    /// <summary>
    /// How often a probe is sent.
    /// <para>
    /// Often enough to see the buffer fill and drain within a ten-second window, rarely
    /// enough that the probes themselves are not part of the load being measured.
    /// </para>
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Longest a single probe is waited for.
    /// <para>
    /// Five seconds is past the point where any interactive use has already failed, so
    /// waiting longer would add precision to a figure whose meaning is already settled.
    /// </para>
    /// </summary>
    public TimeSpan ProbeCeiling { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the idle baseline is sampled for before the transfers begin.</summary>
    public TimeSpan BaselineWindow { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Where a measured increase in latency under load falls.
/// <para>
/// The thresholds are the ones interactive use turns on rather than any regulation: a
/// conversation tolerates about 150 ms of one-way delay before people start talking over
/// each other (ITU-T G.114), and the added delay measured here lands on top of whatever the
/// path already costs. So: a few tens of milliseconds is invisible, a hundred is audible,
/// and past that the connection is not usable for anything interactive while it is busy.
/// </para>
/// </summary>
public static class LoadedLatency
{
    public static readonly TimeSpan NoticeableFrom = TimeSpan.FromMilliseconds(30);

    public static readonly TimeSpan SevereFrom = TimeSpan.FromMilliseconds(100);

    /// <summary>How much the loaded reading rose above the idle one, or null when either is missing.</summary>
    public static TimeSpan? Increase(LatencyReading? idle, LatencyReading? loaded) =>
        idle is null || loaded is null ? null : loaded.Median - idle.Median;

    /// <summary>
    /// The worse of the two directions, since a line is unusable for calls if either
    /// direction stalls - and the person on the other end hears it either way.
    /// </summary>
    public static TimeSpan? WorstIncrease(
        LatencyReading? idle,
        LatencyReading? underDownload,
        LatencyReading? underUpload)
    {
        var down = Increase(idle, underDownload);
        var up = Increase(idle, underUpload);

        return (down, up) switch
        {
            ({ } d, { } u) => d > u ? d : u,
            ({ } d, null) => d,
            (null, { } u) => u,
            _ => null,
        };
    }

    public static LoadedLatencyGrade Grade(TimeSpan increase) =>
        increase >= SevereFrom ? LoadedLatencyGrade.Severe
        : increase >= NoticeableFrom ? LoadedLatencyGrade.Noticeable
        : LoadedLatencyGrade.Slight;
}
