using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Core.Speed;
using IEM.Storage;
using IEM.Storage.Evidence;
using IEM.Storage.Layout;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IEM.Service.Runtime;

/// <summary>
/// Platform-neutral worker carrying out scheduled speed measurements.
/// Invariants 211 and 275: One Evidence Engine, injected platform probe factory.
/// </summary>
public sealed class SpeedWorker(
    IOptions<MonitorSettings> settings,
    ILogger<SpeedWorker> logger,
    IPlatformProbeFactory probeFactory,
    IPlatformStorageLayout storageLayout) : BackgroundService
{
    private readonly MonitorSettings _settings = settings.Value;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuietPatience = TimeSpan.FromMinutes(10);

    public SpeedStatus Status { get; private set; } = SpeedStatus.Idle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var outputRoot = _settings.ResolveOutputRoot(storageLayout.DefaultOutputRoot);

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
                Status = Status with { State = SpeedState.Idle, DueAtUtc = null, Message = null };
            }

            return;
        }

        var now = DateTimeOffset.UtcNow;

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
        SpeedRequest.Clear(outputRoot);
    }

    private async Task MeasureAsync(SpeedRequest request, string outputRoot, CancellationToken stoppingToken)
    {
        Status = Status with { State = SpeedState.Measuring, DueAtUtc = request.DueAtUtc, Message = null };

        logger.LogInformation("Zakazano merenje brzine počinje. Čeka se da veza bude mirna.");

        await using var linkInspection = await probeFactory.CreateLinkInspectionAsync(request.Interface ?? _settings.Interface).ConfigureAwait(false);
        var link = linkInspection.Inspector.Inspect();

        var observer = new ConnectionObserver();
        using var httpClient = MeasurementHttpClient.Create(observer);
        using var latencyClient = MeasurementHttpClient.Create();

        var activity = new LinkActivityMonitor();
        var measurement = new ThroughputMeasurement(
            httpClient,
            activity,
            ThroughputOptions.Default with { MeasureUpload = request.MeasureUpload },
            clock: null,
            new LoadedLatencySampler(latencyClient));

        var deadline = DateTimeOffset.UtcNow + QuietPatience;

        while (!measurement.ReadyToMeasure)
        {
            stoppingToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow > deadline)
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

            var reading = activity.Sample(link.InterfaceId);
            measurement.Observe(reading);

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }

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
                Message = $"Merenje nije izvršeno: {result.Refusal.Explain()}",
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
            ActualPath = PathAgreement.Of(link.InterfaceId, observer.Attempts),
        };

        var latest = SessionPaths.FindOpen(outputRoot);
        var note = SpeedMeasurementNote.From(
            DateTimeOffset.UtcNow,
            link.Medium,
            link.LinkSpeedBitsPerSecond / 1_000_000d,
            conditions,
            result);

        note.Write(latest?.Directory ?? outputRoot);

        Status = Status with
        {
            State = SpeedState.Completed,
            LastFinding = note,
            Message = $"Izmereno: {result.DownloadMbps:0.##} Mbit/s preuzimanje, " +
                      $"{(result.UploadMbps.HasValue ? $"{result.UploadMbps.Value:0.##} Mbit/s" : "0 Mbit/s")} slanje.",
        };

        logger.LogInformation(
            "Zakazano merenje brzine je završeno: {Download:0.##} Mbit/s preuzimanje, {Upload:0.##} Mbit/s slanje.",
            result.DownloadMbps,
            result.UploadMbps ?? 0);
    }
}

public enum SpeedState
{
    Idle,
    Scheduled,
    Measuring,
    Completed,
    Refused,
    Missed,
    Failed,
}

public sealed record SpeedStatus(
    SpeedState State,
    DateTimeOffset? DueAtUtc,
    SpeedMeasurementNote? LastFinding,
    string? Message)
{
    public static readonly SpeedStatus Idle = new(SpeedState.Idle, null, null, null);
}
