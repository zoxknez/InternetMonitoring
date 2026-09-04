using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Evidence;
using IEM.Linux.Composition;
using IEM.Presentation.Hosting;
using IEM.Storage;
using IEM.Storage.Evidence;
using IEM.Storage.Layout;

namespace IEM.App.Linux.Hosting;

internal sealed class LinuxPortableMonitorHost : IMonitorHost
{
    private readonly LinuxProductionComposition _composition;
    private readonly object _gate = new();
    private CancellationTokenSource? _sessionCancellation;
    private Task? _session;
    private MonitorSnapshot _lastSnapshot = MonitorSnapshot.Empty;

    public LinuxPortableMonitorHost()
    {
        _composition = LinuxProductionCompositionFactory.CreatePortable();
        EnsurePortableStateRoot(_composition.StateRoot);
    }

    public HostKind Kind => HostKind.InProcess;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _session is { IsCompleted: false };
            }
        }
    }

    public event Action<MonitorSnapshot>? Updated;
    public event Action<string?>? FaultChanged;

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> StartSessionAsync(
        TimeSpan duration,
        string? interfaceName,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_session is { IsCompleted: false })
            {
                return Task.FromResult(false);
            }

            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            _lastSnapshot = MonitorSnapshot.Empty;
            var session = Task.Run(
                () => RunSessionAsync(duration, interfaceName, _sessionCancellation.Token),
                CancellationToken.None);
            _session = session;
            _ = NotifyCompletionAsync(session);
        }

        FaultChanged?.Invoke(null);
        return Task.FromResult(true);
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? sessionCancellation;
        Task? session;

        lock (_gate)
        {
            sessionCancellation = _sessionCancellation;
            session = _session;
        }

        if (sessionCancellation is null || session is null)
        {
            return;
        }

        await sessionCancellation.CancelAsync().ConfigureAwait(false);
        await session.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSessionAsync(
        TimeSpan duration,
        string? interfaceName,
        CancellationToken cancellationToken)
    {
        EvidenceRecorder? recorder = null;
        SessionPaths? paths = null;
        MonitorEngine? engine = null;

        try
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var sessionId = $"S{startedUtc:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..31];
            var outputRoot = _composition.StorageLayout.DefaultOutputRoot;
            var selection = string.IsNullOrWhiteSpace(interfaceName)
                ? InterfaceSelectionRequest.ForAuto()
                : InterfaceSelectionRequest.ForExplicit(interfaceName);

            await using var linkInspection = await _composition.ProbeFactory
                .CreateLinkInspectionAsync(selection)
                .ConfigureAwait(false);
            var identity = linkInspection.Identity;
            var inspector = linkInspection.Inspector;
            var link = inspector.Inspect();
            paths = SessionPaths.ForNewSession(outputRoot, startedUtc.ToLocalTime());

            var layout = SessionLayoutDescriptor.CreateStandard(sessionId);
            var provision = await _composition.StorageProtectionProvider
                .ProvisionSessionBoundariesAsync(paths.Directory, layout, cancellationToken)
                .ConfigureAwait(false);
            if (provision.ProtectionState != StorageProtectionState.Established)
            {
                throw new InvalidOperationException(
                    $"Zaštita direktorijuma sesije nije uspostavljena: {provision.DiagnosticMessage}");
            }

            var verification = await _composition.StorageProtectionProvider
                .VerifyStorageProtectionAsync(paths.Directory, layout, cancellationToken)
                .ConfigureAwait(false);
            if (verification.ProtectionState != StorageProtectionState.Established)
            {
                throw new InvalidOperationException(
                    $"Zaštita direktorijuma sesije nije potvrđena: {verification.DiagnosticMessage}");
            }

            MeasurementMarker.Clear(outputRoot);
            await using var observer = _composition.ProbeFactory.CreateObserver();
            var routes = _composition.ProbeFactory.CreateRouteResolver(identity, observer);
            var boundIcmp = _composition.ProbeFactory.CreateBoundIcmp();
            await using var probeSource = new NetworkProbeSource(
                ProbeOptions.Default,
                inspector,
                clock: null,
                routes,
                boundIcmp,
                () => MeasurementMarker.IsHeld(outputRoot),
                observer);

            engine = new MonitorEngine(probeSource);
            var start = new SessionStartPayload(
                sessionId,
                typeof(LinuxPortableMonitorHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                startedUtc,
                duration,
                Environment.MachineName,
                !string.IsNullOrWhiteSpace(identity.InterfaceName) ? identity.InterfaceName : link.InterfaceName,
                link.Medium,
                link.LinkSpeedBitsPerSecond,
                link.GatewayAddress,
                identity.InterfaceId);
            recorder = EvidenceRecorder.Start(paths, engine, start);

            await using var tracer = new IncidentPathTracer();
            tracer.TraceCompleted += recorder.RecordTrace;
            tracer.Attach(engine);

            var planned = duration == Timeout.InfiniteTimeSpan ? null : (TimeSpan?)duration;
            var initial = new MonitorSnapshot
            {
                SessionId = sessionId,
                Directory = paths.Directory,
                StartedUtc = startedUtc,
                PlannedDuration = planned,
                InterfaceName = start.InterfaceName,
                Medium = start.Medium,
                GatewayAddress = start.GatewayAddress,
            };
            Publish(initial);

            engine.SampleRecorded += sample => Publish(
                MonitorSnapshot.From(engine, sample, sessionId, paths.Directory) with
                {
                    StartedUtc = startedUtc,
                    PlannedDuration = planned,
                });

            try
            {
                await engine.RunAsync(duration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A user-requested stop finalizes the evidence package; it is not a host fault.
            }

            await tracer.DisposeAsync().ConfigureAwait(false);
            await probeSource.DisposeAsync().ConfigureAwait(false);

            recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);
            EvidencePackage.Build(paths);
        }
#pragma warning disable CA1031 // A portable-session failure is surfaced to the window, not the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            CompleteQuietly(recorder, engine);
            FaultChanged?.Invoke($"Prenosivi nadzor nije završen kako je planirano: {ex.Message}");
        }
        finally
        {
            recorder?.Dispose();
        }
    }

    private void Publish(MonitorSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        Updated?.Invoke(snapshot);
    }

    private async Task NotifyCompletionAsync(Task session)
    {
        await session.ConfigureAwait(false);

        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }
        }

        Updated?.Invoke(_lastSnapshot);
    }

    private static void CompleteQuietly(EvidenceRecorder? recorder, MonitorEngine? engine)
    {
        if (recorder is null || engine is null)
        {
            return;
        }

        try
        {
            recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);
        }
        catch
        {
            // The original failure remains authoritative.
        }
    }

    private static void EnsurePortableStateRoot(string stateRoot)
    {
        if (Directory.Exists(stateRoot))
        {
            return;
        }

        Directory.CreateDirectory(stateRoot);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                stateRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopSessionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sessionCancellation?.Dispose();
        }
    }
}
