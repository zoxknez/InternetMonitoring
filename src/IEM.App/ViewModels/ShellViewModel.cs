using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IEM.App.Controls;
using IEM.App.Hosting;
using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Core.Speed;
using IEM.Legal;
using IEM.Storage;
using IEM.Windows;

namespace IEM.App.ViewModels;

/// <param name="Label">What the user sees in the list.</param>
/// <param name="Note">
/// What the duration is for. The field is wide and a duration is three characters, so
/// without it four fifths of the control was empty - and the one thing a person choosing
/// here actually wants to know is which option matches their situation.
/// </param>
public sealed record DurationChoice(string Label, TimeSpan Duration, string Note);

/// <summary>
/// One figure and what it means, laid out as label on the left and value hard against the
/// right edge.
/// <para>
/// Built here rather than written out six times in the markup so every row is aligned the
/// same way by construction. Hand-placed figures drift, and a column of numbers that do not
/// line up is the fastest way to make measurements look casual.
/// </para>
/// </summary>
public sealed record Metric(string Label, string Value, string? Hint = null);

/// <param name="Colour">
/// Green when every attempt answered, red when none did, amber when some did, and grey when
/// the check was not attempted at all.
/// </param>
public sealed record ProbeState(string Name, string Detail, System.Windows.Media.Brush Colour);

/// <summary>
/// The window's state.
/// <para>
/// Deliberately thin. Everything that decides anything - what counts as an outage, who
/// is at fault, whether there is a case - lives in the core and is shared with the report
/// and the console. This class arranges those answers on screen and nothing more, so the
/// window and the exported document can never disagree.
/// </para>
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IMonitorHost _host;
    private readonly LiveHistory _history = new();
    private readonly string _outputRoot;

    /// <summary>Session the charts currently hold history for.</summary>
    private string? _attachedSession;

    [ObservableProperty]
    private MonitorSnapshot _snapshot = MonitorSnapshot.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _fault;

    [ObservableProperty]
    private DurationChoice _selectedDuration;

    [ObservableProperty]
    private IReadOnlyList<TimelineSlice> _timeline = [];

    [ObservableProperty]
    private IReadOnlyList<LatencyPoint> _latency = [];

    public ShellViewModel(IMonitorHost host, string outputRoot)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _outputRoot = outputRoot;
        _selectedDuration = Durations[3];

        _host.Updated += OnUpdated;
        _host.FaultChanged += OnHostFaultChanged;
    }

    /// <summary>
    /// How long to watch for.
    /// <para>
    /// Two days is the default because it usually catches a fault that repeats, not because
    /// anything prescribes it. The forty-eight hours in the quality pravilnik is the time an
    /// operator has to clear a fault once throughput has fallen below the minimum - a
    /// different thing entirely, and it was described here as if it were a rule about how
    /// long evidence has to be gathered.
    /// </para>
    /// </summary>
    public IReadOnlyList<DurationChoice> Durations { get; } =
    [
        new("1 sat", TimeSpan.FromHours(1), "brza provera"),
        new("6 sati", TimeSpan.FromHours(6), "popodne ili veče"),
        new("24 sata", TimeSpan.FromHours(24), "ceo dan i noć"),
        new("48 sati", TimeSpan.FromHours(48), "dva dana i dve noći"),
        new("72 sata", TimeSpan.FromHours(72), "vikend"),
        new("Do zaustavljanja", Timeout.InfiniteTimeSpan, "zaustavljate ga ručno"),
    ];

    public int TimelineCapacity => _history.Capacity;

    /// <summary>
    /// Whether monitoring outlives this window. Stated plainly rather than implied,
    /// because the difference matters enormously to someone starting a two-day test.
    /// </summary>
    public bool SurvivesClosing => _host.Kind == HostKind.Service;

    public string HostDescription => _host.Kind == HostKind.Service
        ? "Nadzor radi kao Windows servis. Možete zatvoriti prozor - test se nastavlja, i nastavlja se i posle restarta računara."
        : "Nadzor radi u ovom prozoru. Ako ga zatvorite, test se zaustavlja. Za dvodnevni test instalirajte servis.";

    public SessionVerdict Verdict => SessionVerdict.Evaluate(
        Snapshot.MonitoredTime,
        Snapshot.UpstreamIncidentCount,
        Snapshot.LocalDowntime);

    public string StateLabel => Snapshot.CurrentState.Label();

    public string StateExplanation => Snapshot.CurrentState.Explanation();

    public bool IsOnline => !Snapshot.CurrentState.IsOutage();

    public string LatencyText => Snapshot.CurrentLatency is { } latency
        ? $"{latency.TotalMilliseconds:F0} ms"
        : "-";

    public string ElapsedText => SerbianText.Duration(Snapshot.Elapsed);

    public string AvailabilityText =>
        SerbianText.Percent(Snapshot.AvailabilityPercent);

    public string UpstreamAvailabilityText =>
        SerbianText.Percent(Snapshot.UpstreamAvailabilityPercent);

    public string DowntimeText => SerbianText.Duration(Snapshot.UpstreamDowntime);

    public string LocalDowntimeText => SerbianText.Duration(Snapshot.LocalDowntime);

    /// <summary>
    /// The share of external destinations that went quiet on the last sample - not packet
    /// loss, and no longer labelled as it. One probe per destination cannot measure loss.
    /// </summary>
    public string UnreachableTargetsText => Snapshot.UnreachableTargetShare is { } share
        ? SerbianText.Percent(share, decimals: 1)
        : "nije mereno";

    /// <summary>
    /// Warns when a speed claim is being built on a wireless link, up front rather than
    /// after two days. Outage evidence over Wi-Fi is perfectly valid; a speed measurement
    /// is not, and finding that out on Sunday evening would be the worst possible moment.
    /// </summary>
    public bool ShowWirelessWarning => Snapshot.Medium == LinkMedium.Wireless;

    public string StatusPill => IsRunning ? "NADZOR U TOKU" : "NADZOR NIJE POKRENUT";

    /// <summary>How far through the planned duration, for the bar under the status card.</summary>
    public double ProgressPercent => (Snapshot.Progress ?? 0d) * 100d;

    /// <summary>False for a run with no planned end, where a bar would show nothing true.</summary>
    public bool HasProgress => Snapshot.Progress is not null;

    public string MediumText => Snapshot.Medium switch
    {
        LinkMedium.Ethernet => "žičana (Ethernet)",
        LinkMedium.Wireless => "bežična (Wi-Fi)",
        _ => "nepoznato",
    };

    /// <summary>
    /// The figures, in the order a reader of the complaint would want them.
    /// <para>
    /// The explanations are deliberately short. Written out in full they were longer than
    /// the row they had to fit in and the ends were simply clipped off, which is worse than
    /// leaving them out: a sentence cut mid-word looks like the application is broken.
    /// The full wording lives in the report, where there is a page to hold it.
    /// </para>
    /// </summary>
    public IReadOnlyList<Metric> Metrics =>
    [
        new("Dostupnost", AvailabilityText, "od nadziranog vremena"),
        new("Bez lokalnih kvarova", UpstreamAvailabilityText, "bez vaše opreme"),
        new("Prekida iza rutera",
            Snapshot.UpstreamIncidentCount.ToString(SerbianText.Culture),
            $"ukupno {Snapshot.IncidentCount.ToString(SerbianText.Culture)}"),
        new("Nedostupnost iza rutera", DowntimeText, $"lokalno {LocalDowntimeText}"),
        new("Mete bez odgovora", UnreachableTargetsText, "poslednji uzorak"),
        new("Nenadzirano", SerbianText.Duration(Snapshot.GapTime), "spavanje ili restart"),
    ];

    /// <summary>
    /// When the test that is about to be started would finish.
    /// <para>
    /// "48 sati" is an interval; what somebody actually needs to know before committing a
    /// machine to it is the day and hour they get their evidence back.
    /// </para>
    /// </summary>
    public string EndsAtText => SelectedDuration.Duration == Timeout.InfiniteTimeSpan
        ? "Test nema rok. Traje dok ga ne zaustavite."
        : $"Test se završava {SerbianText.DateTime(DateTimeOffset.Now + SelectedDuration.Duration)}.";

    /// <summary>
    /// The five families of check, as the last cycle left them.
    /// <para>
    /// The snapshot has carried these counts all along and the window never showed them, so
    /// the state on screen was a conclusion with its reasoning hidden. "Ruter 1/1, ping 0/3"
    /// is the difference between believing the verdict and being able to check it.
    /// </para>
    /// </summary>
    public IReadOnlyList<ProbeState> Probes =>
    [
        Probe("Ruter", Snapshot.Gateway),
        Probe("Ping", Snapshot.ExternalIcmp),
        Probe("TCP", Snapshot.ExternalTcp),
        Probe("DNS", Snapshot.Dns),
        Probe("HTTP", Snapshot.Http),
    ];

    private static ProbeState Probe(string name, ProbeTally tally)
    {
        // Silent is its own colour, never green: a check that was not attempted has not
        // passed, and painting it as though it had is the whole mistake this tool exists
        // to avoid making about connections.
        var brush = tally.IsSilent ? Palette.Neutral
            : tally.AllSucceeded ? Palette.Ok
            : tally.AllFailed ? Palette.Outage
            : Palette.Degraded;

        var detail = tally.IsSilent
            ? "nije provereno"
            : $"{tally.Succeeded.ToString(SerbianText.Culture)}/{tally.Attempted.ToString(SerbianText.Culture)}";

        return new ProbeState(name, detail, brush);
    }

    /// <summary>Remaining time without the leading word, for a cell that has its own label.</summary>
    public string RemainingValue => Snapshot.Remaining is { } remaining
        ? SerbianText.Duration(remaining)
        : "bez roka";

    /// <summary>
    /// What is being measured, on one line in the header band.
    /// <para>
    /// These are five short facts. Given a card of their own they occupied a row of the
    /// page and left most of it empty; they belong with the chrome, where they answer
    /// "is this the right link" without spending a band of the dashboard on it.
    /// </para>
    /// </summary>
    public string FactsLine => IsRunning
        ? string.Join("   ·   ",
            new[]
            {
                Snapshot.InterfaceName ?? "nepoznat adapter",
                MediumText,
                Snapshot.GatewayAddress is { } gateway ? $"ruter {gateway}" : "ruter nepoznat",
                $"{Snapshot.SampleCount.ToString("N0", SerbianText.Culture)} uzoraka",
                Snapshot.SessionId ?? "-",
            })
        : string.Empty;

    [RelayCommand]
    private async Task StartAsync()
    {
        Fault = null;
        _history.Clear();
        _history.PlanFor(SelectedDuration.Duration == Timeout.InfiniteTimeSpan ? null : SelectedDuration.Duration);
        PublishHistory();

        var started = await _host.StartSessionAsync(SelectedDuration.Duration, null, CancellationToken.None);

        if (!started)
        {
            Fault = "Nadzor nije pokrenut.";
            return;
        }

        IsRunning = true;
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _host.StopSessionAsync(CancellationToken.None);
        IsRunning = false;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var target = Snapshot.Directory is { } directory && Directory.Exists(directory)
            ? directory
            : _outputRoot;

        if (Directory.Exists(target))
        {
            OpenWithShell(target);
        }
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (Snapshot.Directory is not { } directory)
        {
            return;
        }

        // Built on demand when it does not exist yet. The report is written when a session
        // closes, so for the whole of a two-day run this button used to open a file listing
        // - which is not what somebody who wants to see their evidence asked for.
        var outcome = Hosting.ReportBuilder.Prepare(directory);

        if (!outcome.Ready)
        {
            // Says why rather than doing something else quietly. The folder still opens,
            // because the raw evidence in it is intact either way.
            Fault = outcome.Refusal;
            OpenFolder();
            return;
        }

        Fault = null;
        OpenWithShell(outcome.Path!);
    }

    /// <summary>
    /// Writes the complaint and the deadlines beside the evidence, then opens them.
    /// <para>
    /// This is where most complaints quietly stop. Someone who has just spent two days
    /// collecting evidence should not then have to work out how to phrase the thing, which
    /// figures matter, or by when - so the document arrives written.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void PrepareComplaint()
    {
        if (Snapshot.Directory is not { } directory || !Directory.Exists(directory))
        {
            Fault = "Nema završene sesije iz koje bi se napravio prigovor.";
            return;
        }

        var outcome = Hosting.ComplaintBuilder.Prepare(directory, operatorName: null, _outputRoot);

        if (!outcome.Written)
        {
            // Refused rather than written, and the reason goes on screen. A complaint that
            // overstates what was measured is worse for its author than no complaint.
            Fault = outcome.Refusal;
            return;
        }

        Fault = null;
        RaiseCase();
        OpenWithShell(outcome.LetterPath!);
    }

    /// <summary>
    /// Writes the submission to the regulator, or says what still stands in the way.
    /// <para>
    /// The refusal is the useful half: a submission filed before the operator's deadline
    /// passes is sent back unread, and the person has spent effort and part of their window.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void PrepareRegulator()
    {
        if (Snapshot.Directory is not { } directory || !Directory.Exists(directory))
        {
            Fault = "Nema sesije iz koje bi se napravila prijava.";
            return;
        }

        var outcome = Hosting.ComplaintBuilder.PrepareRegulator(directory, _outputRoot);

        if (!outcome.Written)
        {
            Fault = outcome.Refusal;
            return;
        }

        Fault = null;
        RaiseCase();
        OpenWithShell(outcome.SubmissionPath!);
    }

    /// <summary>
    /// Records that the complaint went out today. Dates land in the journal almost always on
    /// the day they happen, which is what makes a button the right shape for this.
    /// </summary>
    [RelayCommand]
    private void MarkSubmittedToday()
    {
        if (Record(caseState => caseState with { SubmittedDate = DateOnly.FromDateTime(DateTime.Now) }))
        {
            Fault = null;
        }
    }

    /// <summary>Records that the operator answered today and refused the complaint.</summary>
    [RelayCommand]
    private void MarkRefusedToday()
    {
        if (Record(caseState => caseState with
        {
            OperatorRespondedDate = DateOnly.FromDateTime(DateTime.Now),
            OperatorUpheld = false,
        }))
        {
            Fault = null;
        }
    }

    /// <summary>Records that the operator answered today and upheld the complaint.</summary>
    [RelayCommand]
    private void MarkUpheldToday()
    {
        if (Record(caseState => caseState with
        {
            OperatorRespondedDate = DateOnly.FromDateTime(DateTime.Now),
            OperatorUpheld = true,
        }))
        {
            Fault = null;
        }
    }

    /// <summary>Where the case stands, for the label in the window.</summary>
    [ObservableProperty]
    private string? _caseText;

    /// <summary>Writes an event into the journal, or explains why there is nothing to write into.</summary>
    private bool Record(Func<ComplaintCase, ComplaintCase> change)
    {
        var journal = CaseJournalStore.Load(_outputRoot);

        if (journal is null)
        {
            Fault = "Još nema predmeta. Prvo pripremite prigovor operateru.";
            return false;
        }

        CaseJournalStore.Save(
            _outputRoot,
            journal with { Case = change(journal.Case) },
            DateOnly.FromDateTime(DateTime.Now));

        RaiseCase();
        return true;
    }

    /// <summary>Refreshes the case line from the journal, for the corner it sits in.</summary>
    private void RaiseCase()
    {
        var journal = CaseJournalStore.Load(_outputRoot);
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (journal is null)
        {
            CaseText = null;
            return;
        }

        // The rules the case was recorded under, not today's. A case from last year keeps the
        // position it had, which is the whole reason they are written into the file.
        var legal = journal.Legal ?? journal.Case.Resolve(today);
        var stage = journal.Case.StageOn(today, legal);
        var uncertain = legal.State == LegalContextState.Resolved ? string.Empty : " · rokovi nisu potvrđeni";

        CaseText = $"{stage.Label()} · operater {journal.Case.OperatorName}{uncertain}";
    }

    // ---- Scheduled speed measurement -----------------------------------------

    public IReadOnlyList<string> SpeedScheduleUnits { get; } = ["minuta", "sati"];

    /// <summary>The number typed into the scheduling field; the unit is chosen beside it.</summary>
    [ObservableProperty]
    private string _speedScheduleAmount = string.Empty;

    [ObservableProperty]
    private string _selectedSpeedScheduleUnit = "minuta";

    /// <summary>
    /// The contracted rate, written the way the contract states it: "100" or "100/20".
    /// <para>
    /// Without it the window could record a figure but never judge it, so every measurement
    /// taken here was filed as unusable and the verdict had to be fetched from the console -
    /// which is a strange thing to ask of somebody who has the window open in front of them.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _contractedRateText = string.Empty;

    /// <summary>What the scheduled measurement is doing right now, or its result.</summary>
    [ObservableProperty]
    private string? _speedStatus;

    /// <summary>True while a measurement is scheduled or running, so the button steps aside.</summary>
    [ObservableProperty]
    private bool _speedBusy;

    public bool CanScheduleSpeed => !SpeedBusy;

    private CancellationTokenSource? _speedCancellation;

    /// <summary>
    /// Records the connection speed at a moment the user picks in advance.
    /// <para>
    /// A line that dips every evening at nine cannot be measured at lunch, and nobody will
    /// sit by the console until then. The number is how far away the measurement is; when
    /// the time comes it runs by itself, waits for the link to be quiet, and files the
    /// figure beside the session like any other measurement.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task ScheduleSpeedAsync()
    {
        if (SpeedBusy)
        {
            return;
        }

        if (!TryParseScheduleAmount(out var delay, out var refusal))
        {
            Fault = refusal;
            return;
        }

        if (!ContractedRate.TryParse(ContractedRateText, out var contractedDownload, out var contractedUpload))
        {
            Fault = "Ugovorena brzina nije prepoznata. Upišite broj, ili par: 100/20.";
            return;
        }

        // With the service installed the instruction is handed to it rather than kept here.
        // A measurement scheduled inside this window dies when the window closes, which
        // makes the one case scheduling exists for - three in the morning, with nobody
        // awake - the one case it could not serve.
        if (SurvivesClosing && TryHandToService(delay, contractedDownload, contractedUpload))
        {
            return;
        }

        Fault = null;
        SpeedBusy = true;
        _speedCancellation = new CancellationTokenSource();

        try
        {
            await RunScheduledSpeedAsync(delay, _speedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SpeedStatus = "Zakazano merenje je otkazano.";
        }
#pragma warning disable CA1031 // A measurement that fails has to say so, not take the window with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SpeedStatus = $"Merenje nije uspelo: {ex.Message}";
        }
        finally
        {
            SpeedBusy = false;
        }
    }

    /// <summary>
    /// Writes the instruction where the service will find it, so the measurement happens
    /// whether or not this window is still open when its moment comes.
    /// </summary>
    /// <returns>False when it could not be written, so the caller falls back to measuring here.</returns>
    private bool TryHandToService(TimeSpan delay, double? contractedDownload, double? contractedUpload)
    {
        var dueAt = DateTimeOffset.UtcNow + delay;

        try
        {
            new SpeedRequest(dueAt, contractedDownload, contractedUpload).Write(_outputRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The window measures it itself instead, and says so rather than pretending the
            // instruction landed somewhere it did not.
            SpeedStatus =
                $"Zakazivanje preko servisa nije uspelo ({ex.Message}). Merenje se izvršava iz " +
                "ovog prozora, pa ga do tada ne zatvarajte.";

            return false;
        }

        Fault = null;

        SpeedStatus =
            $"Merenje je zakazano za {SerbianText.DateTime(dueAt.ToLocalTime())}. Izvršiće ga servis, " +
            "pa prozor može biti zatvoren. Rezultat ulazi u izveštaj sesije. " +
            (contractedDownload is null
                ? "Bez ugovorene brzine merenje se zapisuje, ali se ne ocenjuje."
                : $"Ugovoreno: {ContractedRate.Describe(contractedDownload, contractedUpload)}.");

        return true;
    }

    /// <summary>
    /// A number of minutes or hours, entered bare because that is how a person says it:
    /// "izmeri za 90 minuta", "izmeri za 2 sata".
    /// </summary>
    private bool TryParseScheduleAmount(out TimeSpan delay, out string refusal)
    {
        delay = TimeSpan.Zero;
        refusal = string.Empty;

        if (!int.TryParse(SpeedScheduleAmount.Trim(), out var amount) || amount <= 0)
        {
            refusal = "Unesite za koliko vremena da se merenje pokrene - samo broj, npr. 90.";
            return false;
        }

        var ceiling = SelectedSpeedScheduleUnit == "sati" ? 24 * 7 : 24 * 60;

        if (amount > ceiling)
        {
            refusal = "Zakazivanje je ograničeno na nedelju dana unapred.";
            return false;
        }

        delay = SelectedSpeedScheduleUnit == "sati"
            ? TimeSpan.FromHours(amount)
            : TimeSpan.FromMinutes(amount);
        return true;
    }

    private async Task RunScheduledSpeedAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var startsAt = DateTimeOffset.Now + delay;
        var started = DateTimeOffset.Now;

        while (true)
        {
            var left = delay - (DateTimeOffset.Now - started);

            if (left <= TimeSpan.Zero)
            {
                break;
            }

            SpeedStatus = $"Merenje kreće u {startsAt:HH:mm} (još {SerbianText.Duration(left)}).";
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        await MeasureAndRecordAsync(cancellationToken);
    }

    /// <summary>
    /// One measurement, taken and filed exactly the way the console command takes it: the
    /// link is inspected at the moment of measuring, the connection must be quiet first,
    /// and the figure lands beside the session with its conditions.
    /// </summary>
    private async Task MeasureAndRecordAsync(CancellationToken cancellationToken)
    {
        if (!ContractedRate.TryParse(ContractedRateText, out var contractedDownload, out var contractedUpload))
        {
            SpeedStatus = "Ugovorena brzina nije prepoznata. Upišite broj, ili par: 100/20.";
            return;
        }

        await using var linkInspection = WindowsLinkInspection.Create(null);
        var link = linkInspection.Inspector.Inspect();

        using var httpClient = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        // The round-trip probes travel on a client of their own, so they do not queue behind
        // the transfer they are measuring alongside.
        using var latencyClient = new HttpClient(new SocketsHttpHandler { UseProxy = false });

        var activity = new LinkActivityMonitor();
        var measurement = new ThroughputMeasurement(
            httpClient,
            activity,
            options: null,
            clock: null,
            new LoadedLatencySampler(latencyClient));

        // The same patience the console command has by default: ten minutes of standing in
        // the queue before giving up on a link that never went quiet.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        var nextNote = DateTimeOffset.MinValue;

        while (!measurement.ReadyToMeasure)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow > deadline)
            {
                SpeedStatus = "Merenje nije izvršeno: veza nije bila mirna ni posle 10 minuta čekanja.";
                return;
            }

            var reading = activity.Sample(link.InterfaceId);
            measurement.Observe(reading);

            if (reading is not null && DateTimeOffset.UtcNow >= nextNote)
            {
                nextNote = DateTimeOffset.UtcNow.AddSeconds(10);

                SpeedStatus = reading.IsIdle
                    ? $"Veza je mirna ({measurement.IdleFor?.TotalSeconds:0} s od 10 s) - čeka se početak…"
                    : $"Veza je zauzeta ({reading.BytesPerSecond / 1000d:0.#} KB/s) - čeka se da utihne…";
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        SpeedStatus = "Merenje u toku…";

        // From here on this application is the load, so a session running beside it - in this
        // process or in the service - excludes the period instead of recording our own
        // transfer as delay and loss on the customer's line.
        using var marker = MeasurementMarker.Hold(_outputRoot);

        var result = await measurement.MeasureAsync(connectionHealthy: true, cancellationToken);

        if (!result.Ran)
        {
            SpeedStatus = "Merenje nije izvršeno: server za merenje nije odgovorio.";
            return;
        }

        // The conditions the window can vouch for: it waited for the link to go quiet, and
        // it measured the adapter it is watching. Whether that is enough for a complaint is
        // decided by the same rules the console uses, from the same place.
        var conditions = new SpeedMeasurementConditions(
            link.Medium,
            link.LinkSpeedBitsPerSecond,
            contractedDownload,
            result.DownloadMbps)
        {
            ContractedUploadMbps = contractedUpload,
            MeasuredUploadMbps = result.UploadMbps,

            // Checked rather than assumed: on a machine with a docking station or a VPN the
            // transfer can leave through an adapter other than the one being described. An
            // unresolved check stays unresolved - it used to fall back to "one path", so the
            // window recorded a verified path on machines where nothing had been verified.
            RouteState = MeasurementPath.Resolve(link.InterfaceId).State,
        };

        var latest = SessionPaths.FindLatest(_outputRoot);
        var note = SpeedMeasurementNote.From(
            DateTimeOffset.UtcNow,
            link.Medium,
            link.LinkSpeedBitsPerSecond / 1_000_000d,
            conditions,
            result);

        note.Write(latest?.Directory ?? _outputRoot);

        SpeedStatus = Summarise(result, note, latest is not null);
    }

    /// <summary>
    /// The one line the window shows after a measurement: what was measured, how it is
    /// judged, and where it went.
    /// </summary>
    private static string Summarise(ThroughputResult result, SpeedMeasurementNote note, bool besideSession)
    {
        var text = $"Preuzimanje {result.DownloadMbps.ToString("0.##", SerbianText.Culture)} Mbit/s";

        if (result.UploadMbps is { } upload)
        {
            text += $", slanje {upload.ToString("0.##", SerbianText.Culture)} Mbit/s";
        }

        if (note.LatencyIncreaseMs is { } increase)
        {
            text += $", kašnjenje pod opterećenjem +{increase.ToString("0.#", SerbianText.Culture)} ms " +
                    $"({note.LoadedLatencyLabel})";
        }

        text += ". ";

        var assessment = note.Assess();

        text += assessment.BandLabel is { } band
            ? $"Ocena: {band}. "
            : "Bez ugovorene brzine nema ocene - upišite je u polje pored dugmeta. ";

        if (assessment.UploadBandLabel is { } uploadBand)
        {
            text += $"Ocena slanja: {uploadBand}. ";
        }

        text += besideSession
            ? "Sačuvano uz poslednju sesiju."
            : "Sačuvano u folderu sa sesijama; ući će u izveštaj prve sledeće sesije.";

        return text;
    }

    partial void OnSpeedBusyChanged(bool value) => OnPropertyChanged(nameof(CanScheduleSpeed));

    public async Task ConnectAsync()
    {
        ShowScheduledMeasurement();
        await _host.ConnectAsync(CancellationToken.None);
    }

    /// <summary>
    /// Says so when the service is already holding a measurement for later.
    /// <para>
    /// The instruction outlives this window, which is the point of handing it to the
    /// service - so a window opened afterwards has to be able to say that a measurement is
    /// still coming. Read from the same file the scheduling writes, rather than asked over
    /// the pipe, because it must also be answerable while the service is stopped.
    /// </para>
    /// </summary>
    private void ShowScheduledMeasurement()
    {
        if (!SurvivesClosing || SpeedRequest.Read(_outputRoot) is not { } pending)
        {
            return;
        }

        SpeedStatus = pending.DueAtUtc > DateTimeOffset.UtcNow
            ? $"Merenje brzine je zakazano za {SerbianText.DateTime(pending.DueAtUtc.ToLocalTime())}. " +
              "Izvršiće ga servis, pa prozor može biti zatvoren."
            : "Zakazano merenje brzine čeka da ga servis izvrši. Ako servis ne radi, pokrenite ga.";
    }

    /// <summary>
    /// Title and body of something worth a tray toast: the connection failing, and it
    /// coming back. Nobody watches this window through a two-day test - the toast is how
    /// the person whose evidence this is learns the fault was caught.
    /// </summary>
    public event Action<string, string>? TrayNotification;

    private bool _wasOutage;
    private DateTimeOffset? _outageSince;

    private void AnnounceTransition(MonitorSnapshot snapshot)
    {
        var isOutage = snapshot.CurrentState.IsOutage();

        if (isOutage && !_wasOutage)
        {
            _outageSince = DateTimeOffset.Now;
            TrayNotification?.Invoke(
                "Prekid veze",
                $"{snapshot.CurrentState.Label()}. Evidencija se i dalje snima.");
        }
        else if (!isOutage && _wasOutage && _outageSince is { } since)
        {
            TrayNotification?.Invoke(
                "Veza se vratila",
                $"Nakon {SerbianText.Duration(DateTimeOffset.Now - since)} prekida.");
            _outageSince = null;
        }

        _wasOutage = isOutage;
    }

    /// <summary>
    /// Measures now rather than at a scheduled time: same rules - wait for quiet, three
    /// parallel streams, filed beside the session with its conditions.
    /// </summary>
    [RelayCommand]
    private async Task MeasureNowAsync()
    {
        if (SpeedBusy)
        {
            return;
        }

        SpeedBusy = true;
        _speedCancellation = new CancellationTokenSource();

        try
        {
            await MeasureAndRecordAsync(_speedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SpeedStatus = "Merenje je otkazano.";
        }
#pragma warning disable CA1031 // A measurement that fails has to say so, not take the window with it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SpeedStatus = $"Merenje nije uspelo: {ex.Message}";
        }
        finally
        {
            SpeedBusy = false;
        }
    }

    /// <summary>
    /// Runs the update where bound state may be touched.
    /// <para>
    /// Through the dispatcher when there is one, and inline when there is not. The
    /// null-conditional this replaces silently dropped the update instead - which is
    /// harmless in the one case it was written for, an update arriving while the application
    /// is shutting down, and unhelpful in the other: it made every path that reaches the
    /// window through the host impossible to exercise without standing up WPF.
    /// </para>
    /// </summary>
    private static void OnUi(Action update)
    {
        if (Application.Current is { } application)
        {
            application.Dispatcher.Invoke(update);
        }
        else
        {
            update();
        }
    }

    private void OnUpdated(MonitorSnapshot snapshot)
    {
        AnnounceTransition(snapshot);
        // Arrives on a background thread; everything below touches bound state.
        OnUi(() =>
        {
            var newSession = _attachedSession != snapshot.SessionId;

            Snapshot = snapshot;
            IsRunning = true;

            if (newSession)
            {
                _attachedSession = snapshot.SessionId;
                _history.PlanFor(snapshot.PlannedDuration);
                LoadExistingHistory(snapshot);
                PublishHistory();
            }

            if (_history.Add(snapshot))
            {
                PublishHistory();
            }

            RaiseDerived();
        });
    }

    /// <summary>
    /// Pulls in what the session recorded before this window was looking.
    /// <para>
    /// Read from the session's index rather than replayed from the chain: the index exists
    /// exactly for this, already bucketed to the same resolution the charts use. Failure
    /// here is not worth surfacing - the charts simply start from now, which is what they
    /// would have done anyway.
    /// </para>
    /// </summary>
    private void LoadExistingHistory(MonitorSnapshot snapshot)
    {
        if (snapshot.Directory is not { } directory)
        {
            return;
        }

        try
        {
            var paths = new SessionPaths(directory);
            if (!File.Exists(paths.Database))
            {
                return;
            }

            using var reader = SessionReader.Open(paths.Database);
            var session = reader.Load();

            if (session is null || session.Latency.Count == 0)
            {
                return;
            }

            _history.Load(
                [.. session.Latency.Select(b => (b.MinRtt, b.AverageRtt, b.MaxRtt, b.Outage, b.Degraded))],
                snapshot.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            // The service holds the database open; a transient read failure just means the
            // charts begin at the present moment.
        }
    }

    private void OnHostFaultChanged(string? fault) => OnUi(() => Fault = fault);

    private void PublishHistory()
    {
        // New arrays each time: the chart controls redraw on property change, and mutating
        // the same list in place would leave them showing stale pixels.
        Timeline = [.. _history.Slices];
        Latency = [.. _history.Latency];
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Verdict));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateExplanation));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(UpstreamAvailabilityText));
        OnPropertyChanged(nameof(DowntimeText));
        OnPropertyChanged(nameof(LocalDowntimeText));
        OnPropertyChanged(nameof(UnreachableTargetsText));
        OnPropertyChanged(nameof(RemainingValue));
        OnPropertyChanged(nameof(ShowWirelessWarning));
        OnPropertyChanged(nameof(StatusPill));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(MediumText));
        OnPropertyChanged(nameof(Metrics));
        OnPropertyChanged(nameof(Probes));
        OnPropertyChanged(nameof(FactsLine));
    }

    /// <summary>The finishing time follows whichever duration is selected.</summary>
    partial void OnSelectedDurationChanged(DurationChoice value) => OnPropertyChanged(nameof(EndsAtText));

    /// <summary>
    /// The pill in the header reads from <see cref="IsRunning"/>, which the generated setter
    /// raises on its own - but nothing else recomputes the derived text beside it.
    /// </summary>
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusPill));

    private static void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing registered to open it, or the user cancelled. Not worth a dialog.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A scheduled measurement belongs to this window; closing it cancels the plan rather
        // than leaving a measurement to fire into a process that is going away.
        _speedCancellation?.Cancel();
        _speedCancellation?.Dispose();

        _host.Updated -= OnUpdated;
        _host.FaultChanged -= OnHostFaultChanged;
        await _host.DisposeAsync();
    }
}
