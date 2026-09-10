using System.Collections.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Reports;
using IEM.Presentation;
using IEM.Presentation.Contracts;
using IEM.Presentation.Hosting;
using IEM.Presentation.Models;
using IEM.Presentation.Semantics;
using IEM.Presentation.States;

namespace IEM.App.Linux.ViewModels;

internal sealed partial class LinuxShellViewModel : ObservableObject, IAsyncDisposable
{
    private const int HistoryCapacity = 600;
    private readonly IMonitorHost _host;
    private readonly DefaultPresentationProjector _projector = new();
    private readonly List<TimelineSlice> _timeline = [];
    private readonly List<LatencyPoint> _latency = [];
    private MonitorSnapshot _live = MonitorSnapshot.Empty;
    private long _revision;
    private bool _initialized;

    public LinuxShellViewModel(IMonitorHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        serviceStatus = host.Kind == HostKind.Service
            ? ServiceConnectionStatus.Connecting
            : ServiceConnectionStatus.ServiceUnavailable;
        selectedDuration = ShellPresentationState.DefaultDurations[3];
        state = ShellPresentationState.Initial;
        _host.Updated += OnHostUpdated;
        _host.FaultChanged += OnHostFaultChanged;
        Reproject();
    }

    public ImmutableArray<DurationChoice> Durations => ShellPresentationState.DefaultDurations;

    public string ModeLabel => _host.Kind == HostKind.Service
        ? "Sistemski servis · nadzor ostaje aktivan kada zatvorite prozor"
        : "Prenosivi režim · nadzor traje samo dok je aplikacija otvorena";

    public string ServiceStatusLabel => _host.Kind != HostKind.Service
        ? "Prenosivi režim · sistemski servis se ne koristi"
        : ServiceStatus switch
        {
            ServiceConnectionStatus.Connected => "Servis je dostupan",
            ServiceConnectionStatus.Connecting => "Povezivanje sa servisom…",
            _ => "Sistemski servis nije dostupan",
        };

    public bool CanStart => !IsRunning;
    public bool CanStop => IsRunning;
    public bool HasFault => !string.IsNullOrWhiteSpace(Fault);

    [ObservableProperty]
    private ShellPresentationState state;

    [ObservableProperty]
    private DurationChoice selectedDuration;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFault))]
    private string? fault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceStatusLabel))]
    private ServiceConnectionStatus serviceStatus;

    [ObservableProperty]
    private string operatorName = string.Empty;

    [ObservableProperty]
    private string contractNumber = string.Empty;

    [ObservableProperty]
    private string userContact = string.Empty;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await _host.ConnectAsync(CancellationToken.None);
        IsRunning = _host.IsRunning;
        Reproject();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Fault = null;
        _timeline.Clear();
        _latency.Clear();
        _live = MonitorSnapshot.Empty;

        var accepted = await _host.StartSessionAsync(
            SelectedDuration.Duration,
            interfaceName: null,
            CancellationToken.None);
        IsRunning = accepted || _host.IsRunning;
        if (!accepted && string.IsNullOrWhiteSpace(Fault))
        {
            Fault = "Nadzor nije pokrenut.";
        }

        Reproject();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await _host.StopSessionAsync(CancellationToken.None);
        IsRunning = _host.IsRunning;
        Reproject();
    }

    [RelayCommand]
    private void OpenEvidenceFolder()
    {
        var directory = _live.Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Fault = "Direktorijum dokazne sesije još nije dostupan.";
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { directory },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Fault = $"Direktorijum nije otvoren: {ex.Message}";
        }
    }

    partial void OnSelectedDurationChanged(DurationChoice value) => Reproject();
    partial void OnOperatorNameChanged(string value) => Reproject();
    partial void OnContractNumberChanged(string value) => Reproject();
    partial void OnUserContactChanged(string value) => Reproject();

    private void OnHostUpdated(MonitorSnapshot snapshot) =>
        Dispatcher.UIThread.Post(() => ApplyHostSnapshot(snapshot));

    internal void ApplyHostSnapshot(MonitorSnapshot snapshot)
    {
        _live = snapshot;
        if (_host.Kind == HostKind.Service)
        {
            ServiceStatus = ServiceConnectionStatus.Connected;
        }
        IsRunning = _host.IsRunning;

        if (snapshot.SampleCount > 0)
        {
            AppendBounded(_timeline, new TimelineSlice(snapshot.CurrentState.SeverityOf()));
            AppendBounded(_latency, new LatencyPoint(
                snapshot.CurrentLatency?.TotalMilliseconds,
                snapshot.CurrentLatency?.TotalMilliseconds,
                snapshot.CurrentLatency?.TotalMilliseconds));
        }

        Reproject();
    }

    private void OnHostFaultChanged(string? value) =>
        Dispatcher.UIThread.Post(() => ApplyHostFault(value));

    internal void ApplyHostFault(string? value)
    {
        Fault = value;
        if (_host.Kind == HostKind.Service)
        {
            ServiceStatus = string.IsNullOrWhiteSpace(value)
                ? ServiceConnectionStatus.Connected
                : ServiceConnectionStatus.ServiceUnavailable;
        }
        Reproject();
    }

    private void Reproject()
    {
        var now = DateTimeOffset.UtcNow;
        var runtimeState = IsRunning ? SessionRuntimeState.Monitoring : SessionRuntimeState.Idle;
        var snapshot = new PresentationSnapshot(
            SnapshotId: $"linux-ui-{++_revision}",
            SessionId: _live.SessionId ?? string.Empty,
            AnalysisRevision: _revision,
            CapturedAtUtc: now,
            RuntimeState: runtimeState,
            ServiceStatus: ServiceStatus,
            Analysis: null,
            CanonicalReport: null,
            SourceRefs: Array.Empty<string>());
        var hostFacts = _host.Kind == HostKind.Service
            ? new HostPresentationFacts(
                true,
                "Radi u pozadini",
                "Prozor možete zatvoriti; nadzor nastavlja sistemski servis.",
                "Nastavlja posle restarta",
                "Otvorena sesija se nastavlja kada se servis ponovo pokrene.",
                "Nadzor vodi Linux sistemski servis i nije vezan za životni vek prozora.")
            : new HostPresentationFacts(
                false,
                "Radi dok je prozor otvoren",
                "Zatvaranje aplikacije završava prenosivu sesiju.",
                "Ne nastavlja posle restarta",
                "Za dug nadzor instalirajte sistemski servis.",
                "Prenosivi nadzor radi u ovom procesu i završava se sa aplikacijom.");
        var interaction = new ShellInteractionState(
            IsRunning,
            Fault,
            ShellTab.Monitor,
            SelectedDuration,
            Durations,
            HistoryCapacity,
            SpeedScheduleAmount: string.Empty,
            SelectedSpeedScheduleUnit: "minuta",
            SpeedScheduleUnits: ShellPresentationState.DefaultSpeedUnits,
            ContractedRateText: string.Empty,
            SpeedStatus: null,
            SpeedBusy: false);
        var workspace = new IEM.Presentation.Contracts.CaseWorkspaceState(
            OperatorName,
            ContractNumber,
            UserContact,
            ReportCompositionProfile.Complaint,
            ImmutableArray<UserStatementPresentationItem>.Empty,
            CaseJournalText: null);

        State = _projector.ProjectShell(new ShellProjectionInput(
            snapshot,
            interaction,
            hostFacts,
            new HistoryPresentationState(_timeline.ToImmutableArray(), _latency.ToImmutableArray()),
            UpdatePresentationState.Hidden,
            workspace,
            SpeedExecutionFacts.None,
            _live));
    }

    private static void AppendBounded<T>(List<T> list, T value)
    {
        if (list.Count == HistoryCapacity)
        {
            list.RemoveAt(0);
        }

        list.Add(value);
    }

    public async ValueTask DisposeAsync()
    {
        _host.Updated -= OnHostUpdated;
        _host.FaultChanged -= OnHostFaultChanged;
        await _host.DisposeAsync();
    }
}
