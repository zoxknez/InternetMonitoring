using System.Text;
using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Evidence;
using IEM.Storage;
using IEM.Storage.Evidence;
using IEM.Windows;
using Microsoft.Extensions.Options;

namespace IEM.Service;

/// <summary>
/// Owns a monitoring session for as long as the service is running.
/// <para>
/// The reason this is a service at all: a two-day test cannot depend on a window staying
/// open, on a user staying logged in, or on the machine not restarting. Everything here
/// is written so that stopping and starting again continues the same session rather than
/// quietly beginning a new one.
/// </para>
/// </summary>
public sealed class MonitorWorker(
    IOptions<MonitorSettings> settings,
    ILogger<MonitorWorker> logger,
    PowerEventBroker powerEvents,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly MonitorSettings _settings = settings.Value;

    /// <summary>Live state for anything asking over the status pipe.</summary>
    public ServiceStatus Status { get; private set; } = ServiceStatus.Idle;

    /// <summary>
    /// The most recent measurement, for an interface to display.
    /// <para>
    /// Written from the sampling thread and read from the pipe thread. A record is
    /// replaced whole rather than mutated, so a reader always sees one coherent moment
    /// instead of a mixture of two.
    /// </para>
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
                "kako bi ga Windows ponovo pokrenuo i sesija se nastavila.");

            Status = Status with { Fault = ex.Message, State = SessionState.Interrupted };

            // Exits with a failure code rather than tidily.
            //
            // Tidily was the bug: the service was registered with a restart-on-failure
            // action, but a process that logs an error and then returns zero has not failed
            // as far as the service manager is concerned. It would simply stop - and a
            // two-day test would sit dead with nothing restarting it and nothing on screen
            // saying so. Exiting non-zero is what makes that recovery action fire, and the
            // session resumes from the chain on the next start.
            Environment.ExitCode = FatalExitCode;

            lifetime.StopApplication();
            return;
        }
#pragma warning restore CA1031

        // The service exists to run a session. Once there is none left to run it stops,
        // rather than sitting idle and giving the impression a test is still under way -
        // but not while a measurement is still standing on the calendar.
        if (!stoppingToken.IsCancellationRequested)
        {
            await WaitForScheduledMeasurementAsync(stoppingToken).ConfigureAwait(false);
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// Holds the service open while a scheduled speed measurement is still pending.
    /// <para>
    /// Stopping out from under it would put scheduling back exactly where it was before the
    /// service took it over: in a process that has to stay open until the moment arrives.
    /// The instruction on disk is the shared truth here, as it is for sessions - the worker
    /// that carries it out removes the file, whether it measured or refused.
    /// </para>
    /// </summary>
    private async Task WaitForScheduledMeasurementAsync(CancellationToken stoppingToken)
    {
        var outputRoot = _settings.ResolveOutputRoot();

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
    /// Exit code used when the engine fails. Any non-zero value makes the service manager
    /// treat the exit as a failure; a distinct one makes it identifiable in the event log.
    /// </summary>
    public const int FatalExitCode = 3;

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        var outputRoot = _settings.ResolveOutputRoot();
        Directory.CreateDirectory(outputRoot);

        // Built before the session is decided, because opening a new one needs to record
        // what kind of link it is watching. Wireless detail comes through the Windows
        // layer; without it a dropped Wi-Fi adapter cannot be told apart from a router
        // that stopped broadcasting.
        await using var linkInspection = WindowsLinkInspection.Create(_settings.Interface);

        var plan = DecideSession(outputRoot, linkInspection.Inspector);
        if (plan is null)
        {
            logger.LogInformation(
                "Nema zatražene sesije. Servis se zaustavlja. " +
                "Sesiju pokrenite komandom: InternetEvidenceService.exe start-session 48h");

            return;
        }

        // Routing is resolved per destination and probes are pinned to the source address it
        // reports, so every measurement records which link actually carried it.
        // A mark left behind by a process that was killed mid-measurement would otherwise
        // suspend assessment for the first two minutes of this session.
        MeasurementMarker.Clear(outputRoot);

        await using var probeSource = new NetworkProbeSource(
            ProbeOptions.Default,
            linkInspection.Inspector,
            clock: null,
            new RouteResolver(),
            BoundPing.Instance,

            // A speed measurement fills the line on purpose. While one runs - here, in the
            // window, or in a console - the period is excluded from assessment instead of
            // being recorded as the delay and loss it certainly is.
            () => MeasurementMarker.IsHeld(outputRoot));

        var engine = new MonitorEngine(probeSource, MonitorOptions.Default, resume: plan.Resume);

        // A suspend the operating system announces is worth more than any guess made
        // afterwards from clock readings, so it is handed straight to the engine.
        using var suspendSubscription = powerEvents.OnSuspending(engine.NotifySuspending);

        var recorder = plan.Resume is null
            ? EvidenceRecorder.Start(plan.Paths, engine, plan.Start!)
            : EvidenceRecorder.Resume(plan.Paths, engine, plan.SessionId);

        // Traces run off the sampling path and land in the chain when they finish. They
        // are what turns "nothing answered" into "the path stopped at the operator's edge".
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

            // Step one, before anything is written: stop probing and let whatever is in
            // flight finish. A TCP connect takes up to two seconds and a path trace tens,
            // so one begun a moment before the stop would otherwise land in the chain after
            // the entry that closes it.
            SetFinalizeStep(FinalizeStep.StoppingProbes);
            await tracer.DisposeAsync().ConfigureAwait(false);
            await probeSource.DisposeAsync().ConfigureAwait(false);

            finished = CompleteSession(engine, recorder, plan, outputRoot, stoppingToken.IsCancellationRequested);
        }
        finally
        {
            // Closed here and nowhere else, so ownership is unambiguous and the database
            // is released before anything tries to read it back.
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

    /// <summary>
    /// Decides whether to continue an interrupted session or open a new one.
    /// <para>
    /// Continuing is preferred, because a service restart part-way through a test is the
    /// ordinary case. A session whose planned window has already passed is closed out with
    /// what it collected rather than extended: silently lengthening the observation period
    /// would misrepresent what was measured, and the evidence for the original window is
    /// perfectly good on its own.
    /// </para>
    /// </summary>
    private SessionPlan? DecideSession(string outputRoot, ILinkInspector linkInspector)
    {
        var now = DateTimeOffset.UtcNow;

        if (_settings.ResumeUnfinished)
        {
            var analysis = SessionResumeAnalyzer.Analyze(outputRoot, now);

            switch (analysis.Decision)
            {
                case ResumeDecision.Resumable:
                    return new SessionPlan(
                        analysis.Paths!,
                        analysis.Start!.SessionId,
                        analysis.Start.StartedUtc,
                        analysis.Start.PlannedDuration,
                        analysis.Remaining,
                        analysis.Context,
                        Start: null);

                case ResumeDecision.Expired:
                    CloseExpiredSession(analysis);
                    SessionRequest.Clear(outputRoot);
                    break;

                case ResumeDecision.IntegrityCompromised:
                    // The original is left exactly as found, read-only, and a fresh session
                    // begins beside it. Appending would bury the flaw deeper and lend the
                    // doubt to everything measured afterwards.
                    MarkCompromised(analysis);
                    break;

                default:
                    break;
            }
        }

        // Only start something new when someone actually asked for it. Anything else and
        // a reboot after a finished test would silently begin another one.
        var request = SessionRequest.Read(outputRoot) ?? AutoRequest(outputRoot, now);

        return request is null ? null : NewSession(outputRoot, linkInspector, request.Duration, now);
    }

    /// <summary>
    /// Creates a request from configuration when the service is set to start on its own.
    /// <para>
    /// Off by default. It exists for an unattended install where the machine is dedicated
    /// to measuring, and it is written to disk like any other request so the behaviour
    /// after a reboot is identical either way.
    /// </para>
    /// </summary>
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

    private SessionPlan NewSession(string outputRoot, ILinkInspector linkInspector, TimeSpan duration, DateTimeOffset now)
    {
        var paths = SessionPaths.ForNewSession(outputRoot, now.ToLocalTime());
        var link = linkInspector.Inspect();
        var sessionId = $"S{now.ToLocalTime():yyyyMMddHHmmss}";

        var start = new SessionStartPayload(
            sessionId,
            typeof(MonitorWorker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            now,
            duration,
            Environment.MachineName,
            link.InterfaceName,
            link.Medium,
            link.LinkSpeedBitsPerSecond,
            link.GatewayAddress);

        return new SessionPlan(paths, sessionId, now, duration, duration, Resume: null, Start: start);
    }

    /// <summary>
    /// Finishes off a session that ran out of time while the monitor was not running, so
    /// its evidence becomes a complete package instead of being stranded half-open.
    /// </summary>
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
#pragma warning disable CA1031 // Failing to tidy an old session must not prevent starting a new one.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nije uspelo zatvaranje ranije sesije u {Directory}.", analysis.Paths.Directory);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Sets aside a session whose raw chain no longer verifies.
    /// <para>
    /// Nothing inside the session directory is altered - an altered chain is itself a
    /// finding, and overwriting it would destroy the record of what happened. A marker file
    /// is written beside it saying why the session stopped, and the files are made read-only
    /// so nothing here adds to the damage.
    /// </para>
    /// </summary>
    private void MarkCompromised(ResumeAnalysis analysis)
    {
        if (analysis.Paths is null)
        {
            return;
        }

        var recovery = ChainVerifier.Recover(analysis.Paths.RawLog);

        logger.LogError(
            "Integritet sesije u {Directory} je narušen ({Break}). Sesija se ne nastavlja. " +
            "Original ostaje sačuvan samo za čitanje, a pokreće se nova sesija.",
            analysis.Paths.Directory,
            recovery.Break);

        try
        {
            File.WriteAllText(
                Path.Combine(analysis.Paths.Directory, "INTEGRITET-NARUSEN.txt"),
                $"""
                Integritet sirove evidencije ove sesije je narušen.

                Otkriveno:  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
                Razlog:     {recovery.BreakReason}
                Ispravnih zapisa pre prekida: {recovery.EntriesRecovered}

                Sesija se ne nastavlja. Nastavak bi značio da se novi zapisi
                nadovezuju na evidenciju koja je već izmenjena, pa bi sumnja
                prešla i na deo koji je bio ispravan.

                Fajlovi su ostavljeni nepromenjeni, samo za čitanje.

                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            foreach (var file in Directory.EnumerateFiles(analysis.Paths.Directory))
            {
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            }
        }
#pragma warning disable CA1031 // Failing to set a session aside must not prevent starting a new one.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nije uspelo obeležavanje sesije u {Directory}.", analysis.Paths.Directory);
        }
#pragma warning restore CA1031
    }

    private void LogSessionStart(SessionPlan plan)
    {
        if (plan.Resume is not null)
        {
            logger.LogInformation(
                "Nastavlja se sesija {SessionId} u {Directory}. Preostalo: {Remaining}. " +
                "Nadzor nije radio {Interruption}.",
                plan.SessionId,
                plan.Paths.Directory,
                Describe(plan.Remaining),
                SerbianText.Duration(plan.Resume.Interruption));

            return;
        }

        logger.LogInformation(
            "Pokrenuta je sesija {SessionId} u {Directory}. Planirano trajanje: {Duration}.",
            plan.SessionId,
            plan.Paths.Directory,
            Describe(plan.PlannedDuration));
    }

    /// <summary>
    /// Ends the session.
    /// <para>
    /// A session stopped because the service is shutting down is deliberately left open,
    /// so the next start continues it. Only a session that reached its planned end is
    /// closed and packaged - closing on every shutdown would turn a single two-day test
    /// into a scattering of unrelated fragments.
    /// </para>
    /// </summary>
    /// <returns>True when the session reached its planned end.</returns>
    private bool CompleteSession(
        MonitorEngine engine,
        EvidenceRecorder recorder,
        SessionPlan plan,
        string outputRoot,
        bool stoppingEarly)
    {
        if (stoppingEarly)
        {
            // The request stays on disk. It is what tells the next start that this session
            // is still wanted rather than finished.
            logger.LogInformation(
                "Servis se zaustavlja. Sesija {SessionId} ostaje otvorena i nastaviće se pri sledećem pokretanju.",
                plan.SessionId);

            Status = Status with { State = SessionState.Interrupted };
            return false;
        }

        // The boundaries and totals, forced to disk before anything reads them back.
        SetFinalizeStep(FinalizeStep.WritingEvidence);

        // Complete writes the closing entry and then re-reads the whole file to check it,
        // so the verification step is announced before the call rather than after it.
        SetFinalizeStep(FinalizeStep.VerifyingChain);
        var verification = recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);

        // The session reached its planned end, so the request is discharged. Leaving it
        // would start another full session the next time the machine boots.
        SessionRequest.Clear(outputRoot);

        logger.LogInformation(
            "Sesija {SessionId} je završena. Dostupnost {Availability:F4} %, prekida {Incidents} " +
            "(izolovano iza rutera {Upstream}). Integritet: {Integrity}.",
            plan.SessionId,
            engine.Statistics.AvailabilityPercent,
            engine.Statistics.IncidentCount,
            engine.Statistics.UpstreamIncidentCount,
            verification.Valid ? "ispravan" : "narušen");

        Status = Status with { State = SessionState.Completed };

        // The report is built by the caller, after the recorder has been closed, so the
        // database is not still held open when the report tries to read it back.
        return true;
    }

    /// <summary>
    /// Moves the session into <see cref="SessionState.Finalizing"/> and announces which step
    /// is running, so the interface shows progress instead of appearing to hang while the
    /// chain is verified and the archive is written.
    /// </summary>
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

    /// <param name="PlannedDuration">The whole session's planned length, not what is left of it.</param>
    /// <param name="Remaining">What is left to run now.</param>
    /// <param name="Start">Null when an existing session is being continued.</param>
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

    /// <summary>
    /// Monitoring has stopped and the evidence is being closed off.
    /// <para>
    /// A real state rather than a moment, because closing a session is a sequence with an
    /// order that matters and can take a noticeable while: stop launching probes, let the
    /// ones in flight finish or abandon them, write the boundaries, flush to disk, verify
    /// the chain, close the session, build the report. The interface says which step is
    /// running instead of appearing to hang.
    /// </para>
    /// </summary>
    Finalizing,

    /// <summary>Stopped before its planned end; will be continued on the next start.</summary>
    Interrupted,

    Completed,
}

/// <summary>Steps of closing a session, in the order they must happen.</summary>
public enum FinalizeStep
{
    /// <summary>Not closing anything.</summary>
    None,

    /// <summary>No new probes are started; the ones already sent are given a moment.</summary>
    StoppingProbes,

    /// <summary>Boundaries and totals are written and forced to disk.</summary>
    WritingEvidence,

    /// <summary>The chain is re-read and checked end to end.</summary>
    VerifyingChain,

    /// <summary>The readable report and the archive are produced.</summary>
    BuildingReport,

    Done,
}

public static class FinalizeStepInfo
{
    /// <summary>What the interface shows while each step runs.</summary>
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

    /// <summary>Which step of closing the session is running, when one is.</summary>
    public FinalizeStep FinalizeStep { get; init; }

    /// <summary>What to show the person watching, in Serbian.</summary>
    public string? FinalizeMessage =>
        FinalizeStep == FinalizeStep.None ? null : FinalizeStep.Label();
}
