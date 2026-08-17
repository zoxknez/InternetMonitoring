using System.Diagnostics;
using System.Net;
using IEM.Core.Probes;
using IEM.Core.Time;

namespace IEM.Core.Speed;

/// <summary>Why a measurement did not run.</summary>
public enum ThroughputRefusal
{
    None,

    /// <summary>The machine was already using the connection.</summary>
    LinkBusy,

    /// <summary>The connection was not working well enough to measure.</summary>
    ConnectionUnhealthy,

    /// <summary>Nothing answered, so there was no throughput to measure.</summary>
    NoResponse,

    /// <summary>The link went idle but not for long enough to trust.</summary>
    NotIdleLongEnough,
}

/// <param name="Refusal">Why it did not run, when it did not.</param>
public sealed record ThroughputResult(
    double DownloadMbps,
    long BytesTransferred,
    TimeSpan Duration,
    ThroughputRefusal Refusal)
{
    public bool Ran => Refusal == ThroughputRefusal.None;

    /// <summary>
    /// Sending rate, or null when the upload half did not run or nothing got through.
    /// <para>
    /// Null rather than zero, and the distinction is the whole point: zero would read as a
    /// line that cannot send at all, which is a serious finding, while what actually
    /// happened is that nothing was measured.
    /// </para>
    /// </summary>
    public double? UploadMbps { get; init; }

    public long UploadBytes { get; init; }

    public TimeSpan? UploadDuration { get; init; }

    /// <summary>Round trips taken before the transfers began, as the line to read the rest against.</summary>
    public LatencyReading? IdleLatency { get; init; }

    /// <summary>Round trips taken while the line was pulling at full rate.</summary>
    public LatencyReading? DownloadLoadedLatency { get; init; }

    /// <summary>Round trips taken while the line was sending at full rate.</summary>
    public LatencyReading? UploadLoadedLatency { get; init; }

    /// <summary>
    /// How much the connection's own buffering added to every other packet while it was
    /// busy, taken from whichever direction was worse - a call is ruined by either.
    /// </summary>
    public TimeSpan? LatencyIncreaseUnderLoad =>
        LoadedLatency.WorstIncrease(IdleLatency, DownloadLoadedLatency, UploadLoadedLatency);

    public LoadedLatencyGrade? LoadedLatencyGrade =>
        LatencyIncreaseUnderLoad is { } increase ? Speed.LoadedLatency.Grade(increase) : null;

    public static ThroughputResult Refused(ThroughputRefusal reason) =>
        new(0, 0, TimeSpan.Zero, reason);
}

public sealed record ThroughputOptions
{
    public static readonly ThroughputOptions Default = new();

    /// <summary>
    /// Where the payload is fetched from.
    /// <para>
    /// Cloudflare's own endpoint, which exists for this purpose, serves an arbitrary length
    /// and is reachable from every Serbian operator. Deliberately not a mirror belonging to
    /// any one of them: a figure measured against the operator's own server tests their
    /// cache rather than their transit, and is the first thing they would point out.
    /// </para>
    /// </summary>
    public string DownloadUrl { get; init; } = "https://speed.cloudflare.com/__down?bytes=";

    /// <summary>
    /// Where the sent payload goes.
    /// <para>
    /// The same host's endpoint for the opposite direction, so both halves of the
    /// measurement travel the same path to the same place and can be read against each
    /// other. It accepts a body of any length and discards it.
    /// </para>
    /// </summary>
    public string UploadUrl { get; init; } = "https://speed.cloudflare.com/__up";

    /// <summary>
    /// Whether the sending half runs.
    /// <para>
    /// On by default because a measurement of only one direction is the gap this tool had
    /// against every regulator methodology: FCC's Measuring Broadband America and BEREC's
    /// guidance both load the line each way. Off is for a metered connection, where the
    /// second transfer doubles what the measurement costs in data.
    /// </para>
    /// </summary>
    public bool MeasureUpload { get; init; } = true;

    /// <summary>
    /// How much to ask for per request.
    /// <para>
    /// The endpoint refuses anything much larger, so a single request cannot fill the
    /// measurement window on a fast line - fifty megabytes over a gigabit link is gone in
    /// about a second, and most of that second is TCP working its way up to speed. Requests
    /// are therefore issued back to back until the window closes, over the same connection.
    /// </para>
    /// </summary>
    public long PayloadBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>
    /// How long the transfer runs.
    /// <para>
    /// Ten seconds is enough for the rate to settle on any domestic connection. It is also
    /// what the measurement costs in data: about 1.2 GB on a gigabit link, rather less on
    /// anything slower. Worth saying out loud to anyone on a metered connection.
    /// </para>
    /// <para>
    /// RFC 6349 asks for longer windows (&gt;30 s) so network buffers are certainly full; the
    /// shorter window here is a deliberate trade of data cost against that certainty, and it
    /// is one reason the report calls a single figure a snapshot rather than a statistic.
    /// </para>
    /// </summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the sending transfer runs.
    /// <para>
    /// The same window as the download, for the plain reason that the two figures are read
    /// side by side and a shorter window on one of them would make it the less reliable of
    /// the pair without saying so. On an asymmetric line - which nearly every domestic
    /// connection is - the sending half costs a fraction of the data the download does.
    /// </para>
    /// </summary>
    public TimeSpan UploadMaxDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many transfers run at once.
    /// <para>
    /// Regulator methodologies (FCC Measuring Broadband America, SamKnows/Ofcom) measure
    /// over three or more parallel TCP connections, because a single flow can be window- or
    /// buffer-limited well below what the line carries - and a shortfall that exists only in
    /// the measurement is exactly what an operator's own multi-stream retest would disprove,
    /// dragging the rest of the evidence down with it. The parallel streams share the same
    /// link, so the transfer volume is unchanged; only its distribution over connections is.
    /// </para>
    /// </summary>
    public int ParallelConnections { get; init; } = 3;

    /// <summary>
    /// Opening stretch left out of the rate.
    /// <para>
    /// TCP starts slowly and works up, so bytes from the first moments describe the protocol
    /// warming up rather than the connection. Including them understates a fast line badly -
    /// which for this tool is the wrong direction to be wrong in, since an understated figure
    /// is one an operator can disprove.
    /// </para>
    /// </summary>
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the link must have been quiet before measuring.
    /// <para>
    /// A single quiet instant means nothing - a download pauses between chunks. Requiring a
    /// stretch of quiet is what separates "nothing is running" from "nothing is running at
    /// this exact millisecond".
    /// </para>
    /// </summary>
    public TimeSpan RequiredIdlePeriod { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Measures download throughput, but only when the connection is genuinely free.
/// <para>
/// The gating is the point, not a refinement of it. A speed test run while the customer is
/// in a video call measures whatever bandwidth was left over, and a figure like that in a
/// complaint is worse than no figure at all - the operator only has to ask what else was
/// running, and the rest of the report goes with it.
/// </para>
/// <para>
/// So this refuses rather than measures when the link is busy, and says why. A refusal the
/// user can act on is more useful than a number they cannot defend.
/// </para>
/// </summary>
/// <param name="latency">
/// Samples round trips alongside each transfer. Optional, and absent means the figures it
/// would have produced are reported as missing rather than guessed at.
/// <para>
/// It must be given a <see cref="HttpClient"/> of its own. Sharing the one carrying the
/// bulk transfer would have the probe queue behind fifty megabytes of payload inside this
/// process, and the delay this measurement exists to attribute to the connection would be
/// partly our own.
/// </para>
/// </param>
public sealed class ThroughputMeasurement(
    HttpClient httpClient,
    LinkActivityMonitor activity,
    ThroughputOptions? options = null,
    IClock? clock = null,
    LoadedLatencySampler? latency = null)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly LinkActivityMonitor _activity = activity ?? throw new ArgumentNullException(nameof(activity));
    private readonly ThroughputOptions _options = options ?? ThroughputOptions.Default;
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly LoadedLatencySampler? _latency = latency;

    private long? _idleSinceTicks;

    /// <summary>
    /// Folds in one activity reading. Called on the sampling tick so the measurement knows
    /// how long the link has been quiet before it is asked to run.
    /// </summary>
    public void Observe(string interfaceId) => Observe(_activity.Sample(interfaceId));

    /// <summary>
    /// Folds in a reading the caller already has, so the sampling loop does not read the
    /// adapter counters twice.
    /// </summary>
    public void Observe(LinkActivity? reading)
    {
        if (reading is null)
        {
            return;
        }

        if (reading.IsIdle)
        {
            _idleSinceTicks ??= _clock.MonotonicTicks;
        }
        else
        {
            // Any traffic at all restarts the clock. Averaging over a window would let a
            // steady trickle pass as quiet, which is exactly the case this exists to catch.
            _idleSinceTicks = null;
        }
    }

    /// <summary>How long the link has been quiet, or null if it is not quiet now.</summary>
    public TimeSpan? IdleFor =>
        _idleSinceTicks is { } since ? _clock.MonotonicElapsedSince(since) : null;

    /// <summary>The link has been quiet long enough for a measurement to mean something.</summary>
    public bool ReadyToMeasure => IdleFor >= _options.RequiredIdlePeriod;

    /// <summary>
    /// Runs a measurement, or refuses and says why.
    /// </summary>
    /// <param name="connectionHealthy">Nothing was failing when this was called.</param>
    public async Task<ThroughputResult> MeasureAsync(bool connectionHealthy, CancellationToken cancellationToken)
    {
        if (!connectionHealthy)
        {
            return ThroughputResult.Refused(ThroughputRefusal.ConnectionUnhealthy);
        }

        if (IdleFor is null)
        {
            return ThroughputResult.Refused(ThroughputRefusal.LinkBusy);
        }

        if (!ReadyToMeasure)
        {
            return ThroughputResult.Refused(ThroughputRefusal.NotIdleLongEnough);
        }

        // Taken before anything is loaded, and it is the reading everything else is judged
        // against: a connection that answers in 12 ms idle and 400 ms while busy has a
        // problem that no download figure on its own would ever show.
        var idleLatency = _latency is null
            ? null
            : await _latency.SampleBaselineAsync(cancellationToken).ConfigureAwait(false);

        var download = await TransferAsync(
            Direction.Download, _options.MaxDuration, cancellationToken).ConfigureAwait(false);

        // The measurement saturated the link on purpose, so the idle clock restarts.
        _idleSinceTicks = null;
        _activity.Reset();

        if (download.Bytes == 0 || download.Elapsed <= TimeSpan.Zero)
        {
            return ThroughputResult.Refused(ThroughputRefusal.NoResponse);
        }

        var upload = _options.MeasureUpload
            ? await TransferAsync(Direction.Upload, _options.UploadMaxDuration, cancellationToken)
                    .ConfigureAwait(false)
            : null;

        _idleSinceTicks = null;
        _activity.Reset();

        return new ThroughputResult(download.Mbps, download.Bytes, download.Elapsed, ThroughputRefusal.None)
        {
            // Zero bytes sent means the sending half did not happen, not that the line
            // cannot send - so it is reported as missing rather than as a rate of nought.
            UploadMbps = upload is { Bytes: > 0 } ? upload.Mbps : null,
            UploadBytes = upload?.Bytes ?? 0,
            UploadDuration = upload is { Bytes: > 0 } ? upload.Elapsed : null,
            IdleLatency = idleLatency,
            DownloadLoadedLatency = download.Latency,
            UploadLoadedLatency = upload?.Latency,
        };
    }

    private enum Direction
    {
        Download,
        Upload,
    }

    /// <param name="Latency">Round trips sampled while this transfer had the line loaded.</param>
    private sealed record TransferOutcome(double Mbps, long Bytes, TimeSpan Elapsed, LatencyReading? Latency);

    /// <summary>
    /// One direction, run over the configured number of connections, with round trips
    /// sampled alongside it for as long as it lasts.
    /// </summary>
    private async Task<TransferOutcome> TransferAsync(
        Direction direction,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maxDuration);

        var started = Stopwatch.GetTimestamp();

        // Sampled for exactly as long as the transfer runs, with the warm-up thrown away:
        // during it the buffers are still filling, so a probe answered then describes a line
        // that is not yet loaded.
        using var samplingDone = new CancellationTokenSource();
        var latencyTask = _latency is null
            ? Task.FromResult<LatencyReading?>(null)
            : _latency.SampleWhileAsync(_options.WarmUp, samplingDone.Token);

        // Every stream runs on its own connection - the pooled handler opens as many as are
        // asked for in parallel - so the rate is what several connections together push
        // through the line, not what one window allows.
        var streams = new Task<(long Moved, long Measured, long MeasureFrom)>[_options.ParallelConnections];

        for (var i = 0; i < streams.Length; i++)
        {
            streams[i] = direction == Direction.Download
                ? DownloadAsync($"{_options.DownloadUrl}{_options.PayloadBytes}", started, maxDuration, timeout.Token)
                : UploadAsync(_options.UploadUrl, started, maxDuration, timeout.Token);
        }

        // A stream never throws: one that ran out of window, or lost its connection mid-way,
        // hands back whatever moved. Bytes that moved are true whether or not the window
        // closed cleanly, and losing a stream shortens the measurement rather than ending it.
        var results = await Task.WhenAll(streams).ConfigureAwait(false);

        await samplingDone.CancelAsync().ConfigureAwait(false);
        var latency = await latencyTask.ConfigureAwait(false);

        long moved = 0;

        // Counted separately from the warm-up, so the rate describes the connection rather
        // than TCP finding its feet.
        long measuredBytes = 0;
        long measureFrom = 0;

        foreach (var result in results)
        {
            moved += result.Moved;
            measuredBytes += result.Measured;

            if (result.MeasureFrom != 0 && (measureFrom == 0 || result.MeasureFrom < measureFrom))
            {
                measureFrom = result.MeasureFrom;
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        if (moved == 0 || elapsed <= TimeSpan.Zero)
        {
            return new TransferOutcome(0, 0, elapsed, latency);
        }

        // The settled stretch where there is one. A transfer that ended before the warm-up
        // was over - a very slow line, or a server that closed early - is rated over the
        // whole thing instead, which understates it slightly and never the other way.
        var settled = measureFrom != 0 ? Stopwatch.GetElapsedTime(measureFrom) : TimeSpan.Zero;

        var (bytes, window) = settled > TimeSpan.FromSeconds(1) && measuredBytes > 0
            ? (measuredBytes, settled)
            : (moved, elapsed);

        return new TransferOutcome(bytes * 8d / 1_000_000d / window.TotalSeconds, moved, elapsed, latency);
    }

    /// <summary>
    /// One parallel transfer in the sending direction, counting the bytes it hands to the
    /// socket until the window closes under it.
    /// <para>
    /// The body is generated rather than read from anywhere, and from random bytes: a buffer
    /// of zeroes compresses to nothing on any path that compresses, and the rate measured
    /// would be the compressor's rather than the line's.
    /// </para>
    /// <para>
    /// What is counted is what was written to the stream, which for the first fraction of a
    /// second runs ahead of what left the machine while the send buffers fill. The warm-up
    /// is discarded for exactly that reason, and over a ten-second window what remains is
    /// the line's own rate.
    /// </para>
    /// </summary>
    private async Task<(long Moved, long Measured, long MeasureFrom)> UploadAsync(
        string url,
        long startedTicks,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        var content = new GeneratedContent(startedTicks, maxDuration, _options.WarmUp);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A stream that dies mid-window is a stop, not a failure: partials count.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            content.Dispose();
        }

        return (content.Sent, content.Measured, content.MeasureFrom);
    }

    /// <summary>
    /// A request body that keeps producing bytes until the measurement window closes,
    /// counting what it produced and when the settled stretch began.
    /// </summary>
    private sealed class GeneratedContent(long startedTicks, TimeSpan window, TimeSpan warmUp) : HttpContent
    {
        private const int BlockSize = 64 * 1024;

        public long Sent { get; private set; }

        public long Measured { get; private set; }

        public long MeasureFrom { get; private set; }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            var block = new byte[BlockSize];
            Random.Shared.NextBytes(block);

            while (Stopwatch.GetElapsedTime(startedTicks) < window && !cancellationToken.IsCancellationRequested)
            {
                await stream.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                Sent += block.Length;

                if (Stopwatch.GetElapsedTime(startedTicks) < warmUp)
                {
                    continue;
                }

                if (MeasureFrom == 0)
                {
                    MeasureFrom = Stopwatch.GetTimestamp();
                    continue;
                }

                Measured += block.Length;
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        /// <summary>
        /// Unknown by design: the body ends when the clock says so, not after a fixed size,
        /// so the request is sent chunked and the length is never declared.
        /// </summary>
        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    /// <summary>
    /// One parallel transfer, counting its own bytes until the window closes under it.
    /// <para>
    /// Back-to-back requests until the window closes: one is not enough on a fast line, and
    /// the connection is reused, so the cost of chaining them is a request header rather
    /// than a fresh handshake.
    /// </para>
    /// <para>
    /// Nothing is ever thrown. The window expiring, a refused connection, a reset stream -
    /// each is an ordinary way for a transfer to stop, and what matters is that the bytes
    /// which did arrive survive to be counted. An empty stream returns zeros, and the caller
    /// turns a total of nothing into a refusal.
    /// </para>
    /// </summary>
    /// <returns>Bytes received, bytes inside the measured window, and when that window began.</returns>
    private async Task<(long Received, long Measured, long MeasureFrom)> DownloadAsync(
        string url,
        long startedTicks,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        long received = 0;
        long measured = 0;
        long measureFrom = 0;

        try
        {
            var buffer = new byte[64 * 1024];

            while (Stopwatch.GetElapsedTime(startedTicks) < maxDuration)
            {
                using var response = await _httpClient
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                int read;

                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;

                    if (Stopwatch.GetElapsedTime(startedTicks) < _options.WarmUp)
                    {
                        continue;
                    }

                    if (measureFrom == 0)
                    {
                        measureFrom = Stopwatch.GetTimestamp();
                        continue;
                    }

                    measured += read;
                }
            }
        }
#pragma warning disable CA1031 // A stream that dies mid-window is a stop, not a failure: partials count.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return (received, measured, measureFrom);
    }
}
