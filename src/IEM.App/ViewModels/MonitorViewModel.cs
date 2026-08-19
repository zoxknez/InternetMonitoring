using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IEM.App.Presentation;
using IEM.Core.Presentation;
using IEM.Core.Reports;

namespace IEM.App.ViewModels;

public sealed record MonitorTimelineItem(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    TimelineEntryCategory Category,
    string CategoryLabel,
    string Description,
    bool IsSuspend,
    bool IsOutage);

/// <summary>
/// Monitor dashboard ViewModel projecting live session observations and health states.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// 161. NON_OBSERVABLE_HOST_INTERVAL_IS_NEVER_VISUALIZED_AS_NETWORK_OUTAGE
/// </summary>
public sealed class MonitorViewModel : INotifyPropertyChanged
{
    private string _targetHealthSummary = "Nema aktivnih merenja (No data yet)";
    private string _probeHealthSummary = "Sonde u stanju pripravnosti";
    private string _qualityBandText = "Nepoznato";
    private string _totalDuration = "0s";
    private string _activeDuration = "0s";
    private string _suspendDuration = "0s";
    private int _interruptionsCount;

    public string TargetHealthSummary
    {
        get => _targetHealthSummary;
        private set => SetProperty(ref _targetHealthSummary, value);
    }

    public string ProbeHealthSummary
    {
        get => _probeHealthSummary;
        private set => SetProperty(ref _probeHealthSummary, value);
    }

    public string QualityBandText
    {
        get => _qualityBandText;
        private set => SetProperty(ref _qualityBandText, value);
    }

    public string TotalDuration
    {
        get => _totalDuration;
        private set => SetProperty(ref _totalDuration, value);
    }

    public string ActiveDuration
    {
        get => _activeDuration;
        private set => SetProperty(ref _activeDuration, value);
    }

    public string SuspendDuration
    {
        get => _suspendDuration;
        private set => SetProperty(ref _suspendDuration, value);
    }

    public int InterruptionsCount
    {
        get => _interruptionsCount;
        private set => SetProperty(ref _interruptionsCount, value);
    }

    public ObservableCollection<MonitorTimelineItem> TimelineItems { get; } = new();

    public void ApplySnapshot(PresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Analysis == null)
        {
            TargetHealthSummary = "Čekanje na prve rezultate merenja...";
            return;
        }

        var a = snapshot.Analysis;
        TargetHealthSummary = a.TargetHealthSummary;
        ProbeHealthSummary = a.ProbeHealthSummary;
        TotalDuration = ReportValue.FromDuration(a.TotalDuration).Format();
        ActiveDuration = ReportValue.FromDuration(a.ActiveMonitoringDuration).Format();
        SuspendDuration = ReportValue.FromDuration(a.HostSuspensionDuration).Format();
        InterruptionsCount = a.OutagesObservedCount;

        var quality = a.QualityAssessments.Count > 0 ? a.QualityAssessments[0].OverallEvidenceBand.ToString() : "Unknown";
        QualityBandText = snapshot.RuntimeState == SessionRuntimeState.Monitoring
            ? $"{quality} (Provisional)"
            : quality;

        TimelineItems.Clear();
        TimelineItems.Add(new MonitorTimelineItem(
            a.SessionStartUtc,
            a.SessionStartUtc.Add(a.ActiveMonitoringDuration),
            TimelineEntryCategory.ActiveMonitoring,
            SemanticVisualTokens.GetTimelineCategoryLabel(TimelineEntryCategory.ActiveMonitoring),
            "Aktivno osmatranje meta",
            IsSuspend: false,
            IsOutage: false));

        if (a.HostSuspensionDuration > TimeSpan.Zero)
        {
            TimelineItems.Add(new MonitorTimelineItem(
                a.SessionStartUtc.Add(a.ActiveMonitoringDuration),
                a.SessionEndUtc,
                TimelineEntryCategory.HostSuspended,
                SemanticVisualTokens.GetTimelineCategoryLabel(TimelineEntryCategory.HostSuspended),
                "Računar u sleep stanju (nije prekid mreže)",
                IsSuspend: true,
                IsOutage: false));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
