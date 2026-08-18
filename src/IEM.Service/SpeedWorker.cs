using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Core.Speed;
using IEM.Storage;
using IEM.Windows;
using Microsoft.Extensions.Options;

namespace IEM.Service;

/// <summary>
/// Carries out speed measurements that were scheduled for a moment nobody will be present for.
/// <para>
/// Scheduling used to live in whichever process asked for it: the window's version died when
/// the window closed, and the console's needed a console left open. That made the one case
/// scheduling exists for - "measure at three in the morning, when the line is quiet and I am
/// asleep" - the one case it could not serve. Here the instruction is a file on disk and the
/// work is done by a service that is already running and already survives reboots.
/// </para>
/// <para>
/// The measurement deliberately saturates the connection, so it waits for the link to be
/// genuinely quiet first, exactly as the console command does, and refuses rather than
/// filing a figure taken while something else was using the line.
/// </para>
/// </summary>
public sealed class SpeedWorker(
    IOptions<MonitorSettings> settings,
    ILogger<SpeedWorker> logger) : BackgroundService
{
    private readonly MonitorSettings _settings = settings.Value;

    /// <summary>How often the pending instruction is looked for.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long to stand in the queue for a quiet link before giving up.</summary>
    private static readonly TimeSpan QuietPatience = TimeSpan.FromMinutes(10);

    /// <summary>Live state for anything asking over the status pipe.</summary>
    public SpeedStatus Status { get; private set; } = SpeedStatus.Idle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var outputRoot = _settings.ResolveOutputRoot();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(outputRoot, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed measurement must never take the monitoring with it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogWarning(ex, "Zakazano merenje brzine nije uspelo. Zahtev se uklanja.");
                Status = Status with { State = SpeedState.Failed, Message = ex.Message };
                SpeedRequest.Clear(outputRoot);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(string outputRoot, CancellationToken stoppingToken)
    {
        var request = SpeedRequest.Read(outputRoot);

        if (request is null)
        {
            if (Status.State == SpeedState.Scheduled)
            {
                // Someone cancelled it while we were waiting.
                Status = Status with { State = SpeedState.Idle, DueAtUtc = null, Message = null };
            }

            return;
        }

        var now = DateTimeOffset.UtcNow;

        // A machine switched off overnight comes back to an instruction whose moment passed
        // hours ago. Measuring now would file a figure timestamped now against a request that
        // meant three in the morning - and the hour was the whole point of scheduling it.
        if (request.IsStale(now))
        {
            logger.LogWarning(
                "Zakazano merenje brzine je propušteno: trebalo je da se izvrši {Due}, a prošlo je " +
                "više od {Grace}. Merenje se ne izvršava, jer bi bilo zapisano u pogrešno vreme.",
                SerbianText.DateTime(request.DueAtUtc.ToLocalTime()),
                SerbianText.Duration(SpeedRequest.Grace));

            Status = Status with
            {
                State = SpeedState.Missed,
                DueAtUtc = request.DueAtUtc,
                Message = "Merenje je propušteno jer računar nije radio u zakazano vreme.",
            };

            SpeedRequest.Clear(outputRoot);
            return;
        }

        if (!request.IsDue(now))
        {
            Status = Status with
            {
                State = SpeedState.Scheduled,
                DueAtUtc = request.DueAtUtc,
                Message = null,
            };

            return;
        }

        await MeasureAsync(request, outputRoot, stoppingToken).ConfigureAwait(false);

        // Discharged either way. A request that stayed on disk after a refusal would retry
        // every quarter of a minute for as long as the service ran.
        SpeedRequest.Clear(outputRoot);
    }

    private async Task MeasureAsync(SpeedRequest request, string outputRoot, CancellationToken stoppingToken)
    {
        Status = Status with { State = SpeedState.Measuring, DueAtUtc = request.DueAtUtc, Message = null };

        logger.LogInformation("Zakazano merenje brzine počinje. Čeka se da veza bude mirna.");

        await using var linkInspection = WindowsLinkInspection.Create(request.Interface ?? _settings.Interface);
        var link = linkInspection.Inspector.Inspect();

        // No proxy, for the same reason the console command uses none: an intercepting proxy
        // silently caps the rate, and a measurement that quietly went through one is worse
        // than none at all.
        var observer = new ConnectionObserver();
        using var httpClient = MeasurementHttpClient.Create(observer);

        // The round-trip probes travel on a client of their own, so they do not queue behind
        // the transfer they are measuring alongside.
        using var latencyClient = MeasurementHttpClient.Create();

        var activity = new LinkActivityMonitor();
        var measurement = new ThroughputMeasurement(
            httpClient,
            activity,
            ThroughputOptions.Default with { MeasureUpload = request.MeasureUpload },
            clock: null,
            new LoadedLatencySampler(latencyClient));

        if (!await WaitForQuietAsync(measurement, activity, link.InterfaceId, stoppingToken).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Zakazano merenje brzine nije izvršeno: veza nije bila mirna ni posle {Patience}. " +
                "Izmerena bi bila preostala brzina, ne raspoloživa.",
                SerbianText.Duration(QuietPatience));

            Status = Status with
            {
                State = SpeedState.Refused,
                Message = "Veza je bila zauzeta, pa merenje nije izvršeno.",
            };

            return;
        }

        // From here on the measurement is the load. A session running in this same service
        // excludes the period instead of recording our own transfer against the operator.
        ThroughputResult result;

        using (MeasurementMarker.Hold(outputRoot))
        {
            result = await measurement.MeasureAsync(connectionHealthy: true, stoppingToken).ConfigureAwait(false);
        }

        if (!result.Ran)
        {
            logger.LogWarning("Zakazano merenje brzine nije izvršeno: {Reason}.", result.Refusal);

            Status = Status with
            {
                State = SpeedState.Refused,
                Message = "Merenje nije izvršeno; server za merenje nije odgovorio.",
            };

            return;
        }

        var conditions = new SpeedMeasurementConditions(
            link.Medium,
            link.LinkSpeedBitsPerSecond,
            request.ContractedDownloadMbps,
            result.DownloadMbps)
        {
            ContractedUploadMbps = request.ContractedUploadMbps,
            MeasuredUploadMbps = result.UploadMbps,

            // Checked rather than assumed: on a machine with a docking station or a VPN the
            // transfer can leave through an adapter other than the one being described. An
            // unresolved check stays unresolved - it used to fall back to "one path", so the
            // service recorded a verified path on machines where nothing had been verified.
            RouteState = MeasurementPath.Resolve(link.InterfaceId).State,
            ActualPath = PathAgreement.Of(link.InterfaceId, observer.Attempts),
        };

        var note = SpeedMeasurementNote.From(
            DateTimeOffset.UtcNow,
            link.Medium,
            link.LinkSpeedBitsPerSecond / 1_000_000d,
            conditions,
            result);

        // Beside the session it belongs to, so the report states it with its verdict. With no
        // session yet it waits in the output root, where the next session's report finds it.
        var directory = SessionPaths.FindLatest(outputRoot)?.Directory ?? outputRoot;
        note.Write(directory);

        // Every figure is formatted before it reaches the template. Left to the logger, the
        // download rate came out with a decimal point beside an upload rate with a comma -
        // one line, two conventions, in an application that is Serbian throughout.
        logger.LogInformation(
            "Zakazano merenje brzine je izvršeno: preuzimanje {Download} Mbit/s, " +
            "slanje {Upload}, kašnjenje pod opterećenjem {Latency}. Zapisano u {Directory}.",
            note.DownloadMbps.ToString("0.##", SerbianText.Culture),
            note.UploadMbps is { } upload
                ? $"{upload.ToString("0.##", SerbianText.Culture)} Mbit/s"
                : "nije mereno",
            note.LatencyIncreaseMs is { } increase
                ? $"+{increase.ToString("0.#", SerbianText.Culture)} ms ({note.LoadedLatencyLabel})"
                : "nije mereno",
            directory);

        Status = new SpeedStatus(
            SpeedState.Measured,
            DueAtUtc: request.DueAtUtc,
            MeasuredAtUtc: note.MeasuredAtUtc,
            DownloadMbps: note.DownloadMbps,
            UploadMbps: note.UploadMbps,
            LatencyIncreaseMs: note.LatencyIncreaseMs,
            BandLabel: note.Assess().BandLabel,
            Directory: directory,
            Message: null);
    }

    /// <summary>
    /// Stands in the queue until the link has been quiet long enough, or the patience runs out.
    /// <para>
    /// The same rule the console command follows, and it is the point of the measurement
    /// rather than a refinement of it: a figure taken while the machine was downloading
    /// something else is the remaining bandwidth, not the available one, and an operator only
    /// has to ask what else was running for the rest of the evidence to go with it.
    /// </para>
    /// </summary>
    private static async Task<bool> WaitForQuietAsync(
        ThroughputMeasurement measurement,
        LinkActivityMonitor activity,
        string interfaceId,
        CancellationToken stoppingToken)
    {
        var deadline = DateTimeOffset.UtcNow + QuietPatience;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            stoppingToken.ThrowIfCancellationRequested();

            measurement.Observe(activity.Sample(interfaceId));

            if (measurement.ReadyToMeasure)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }

        return measurement.ReadyToMeasure;
    }
}

/// <summary>Where a scheduled measurement stands.</summary>
public enum SpeedState
{
    /// <summary>Nothing is scheduled.</summary>
    Idle,

    Scheduled,

    /// <summary>Waiting for the link to go quiet, or transferring.</summary>
    Measuring,

    Measured,

    /// <summary>The link never went quiet, or nothing answered. No figure was recorded.</summary>
    Refused,

    /// <summary>Its moment passed while the machine was off, by more than the figure would mean.</summary>
    Missed,

    Failed,
}

/// <param name="LatencyIncreaseMs">How much worse the round trip got under load.</param>
public sealed record SpeedStatus(
    SpeedState State,
    DateTimeOffset? DueAtUtc,
    DateTimeOffset? MeasuredAtUtc,
    double? DownloadMbps,
    double? UploadMbps,
    double? LatencyIncreaseMs,
    string? BandLabel,
    string? Directory,
    string? Message)
{
    public static readonly SpeedStatus Idle =
        new(SpeedState.Idle, null, null, null, null, null, null, null, null);
}
