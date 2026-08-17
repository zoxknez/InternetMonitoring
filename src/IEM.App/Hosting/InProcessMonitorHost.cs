using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Storage;
using IEM.Storage.Evidence;
using IEM.Windows;

namespace IEM.App.Hosting;

/// <summary>
/// Runs the engine inside this process.
/// <para>
/// The path for someone who wants to look at their connection without installing
/// anything. Everything is recorded exactly as the service records it - same chain, same
/// integrity, same report - with one difference the interface states plainly: closing the
/// application ends the session, because there is nothing else keeping it alive.
/// </para>
/// </summary>
public sealed class InProcessMonitorHost(string outputRoot) : IMonitorHost
{
    private readonly string _outputRoot = outputRoot;

    private CancellationTokenSource? _sessionCancellation;
    private Task? _session;
    private MonitorEngine? _engine;

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
            // Wireless detail comes through the Windows layer, so a dropped Wi-Fi adapter can be
            // told apart from a router that stopped broadcasting.
            await using var linkInspection = WindowsLinkInspection.Create(interfaceName);
            var inspector = linkInspection.Inspector;
            var link = inspector.Inspect();

            paths = SessionPaths.ForNewSession(_outputRoot, startedAt);
            var sessionId = $"S{startedAt:yyyyMMddHHmmss}";

            // Routing is resolved per destination and probes are pinned to the source
            // address it reports, so every measurement records which link carried it.
            MeasurementMarker.Clear(_outputRoot);

            await using var probeSource = new NetworkProbeSource(
                ProbeOptions.Default,
                inspector,
                clock: null,
                new RouteResolver(),
                BoundPing.Instance,

                // A measurement started from this window - or from a console beside it -
                // saturates the line on purpose, so that period is excluded from assessment
                // rather than recorded as delay and loss.
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

            // Same traces the service takes, so a session recorded here is not weaker
            // evidence than one recorded by the service.
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

            // Stopped early means the user closed the window or pressed stop. The session
            // is still closed off properly rather than abandoned - unlike the service,
            // there is nothing here that could pick it up later.
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
#pragma warning disable CA1031 // Best effort: the raw evidence is already on disk either way.
        catch (Exception)
        {
            // The chain holds everything observed regardless of whether the closing entry
            // made it, and the report can be rebuilt from it later.
        }
#pragma warning restore CA1031
    }

    private void BuildReport(SessionPaths paths)
    {
        try
        {
            Evidence.EvidencePackage.Build(paths);
        }
#pragma warning disable CA1031 // A failed report must never discard a completed session.
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
