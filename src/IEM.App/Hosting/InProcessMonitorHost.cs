using IEM.Core;
using IEM.Core.Model;
using IEM.Presentation.Hosting;
using IEM.Core.Probes;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.App.Hosting;

/// <summary>
/// Runs the engine inside this process.
/// Invariants 211 and 275: One Evidence Engine, injected platform probe factory.
/// </summary>
public sealed class InProcessMonitorHost : IMonitorHost
{
    private readonly string _outputRoot;
    private readonly IPlatformProbeFactory _probeFactory;

    private CancellationTokenSource? _sessionCancellation;
    private Task? _session;
    private MonitorEngine? _engine;

    public InProcessMonitorHost(string outputRoot, IPlatformProbeFactory probeFactory)
    {
        _outputRoot = outputRoot;
        _probeFactory = probeFactory ?? throw new ArgumentNullException(nameof(probeFactory));
    }

    public HostKind Kind => HostKind.InProcess;

    public bool IsRunning => _session is { IsCompleted: false };

    public event Action<MonitorSnapshot>? Updated;

    public event Action<string?>? FaultChanged;

    /// <summary>Nothing to connect to; the engine lives here.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> StartSessionAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return Task.FromResult(false);
        }

        _sessionCancellation = new CancellationTokenSource();
        _session = Task.Run(
            () => RunAsync(duration, interfaceName, _sessionCancellation.Token),
            CancellationToken.None);

        return Task.FromResult(true);
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessionCancellation is null)
        {
            return;
        }

        await _sessionCancellation.CancelAsync().ConfigureAwait(false);

        if (_session is not null)
        {
            try
            {
                await _session.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }
    }

    private async Task RunAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken)
    {
        EvidenceRecorder? recorder = null;
        SessionPaths? paths = null;

        try
        {
            var startedAt = DateTimeOffset.Now;
            await using var linkInspection = await _probeFactory.CreateLinkInspectionAsync(interfaceName).ConfigureAwait(false);
            var inspector = linkInspection.Inspector;
            var link = inspector.Inspect();

            paths = SessionPaths.ForNewSession(_outputRoot, startedAt);
            var sessionId = $"S{startedAt:yyyyMMddHHmmss}";

            MeasurementMarker.Clear(_outputRoot);

            await using var probeSource = new NetworkProbeSource(
                ProbeOptions.Default,
                inspector,
                clock: null,
                _probeFactory.CreateRouteResolver(),
                _probeFactory.CreateBoundIcmp(),
                () => MeasurementMarker.IsHeld(_outputRoot));

            var engine = new MonitorEngine(probeSource);
            _engine = engine;

            var start = new SessionStartPayload(
                sessionId,
                typeof(InProcessMonitorHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                startedAt.ToUniversalTime(),
                duration,
                Environment.MachineName,
                link.InterfaceName,
                link.Medium,
                link.LinkSpeedBitsPerSecond,
                link.GatewayAddress);

            recorder = EvidenceRecorder.Start(paths, engine, start);

            await using var tracer = new IncidentPathTracer();
            var boundRecorder = recorder;
            tracer.TraceCompleted += boundRecorder.RecordTrace;
            tracer.Attach(engine);

            var plannedDuration = duration == Timeout.InfiniteTimeSpan ? (TimeSpan?)null : duration;

            engine.SampleRecorded += sample => Updated?.Invoke(
                MonitorSnapshot.From(engine, sample, sessionId, paths.Directory) with
                {
                    StartedUtc = startedAt.ToUniversalTime(),
                    PlannedDuration = plannedDuration,
                });

            await engine.RunAsync(duration, cancellationToken).ConfigureAwait(false);

            recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            CompleteQuietly(recorder);
        }
#pragma warning disable CA1031 // A failed session must not take the window down with it.
        catch (Exception ex)
        {
            CompleteQuietly(recorder);
            FaultChanged?.Invoke(ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            recorder?.Dispose();

            if (paths is not null)
            {
                BuildReport(paths);
            }
        }
    }

    private void CompleteQuietly(EvidenceRecorder? recorder)
    {
        if (recorder is null || _engine is null)
        {
            return;
        }

        try
        {
            recorder.Complete(_engine.Statistics, DateTimeOffset.UtcNow);
        }
#pragma warning disable CA1031
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }

    private void BuildReport(SessionPaths paths)
    {
        try
        {
            Evidence.EvidencePackage.Build(paths);
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            FaultChanged?.Invoke(
                $"Izveštaj nije napravljen ({ex.Message}). Sirova evidencija je sačuvana u {paths.Directory}.");
        }
#pragma warning restore CA1031
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync(CancellationToken.None).ConfigureAwait(false);
        _sessionCancellation?.Dispose();
    }
}
