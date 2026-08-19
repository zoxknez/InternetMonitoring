using System.Net;
using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// Latency under load is the one measured quantity the regulators' tools have and a plain
/// download test does not.
/// <para>
/// A connection that reaches its contracted rate can still be unusable for calls and games,
/// and the reason is nearly always an oversized buffer that fills under load. What shows it
/// is the round trip measured <em>during</em> the transfer, read against the same round trip
/// measured idle - so these tests are about the difference between the two, and about not
/// flattering the connection when a probe never comes back.
/// </para>
/// </summary>
public sealed class LoadedLatencyTests
{
    // ---- Reducing a set of readings ---------------------------------------------

    /// <summary>
    /// The median, not the mean: one stalled probe in twenty moves a mean far more than it
    /// moves the experience the figure is meant to describe.
    /// </summary>
    [Fact]
    public void The_middle_reading_is_the_median_and_the_tail_is_kept_beside_it()
    {
        TimeSpan[] samples =
        [
            Ms(10), Ms(12), Ms(11), Ms(13), Ms(10),
            Ms(11), Ms(12), Ms(14), Ms(11), Ms(400),
        ];

        var reading = LatencyReading.From(samples);

        Assert.NotNull(reading);
        Assert.Equal(Ms(10), reading.Min);
        Assert.InRange(reading.Median.TotalMilliseconds, 11, 12);

        // The one stalled probe belongs in the tail figure and nowhere else.
        Assert.Equal(Ms(400), reading.P95);
        Assert.Equal(10, reading.Samples);
    }

    [Fact]
    public void No_readings_reduce_to_nothing_rather_than_to_zero()
    {
        Assert.Null(LatencyReading.From([]));
    }

    [Fact]
    public void A_single_reading_is_reported_as_one_sample()
    {
        var reading = LatencyReading.From([Ms(25)]);

        Assert.NotNull(reading);
        Assert.Equal(Ms(25), reading.Median);
        Assert.Equal(Ms(25), reading.P95);
        Assert.Equal(1, reading.Samples);
    }

    // ---- What the increase means -------------------------------------------------

    [Fact]
    public void The_increase_is_the_loaded_median_above_the_idle_one()
    {
        var idle = LatencyReading.From([Ms(12)]);
        var loaded = LatencyReading.From([Ms(212)]);

        Assert.Equal(Ms(200), LoadedLatency.Increase(idle, loaded));
    }

    /// <summary>
    /// Missing rather than zero: a measurement that never took a baseline has not shown the
    /// connection to be free of the problem.
    /// </summary>
    [Fact]
    public void Without_both_halves_there_is_no_increase_to_report()
    {
        Assert.Null(LoadedLatency.Increase(null, LatencyReading.From([Ms(200)])));
        Assert.Null(LoadedLatency.Increase(LatencyReading.From([Ms(12)]), null));
    }

    /// <summary>
    /// A call is ruined by either direction stalling, and the person at the other end hears
    /// it either way - so the reported figure is the worse of the two.
    /// </summary>
    [Fact]
    public void The_worse_direction_is_the_one_reported()
    {
        var idle = LatencyReading.From([Ms(10)]);
        var underDownload = LatencyReading.From([Ms(40)]);
        var underUpload = LatencyReading.From([Ms(310)]);

        Assert.Equal(Ms(300), LoadedLatency.WorstIncrease(idle, underDownload, underUpload));
        Assert.Equal(Ms(30), LoadedLatency.WorstIncrease(idle, underDownload, null));
        Assert.Equal(Ms(300), LoadedLatency.WorstIncrease(idle, null, underUpload));
        Assert.Null(LoadedLatency.WorstIncrease(idle, null, null));
    }

    [Theory]
    [InlineData(5, LoadedLatencyGrade.Slight)]
    [InlineData(29, LoadedLatencyGrade.Slight)]
    [InlineData(30, LoadedLatencyGrade.Noticeable)]
    [InlineData(99, LoadedLatencyGrade.Noticeable)]
    [InlineData(100, LoadedLatencyGrade.Severe)]
    [InlineData(800, LoadedLatencyGrade.Severe)]
    public void The_grade_follows_the_measured_increase(double increaseMs, LoadedLatencyGrade expected)
    {
        Assert.Equal(expected, LoadedLatency.Grade(Ms(increaseMs)));
    }

    /// <summary>
    /// The wording has to reach someone who says "radi, ali zapinje" - so it talks about
    /// calls and games, and the serious grade says plainly that this is its own complaint.
    /// </summary>
    [Fact]
    public void Every_grade_is_explained_in_terms_of_what_stops_working()
    {
        foreach (var grade in Enum.GetValues<LoadedLatencyGrade>())
        {
            Assert.NotEmpty(grade.Explain());
            Assert.NotEmpty(grade.Label());
        }

        Assert.Contains("prigovor", LoadedLatencyGrade.Severe.Explain(), StringComparison.Ordinal);
        Assert.Contains("ne kvari", LoadedLatencyGrade.Slight.Explain(), StringComparison.Ordinal);
    }

    // ---- Sampling ----------------------------------------------------------------

    [Fact]
    public async Task Sampling_stops_when_the_transfer_it_runs_beside_does()
    {
        var sampler = new LoadedLatencySampler(
            new HttpClient(new StubHandler(TimeSpan.FromMilliseconds(5))),
            new LoadedLatencyOptions { Interval = TimeSpan.FromMilliseconds(1) });

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var reading = await sampler.SampleWhileAsync(TimeSpan.Zero, stop.Token);

        Assert.NotNull(reading);
        Assert.True(reading.Samples > 0);
        Assert.True(reading.Median >= TimeSpan.Zero);
    }

    /// <summary>
    /// The opening stretch is thrown away because during it the transfer is still filling
    /// the buffers: a probe answered then describes a line that is not yet loaded.
    /// </summary>
    [Fact]
    public async Task Readings_taken_before_the_line_settles_are_discarded()
    {
        var clock = new ManualClock();
        var sampler = new LoadedLatencySampler(
            new HttpClient(new StubHandler(TimeSpan.FromMilliseconds(1))),
            new LoadedLatencyOptions { Interval = TimeSpan.FromMilliseconds(1) },
            clock);

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));

        // The clock never advances, so nothing ever gets past the settling window.
        var reading = await sampler.SampleWhileAsync(TimeSpan.FromSeconds(30), stop.Token);

        Assert.Null(reading);
    }

    /// <summary>
    /// A probe that never comes back is counted at the ceiling rather than discarded. Under
    /// a full buffer the slowest probes are the whole finding, and dropping them would report
    /// the connection as better than it is - the one direction this tool must never err in.
    /// </summary>
    [Fact]
    public async Task A_probe_that_never_answers_counts_at_the_ceiling_instead_of_vanishing()
    {
        var sampler = new LoadedLatencySampler(
            new HttpClient(new StubHandler(TimeSpan.FromSeconds(30))),
            new LoadedLatencyOptions
            {
                Interval = TimeSpan.FromMilliseconds(1),
                ProbeCeiling = TimeSpan.FromMilliseconds(40),
            });

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var reading = await sampler.SampleWhileAsync(TimeSpan.Zero, stop.Token);

        Assert.NotNull(reading);
        Assert.True(reading.TimedOut > 0);
        Assert.True(reading.Median >= TimeSpan.FromMilliseconds(35));
    }

    /// <summary>
    /// A refused or reset probe is a missing reading, not a delay - reporting it as one
    /// would invent a stall out of a server hiccup.
    /// </summary>
    [Fact]
    public async Task A_refused_probe_leaves_no_reading_at_all()
    {
        var sampler = new LoadedLatencySampler(
            new HttpClient(new StubHandler(TimeSpan.Zero, HttpStatusCode.ServiceUnavailable)),
            new LoadedLatencyOptions { Interval = TimeSpan.FromMilliseconds(1) });

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Assert.Null(await sampler.SampleWhileAsync(TimeSpan.Zero, stop.Token));
    }

    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

    /// <summary>An endpoint that answers after a fixed delay, with no network involved.</summary>
    private sealed class StubHandler(TimeSpan delay, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        }
    }
}
