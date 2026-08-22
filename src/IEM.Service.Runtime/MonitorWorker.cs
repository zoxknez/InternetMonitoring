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
    IStorageProtectionProvider storageProtection) : BackgroundService
{
    private readonly MonitorSettings _settings = settings.Value;

    /// <summary>Live state for anything asking over the status pipe/transport.</summary>
    public ServiceStatus Status { get; private set; } = ServiceStatus.Idle;

    /// <summary>
    /// The most recent measurement snapshot, for an interface to display.
    /// </summary>
    public MonitorSnapshot Live { get; private set; } = MonitorSnapshot.Empty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunSessionAsync(stoppingToken).ConfigureAwait(false);
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

        if (!stoppingToken.IsCancellationRequested)
        {
            await WaitForScheduledMeasurementAsync(stoppingToken).ConfigureAwait(false);
            lifetime.StopApplication();
        }
    }

    private async Task WaitForScheduledMeasurementAsync(CancellationToken stoppingToken)
    {
        var outputRoot = _settings.ResolveOutputRoot(storageLayout.DefaultOutputRoot);

        if (SpeedRequest.Read(outputRoot) is not { } pending)
        {
            return;
        }

        logger.LogInformation(
            "Nema više sesija, ali je merenje brzine zakazano za {Due}. Servis ostaje pokrenut do tada.",
            SerbianText.DateTime(pending.DueAtUtc.ToLocalTime()));

        while (!stoppingToken.IsCancellationRequested && SpeedRequest.Read(outputRoot) is not null)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Exit code used when the engine fails.
    /// </summary>
    public const int FatalExitCode = 3;

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        var outputRoot = _settings.ResolveOutputRoot(storageLayout.DefaultOutputRoot);
        var now = DateTimeOffset.UtcNow;

        // Stage A: Session Intent
        var intent = ResolveSessionIntent(outputRoot, now);
        if (intent.Kind == SessionIntentKind.Idle)
        {
            logger.LogInformation(
                "Nema aktivne sesije. Servis je pokrenut i čeka u stanju mirovanja.");

            Status = ServiceStatus.Idle;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected shutdown
            }

            return;
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
            var verObs = await storageProtection.VerifyStorageProtectionAsync(analysis.Paths!.Directory, layoutDesc, stoppingToken).ConfigureAwait(false);
            if (verObs.ProtectionState != StorageProtectionState.Established)
            {
                logger.LogError("Nastavak sesije '{SessionId}' je odbijen jer granica zaštite nije Established: {Error}",
                    analysis.Start.SessionId, verObs.DiagnosticMessage);
                return;
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
            var sessionId = $"S{now.ToLocalTime():yyyyMMddHHmmss}";

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
            var provObs = await storageProtection.ProvisionSessionBoundariesAsync(plan.Paths.Directory, sessionLayout, stoppingToken).ConfigureAwait(false);
            if (provObs.ProtectionState != StorageProtectionState.Established)
            {
                logger.LogCritical("Sigurnosna granica sesije nije uspostavljena (Provision): {Error}", provObs.DiagnosticMessage);
                throw new InvalidOperationException($"Storage boundary provision failed: {provObs.DiagnosticMessage}");
            }
        }

        var boundaryCheck = await storageProtection.VerifyStorageProtectionAsync(plan.Paths.Directory, sessionLayout, stoppingToken).ConfigureAwait(false);
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

            await engine.RunAsync(plan.Remaining, stoppingToken).ConfigureAwait(false);

            SetFinalizeStep(FinalizeStep.StoppingProbes);
            await tracer.DisposeAsync().ConfigureAwait(false);
            await probeSource.DisposeAsync().ConfigureAwait(false);

            finished = CompleteSession(engine, recorder, plan, outputRoot, stoppingToken.IsCancellationRequested);
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
        }
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

        var request = SessionRequest.Read(outputRoot) ?? AutoRequest(outputRoot, now);
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
        bool wasCancelled)
    {
        var now = DateTimeOffset.UtcNow;
        var stats = engine.Statistics;

        SetFinalizeStep(FinalizeStep.WritingEvidence);

        if (wasCancelled)
        {
            logger.LogInformation(
                "Servis se zaustavlja pre isteka trajanja. Sesija {SessionId} je prekinuta i biće " +
                "nastavljena pri sledećem pokretanju servisa.",
                plan.SessionId);

            recorder.Complete(stats, now);
            Status = Status with { State = SessionState.Interrupted };
            SetFinalizeStep(FinalizeStep.Done);
            return false;
        }

        logger.LogInformation("Planirano trajanje sesije {SessionId} je isteklo. Sesija se zatvara.", plan.SessionId);

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

    private static string Describe(TimeSpan duration) =>
        duration == Timeout.InfiniteTimeSpan ? "do prekida" : SerbianText.Duration(duration);

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
