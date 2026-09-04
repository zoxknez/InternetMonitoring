using System.Text;
using IEM.Core;
using IEM.Core.Hosting;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Evidence;
using IEM.Storage;
using IEM.Storage.Evidence;
using IEM.Storage.Layout;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IEM.Service.Runtime;

/// <summary>
/// Platform-neutral monitoring session worker.
/// Owns a monitoring session for as long as the host process is running.
/// Invariants 211 and 275: One Evidence Engine, injected platform adapters.
/// </summary>
public sealed class MonitorWorker(
    IOptions<MonitorSettings> settings,
    ILogger<MonitorWorker> logger,
    IPlatformProbeFactory probeFactory,
    IPowerEventSource powerEvents,
    IPlatformStorageLayout storageLayout,
    IHostApplicationLifetime lifetime,
    IStorageProtectionProvider storageProtection) : BackgroundService, IMonitorSessionController
{
    private readonly MonitorSettings _settings = settings.Value;
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly SemaphoreSlim _sessionSignal = new(0, 1);
    private CancellationTokenSource? _activeSessionCancellation;
    private SessionStopDisposition _requestedStopDisposition;
    private bool _startPending;
    private bool _paused;
    private bool _autoStartEvaluated;

    /// <summary>Live state for anything asking over the status pipe/transport.</summary>
    public ServiceStatus Status { get; private set; } = ServiceStatus.Idle;

    /// <summary>
    /// The most recent measurement snapshot, for an interface to display.
    /// </summary>
    public MonitorSnapshot Live { get; private set; } = MonitorSnapshot.Empty;

    public async Task<SessionControlResult> StartSessionAsync(
        string sessionId,
        TimeSpan duration,
        string? interfaceName,
        string ownerPrincipalRef,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sessionId))
        {
            return new SessionControlResult(
                SessionControlOutcome.InvalidRequest,
                null,
                "Identifikator sesije mora imati 1-64 ASCII slova, cifre, tačke, donje crte ili crtice.");
        }

        if ((duration <= TimeSpan.Zero && duration != Timeout.InfiniteTimeSpan) ||
            duration > TimeSpan.FromDays(31))
        {
            return new SessionControlResult(
                SessionControlOutcome.InvalidRequest,
                sessionId,
                "Trajanje mora biti pozitivno, najviše 31 dan, ili beskonačno.");
        }

        if (string.IsNullOrWhiteSpace(ownerPrincipalRef))
        {
            return new SessionControlResult(
                SessionControlOutcome.InvalidRequest,
                sessionId,
                "Vlasnik sesije nije utvrđen.");
        }

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_paused && !string.IsNullOrWhiteSpace(Status.SessionId))
            {
                if (!string.Equals(sessionId, Status.SessionId, StringComparison.Ordinal))
                {
                    return new SessionControlResult(
                        SessionControlOutcome.Conflict,
                        Status.SessionId,
                        "Druga, pauzirana sesija čeka nastavak ili završavanje.");
                }

                _paused = false;
                _startPending = true;
                SignalSessionLoop();
                return new SessionControlResult(
                    SessionControlOutcome.Accepted,
                    Status.SessionId,
                    "Nastavak sesije je prihvaćen.");
            }

            if (_startPending || Status.State is SessionState.Running or SessionState.Finalizing)
            {
                return new SessionControlResult(
                    SessionControlOutcome.Conflict,
                    Status.SessionId,
                    "Sesija je već aktivna ili se upravo pokreće/završava.");
            }

            var outputRoot = _settings.ResolveOutputRoot(storageLayout.DefaultOutputRoot);
            new SessionRequest(
                duration,
                string.IsNullOrWhiteSpace(interfaceName) ? null : interfaceName.Trim(),
                DateTimeOffset.UtcNow,
                sessionId,
                ownerPrincipalRef).Write(outputRoot);

            _startPending = true;
            SignalSessionLoop();

            return new SessionControlResult(
                SessionControlOutcome.Accepted,
                sessionId,
                "Pokretanje sesije je prihvaćeno.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SessionControlResult(
                SessionControlOutcome.InvalidRequest,
                sessionId,
                $"Zahtev za sesiju nije sačuvan: {ex.Message}");
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public Task<SessionControlResult> PauseSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken) =>
        StopActiveSessionAsync(sessionId, SessionStopDisposition.Pause, cancellationToken);

    public Task<SessionControlResult> FinalizeSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken) =>
        StopActiveSessionAsync(sessionId, SessionStopDisposition.Finalize, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                await _controlGate.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    _activeSessionCancellation = sessionCancellation;
                    _requestedStopDisposition = SessionStopDisposition.None;
                }
                finally
                {
                    _controlGate.Release();
                }

                var ranSession = false;
                try
                {
                    ranSession = await RunSessionAsync(sessionCancellation.Token, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    await _controlGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        _activeSessionCancellation = null;
                        _requestedStopDisposition = SessionStopDisposition.None;
                        _startPending = false;
                    }
                    finally
                    {
                        _controlGate.Release();
                    }
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                if (!ranSession || _paused)
                {
                    await WaitForSessionSignalAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown.
        }
#pragma warning disable CA1031 // Handled deliberately: the process is brought down below.
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Nadzor je prekinut zbog neočekivane greške. Proces se zaustavlja sa greškom, " +
                "kako bi ga host ponovo pokrenuo i sesija se nastavila.");

            Status = Status with { Fault = ex.Message, State = SessionState.Interrupted };

            Environment.ExitCode = FatalExitCode;
            lifetime.StopApplication();
            return;
        }
#pragma warning restore CA1031

    }

    /// <summary>
    /// Exit code used when the engine fails.
    /// </summary>
    public const int FatalExitCode = 3;

    private async Task<bool> RunSessionAsync(
        CancellationToken sessionToken,
        CancellationToken hostStoppingToken)
    {
        var outputRoot = _settings.ResolveOutputRoot(storageLayout.DefaultOutputRoot);
        var now = DateTimeOffset.UtcNow;

        // Stage A: Session Intent
        var intent = ResolveSessionIntent(outputRoot, now);
        if (intent.Kind == SessionIntentKind.Idle)
        {
            logger.LogInformation(
                "Nema aktivne sesije. Servis je pokrenut i čeka u stanju mirovanja.");

            if (Status.State == SessionState.Idle)
            {
                Status = ServiceStatus.Idle;
            }

            return false;
        }

        // Stage B: Platform Resolution via Factory Scope
        await using var linkInspection = await probeFactory.CreateLinkInspectionAsync(intent.SelectionRequest).ConfigureAwait(false);
        var identity = linkInspection.Identity;
        var inspector = linkInspection.Inspector;

        // Stage C: Pinned Session Construction
        SessionPlan plan;
        if (intent.Kind == SessionIntentKind.Resumable)
        {
            var analysis = intent.Analysis!;
            var layoutDesc = SessionLayoutDescriptor.CreateStandard(analysis.Start!.SessionId);
            var verObs = await storageProtection.VerifyStorageProtectionAsync(analysis.Paths!.Directory, layoutDesc, sessionToken).ConfigureAwait(false);
            if (verObs.ProtectionState != StorageProtectionState.Established)
            {
                logger.LogError("Nastavak sesije '{SessionId}' je odbijen jer granica zaštite nije Established: {Error}",
                    analysis.Start.SessionId, verObs.DiagnosticMessage);
                return false;
            }

            plan = new SessionPlan(
                analysis.Paths!,
                analysis.Start!.SessionId,
                analysis.Start.StartedUtc,
                analysis.Start.PlannedDuration,
                analysis.Remaining,
                analysis.Context,
                Start: null);
        }
        else
        {
            var req = intent.Request!;
            var paths = SessionPaths.ForNewSession(outputRoot, now.ToLocalTime());
            var link = inspector.Inspect();
            var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
                ? $"S{now.ToLocalTime():yyyyMMddHHmmss}"
                : req.SessionId;

            var start = new SessionStartPayload(
                sessionId,
                typeof(MonitorWorker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                now,
                req.Duration,
                Environment.MachineName,
                !string.IsNullOrWhiteSpace(identity.InterfaceName) ? identity.InterfaceName : link.InterfaceName,
                link.Medium,
                link.LinkSpeedBitsPerSecond,
                link.GatewayAddress,
                identity.InterfaceId);

            plan = new SessionPlan(paths, sessionId, now, req.Duration, req.Duration, Resume: null, Start: start);
        }

        // Invariant 81: Storage boundary must be Established before creating probes or recorder
        var sessionLayout = SessionLayoutDescriptor.CreateStandard(plan.SessionId);
        if (plan.Resume is null)
        {
            var provObs = await storageProtection.ProvisionSessionBoundariesAsync(plan.Paths.Directory, sessionLayout, sessionToken).ConfigureAwait(false);
            if (provObs.ProtectionState != StorageProtectionState.Established)
            {
                logger.LogCritical("Sigurnosna granica sesije nije uspostavljena (Provision): {Error}", provObs.DiagnosticMessage);
                throw new InvalidOperationException($"Storage boundary provision failed: {provObs.DiagnosticMessage}");
            }
        }

        var boundaryCheck = await storageProtection.VerifyStorageProtectionAsync(plan.Paths.Directory, sessionLayout, sessionToken).ConfigureAwait(false);
        if (boundaryCheck.ProtectionState != StorageProtectionState.Established)
        {
            logger.LogCritical("Sigurnosna granica sesije nije verifikovana (Verify): {Error}", boundaryCheck.DiagnosticMessage);
            throw new InvalidOperationException($"Storage boundary verification failed: {boundaryCheck.DiagnosticMessage}");
        }

        MeasurementMarker.Clear(outputRoot);

        await using var observer = probeFactory.CreateObserver();
        var routes = probeFactory.CreateRouteResolver(identity, observer);
        var boundIcmp = probeFactory.CreateBoundIcmp();

        await using var probeSource = new NetworkProbeSource(
            ProbeOptions.Default,
            inspector,
            clock: null,
            routes,
            boundIcmp,
            () => MeasurementMarker.IsHeld(outputRoot),
            observer);

        var engine = new MonitorEngine(probeSource, MonitorOptions.Default, resume: plan.Resume);

        using var suspendSubscription = powerEvents.OnSuspending(engine.NotifySuspending);

        var recorder = plan.Resume is null
            ? EvidenceRecorder.Start(plan.Paths, engine, plan.Start!)
            : EvidenceRecorder.Resume(plan.Paths, engine, plan.SessionId);

        await using var tracer = new IncidentPathTracer();
        tracer.TraceCompleted += recorder.RecordTrace;
        tracer.Attach(engine);

        var finished = false;

        try
        {
            Status = new ServiceStatus(
                State: SessionState.Running,
                SessionId: plan.SessionId,
                Directory: plan.Paths.Directory,
                StartedUtc: plan.StartedUtc,
                PlannedDuration: plan.PlannedDuration,
                Resumed: plan.Resume is not null);
            _startPending = false;

            LogSessionStart(plan);
            Subscribe(engine);

            engine.SampleRecorded += sample =>
                Live = MonitorSnapshot.From(engine, sample, plan.SessionId, plan.Paths.Directory) with
                {
                    PlannedDuration = plan.PlannedDuration == Timeout.InfiniteTimeSpan
                        ? null
                        : plan.PlannedDuration,
                    StartedUtc = plan.StartedUtc,
                };

            try
            {
                await engine.RunAsync(plan.Remaining, sessionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
                // Control commands and host shutdown cancel the active engine deliberately.
                // CompleteSession below decides whether the evidence stays open or is finalized.
            }

            SetFinalizeStep(FinalizeStep.StoppingProbes);
            await tracer.DisposeAsync().ConfigureAwait(false);
            await probeSource.DisposeAsync().ConfigureAwait(false);

            finished = CompleteSession(
                engine,
                recorder,
                plan,
                outputRoot,
                hostStoppingToken.IsCancellationRequested
                    ? SessionStopDisposition.HostShutdown
                    : GetRequestedStopDisposition());
        }
        finally
        {
            recorder.Dispose();

            if (recorder.RefusedAfterClose > 0)
            {
                logger.LogInformation(
                    "Odbijeno {Count} zapisa koji su stigli posle zatvaranja sesije (poslednji: {Kind}). " +
                    "Sirova evidencija se završava zapisom o kraju sesije, kako i treba.",
                    recorder.RefusedAfterClose,
                    recorder.LastRefusedKind);
            }
        }

        if (finished && _settings.BuildReportOnCompletion)
        {
            SetFinalizeStep(FinalizeStep.BuildingReport);
            BuildReport(plan.Paths);
            Status = Status with { State = SessionState.Completed, FinalizeStep = FinalizeStep.Done };
        }

        return true;
    }

    private void BuildReport(SessionPaths paths)
    {
        try
        {
            var package = EvidencePackage.Build(paths);
            logger.LogInformation("Izveštaj je napravljen: {Report}.", package.ZipPath ?? package.Directory);
        }
#pragma warning disable CA1031 // A failed report must never discard a completed session.
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Izveštaj nije napravljen, ali sirova evidencija je sačuvana u {Directory}. " +
                "Izveštaj se može napraviti naknadno.",
                paths.Directory);
        }
#pragma warning restore CA1031
    }

    private void Subscribe(MonitorEngine engine)
    {
        engine.IncidentClosed += incident => logger.LogWarning(
            "Prekid #{Number}: {State}, trajanje {Duration}, uzrok kod: {Attribution}.",
            incident.Number,
            incident.WorstState.Label(),
            SerbianText.Duration(incident.DurationReported),
            incident.WorstState.AttributionOf().Label());

        engine.GapDetected += gap => logger.LogInformation(
            "Nadzor pauziran {Duration} ({Cause}). Ne računa se kao prekid veze.",
            SerbianText.Duration(gap.Duration),
            gap.Cause);

        engine.ClockAnomalyDetected += observation => logger.LogWarning(
            "Sistemski sat je pomeren za {Skew}. Trajanja se mere nezavisnim brojačem i ostaju tačna.",
            SerbianText.Duration(observation.Skew.Duration()));
    }

    private enum SessionIntentKind { Idle, Resumable, NewSession }

    private readonly record struct SessionIntent(
        SessionIntentKind Kind,
        InterfaceSelectionRequest SelectionRequest,
        ResumeAnalysis? Analysis = null,
        SessionRequest? Request = null);

    private SessionIntent ResolveSessionIntent(string outputRoot, DateTimeOffset now)
    {
        var mayAutoStart = !_autoStartEvaluated;
        _autoStartEvaluated = true;

        if (_settings.ResumeUnfinished)
        {
            var analysis = SessionResumeAnalyzer.Analyze(outputRoot, now);

            switch (analysis.Decision)
            {
                case ResumeDecision.Resumable:
                    return new SessionIntent(
                        SessionIntentKind.Resumable,
                        InterfaceSelectionRequest.ForResume(
                            analysis.Start?.InterfaceId,
                            analysis.Start?.InterfaceName,
                            analysis.Start?.SchemaVersion ?? IEM.Core.Model.EvidenceModelVersion.LegacySchemaVersion),
                        Analysis: analysis);

                case ResumeDecision.Expired:
                    CloseExpiredSession(analysis);
                    SessionRequest.Clear(outputRoot);
                    break;

                case ResumeDecision.IntegrityCompromised:
                    MarkCompromised(analysis);
                    break;

                default:
                    break;
            }
        }

        var request = SessionRequest.Read(outputRoot) ?? (mayAutoStart ? AutoRequest(outputRoot, now) : null);
        if (request is not null)
        {
            var sel = string.IsNullOrWhiteSpace(request.Interface)
                ? InterfaceSelectionRequest.ForAuto()
                : InterfaceSelectionRequest.ForExplicit(request.Interface);

            return new SessionIntent(SessionIntentKind.NewSession, sel, Request: request);
        }

        return new SessionIntent(SessionIntentKind.Idle, InterfaceSelectionRequest.ForAuto());
    }

    private SessionRequest? AutoRequest(string outputRoot, DateTimeOffset now)
    {
        if (!_settings.AutoStart)
        {
            return null;
        }

        var request = new SessionRequest(_settings.ResolveDuration(), _settings.Interface, now);
        request.Write(outputRoot);

        logger.LogInformation("Sesija je zatražena automatski, prema podešavanju AutoStart.");
        return request;
    }

    private void CloseExpiredSession(ResumeAnalysis analysis)
    {
        if (analysis.Paths is null)
        {
            return;
        }

        logger.LogInformation(
            "Pronađena je nedovršena sesija kojoj je isteklo planirano trajanje. " +
            "Zatvara se sa prikupljenim podacima, pa se pokreće nova.");

        try
        {
            if (AbandonedSessionCloser.Close(analysis.Paths, DateTimeOffset.UtcNow) &&
                _settings.BuildReportOnCompletion)
            {
                EvidencePackage.Build(analysis.Paths);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            logger.LogError(ex, "Zatvaranje istekle sesije u {Directory} nije uspelo.", analysis.Paths.Directory);
        }
#pragma warning restore CA1031
    }

    private void MarkCompromised(ResumeAnalysis analysis)
    {
        logger.LogError(
            "Nedovršena sesija u {Directory} ima neispravan lanac dokaza. " +
            "Folder se ostavlja netaknut radi uvida i pokreće se nova sesija.",
            analysis.Paths?.Directory);
    }

    private bool CompleteSession(
        MonitorEngine engine,
        EvidenceRecorder recorder,
        SessionPlan plan,
        string outputRoot,
        SessionStopDisposition disposition)
    {
        if (disposition is SessionStopDisposition.Pause or SessionStopDisposition.HostShutdown)
        {
            logger.LogInformation(
                disposition == SessionStopDisposition.Pause
                    ? "Sesija {SessionId} je pauzirana i ostaje otvorena za nastavak."
                    : "Servis se zaustavlja pre isteka trajanja. Sesija {SessionId} ostaje otvorena " +
                      "i biće nastavljena pri sledećem pokretanju servisa.",
                plan.SessionId);

            Status = Status with { State = SessionState.Interrupted };
            SetFinalizeStep(FinalizeStep.Done);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var stats = engine.Statistics;

        SetFinalizeStep(FinalizeStep.WritingEvidence);

        logger.LogInformation(
            disposition == SessionStopDisposition.Finalize
                ? "Zatraženo je završavanje sesije {SessionId}. Sesija se zatvara."
                : "Planirano trajanje sesije {SessionId} je isteklo. Sesija se zatvara.",
            plan.SessionId);

        recorder.Complete(stats, now);
        SessionRequest.Clear(outputRoot);

        SetFinalizeStep(FinalizeStep.VerifyingChain);
        var verification = ChainVerifier.Verify(plan.Paths.RawLog);

        if (!verification.Valid)
        {
            logger.LogCritical(
                "Lanac dokaza nije validan nakon zatvaranja sesije: {Reason}. " +
                "Paket dokaza ne može biti potpisan.",
                verification.Reason);

            Status = Status with { State = SessionState.Interrupted, Fault = verification.Reason };
            SetFinalizeStep(FinalizeStep.Done);
            return false;
        }

        Status = Status with { State = SessionState.Completed };
        SetFinalizeStep(FinalizeStep.Done);
        return true;
    }

    private void LogSessionStart(SessionPlan plan)
    {
        if (plan.Resume is not null)
        {
            logger.LogInformation(
                "Nastavlja se postojeća sesija {SessionId}. Preostalo vreme: {Remaining}.",
                plan.SessionId,
                Describe(plan.Remaining));
        }
        else
        {
            logger.LogInformation(
                "Započeta nova sesija {SessionId}. Planirano trajanje: {Duration}.",
                plan.SessionId,
                Describe(plan.PlannedDuration));
        }
    }

    private void SetFinalizeStep(FinalizeStep step)
    {
        Status = Status with
        {
            State = step is FinalizeStep.None or FinalizeStep.Done ? Status.State : SessionState.Finalizing,
            FinalizeStep = step,
        };
    }

    private async Task<SessionControlResult> StopActiveSessionAsync(
        string? sessionId,
        SessionStopDisposition disposition,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeSessionCancellation is null || Status.State != SessionState.Running)
            {
                return new SessionControlResult(
                    SessionControlOutcome.NotFound,
                    Status.SessionId,
                    "Nema aktivne sesije kojom se može upravljati.");
            }

            if (!string.IsNullOrWhiteSpace(sessionId) &&
                !string.Equals(sessionId, Status.SessionId, StringComparison.Ordinal))
            {
                return new SessionControlResult(
                    SessionControlOutcome.NotFound,
                    Status.SessionId,
                    "Aktivna sesija nema traženi identifikator.");
            }

            if (_requestedStopDisposition != SessionStopDisposition.None)
            {
                return new SessionControlResult(
                    SessionControlOutcome.Conflict,
                    Status.SessionId,
                    "Sesija se već zaustavlja ili završava.");
            }

            _requestedStopDisposition = disposition;
            _paused = disposition == SessionStopDisposition.Pause;
            cancellation = _activeSessionCancellation;
        }
        finally
        {
            _controlGate.Release();
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        return new SessionControlResult(
            SessionControlOutcome.Accepted,
            Status.SessionId,
            disposition == SessionStopDisposition.Pause
                ? "Pauziranje sesije je prihvaćeno."
                : "Završavanje sesije je prihvaćeno.");
    }

    private async Task WaitForSessionSignalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _sessionSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ordinary service shutdown while idle or paused.
        }
    }

    private void SignalSessionLoop()
    {
        if (_sessionSignal.CurrentCount == 0)
        {
            _sessionSignal.Release();
        }
    }

    private SessionStopDisposition GetRequestedStopDisposition() => _requestedStopDisposition;

    private static bool IsValidSessionId(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value is not "." and not ".." &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '.' or '_' or '-');

    private static string Describe(TimeSpan duration) =>
        duration == Timeout.InfiniteTimeSpan ? "do prekida" : SerbianText.Duration(duration);

    private enum SessionStopDisposition
    {
        None,
        Pause,
        Finalize,
        HostShutdown,
    }

    private sealed record SessionPlan(
        SessionPaths Paths,
        string SessionId,
        DateTimeOffset StartedUtc,
        TimeSpan PlannedDuration,
        TimeSpan Remaining,
        ResumeContext? Resume,
        SessionStartPayload? Start);
}

public enum SessionState
{
    Idle,
    Running,
    Finalizing,
    Interrupted,
    Completed,
}

public enum FinalizeStep
{
    None,
    StoppingProbes,
    WritingEvidence,
    VerifyingChain,
    BuildingReport,
    Done,
}

public static class FinalizeStepInfo
{
    public static string Label(this FinalizeStep step) => step switch
    {
        FinalizeStep.StoppingProbes => "Završavanje nadzora…",
        FinalizeStep.WritingEvidence => "Upisivanje dokaza…",
        FinalizeStep.VerifyingChain => "Provera dokaza…",
        FinalizeStep.BuildingReport => "Pravljenje izveštaja…",
        FinalizeStep.Done => "Završeno.",
        _ => string.Empty,
    };
}

public sealed record ServiceStatus(
    SessionState State,
    string? SessionId,
    string? Directory,
    DateTimeOffset? StartedUtc,
    TimeSpan? PlannedDuration,
    bool Resumed)
{
    public static readonly ServiceStatus Idle = new(SessionState.Idle, null, null, null, null, false);

    public string? Fault { get; init; }

    public FinalizeStep FinalizeStep { get; init; }

    public string? FinalizeMessage =>
        FinalizeStep == FinalizeStep.None ? null : FinalizeStep.Label();
}
