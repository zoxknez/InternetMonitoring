using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IEM.App.ViewModels;

/// <summary>
/// Speed measurement dashboard ViewModel with strict protection against false 0 Mbps claims on refusal.
/// Invariant 167: NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT.
/// </summary>
public sealed class SpeedViewModel : INotifyPropertyChanged
{
    private string _measurementIntent = "ObserveSystemPath";
    private string _requestedInterface = "Wi-Fi";
    private string _observedPath = "Wi-Fi";
    private string _pathAgreement = "Match";
    private string _tunnelIndication = "NotDetected";
    private string _downloadThroughputText = "— (Nije pokrenuto)";
    private string _uploadThroughputText = "— (Nije pokrenuto)";
    private string _measurementStatusText = "Spremno za pokretanje testa brzine.";
    private bool _ran;

    public string MeasurementIntent
    {
        get => _measurementIntent;
        set => SetProperty(ref _measurementIntent, value);
    }

    public string RequestedInterface
    {
        get => _requestedInterface;
        set => SetProperty(ref _requestedInterface, value);
    }

    public string ObservedPath
    {
        get => _observedPath;
        set => SetProperty(ref _observedPath, value);
    }

    public string PathAgreement
    {
        get => _pathAgreement;
        set => SetProperty(ref _pathAgreement, value);
    }

    public string TunnelIndication
    {
        get => _tunnelIndication;
        set => SetProperty(ref _tunnelIndication, value);
    }

    public string DownloadThroughputText
    {
        get => _downloadThroughputText;
        private set => SetProperty(ref _downloadThroughputText, value);
    }

    public string UploadThroughputText
    {
        get => _uploadThroughputText;
        private set => SetProperty(ref _uploadThroughputText, value);
    }

    public string MeasurementStatusText
    {
        get => _measurementStatusText;
        private set => SetProperty(ref _measurementStatusText, value);
    }

    public bool Ran
    {
        get => _ran;
        private set => SetProperty(ref _ran, value);
    }

    public void UpdateMeasurementResult(bool ran, string? refusalReason, double? downloadMbps, double? uploadMbps)
    {
        Ran = ran;

        if (!ran)
        {
            // Invariant 167: NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
            DownloadThroughputText = "— (Merenje odbijeno)";
            UploadThroughputText = "— (Merenje odbijeno)";
            MeasurementStatusText = !string.IsNullOrEmpty(refusalReason)
                ? $"Merenje nije izvršeno: {refusalReason}."
                : "Merenje nije izvršeno: odbijeno od strane protokola.";
            return;
        }

        DownloadThroughputText = downloadMbps.HasValue ? $"{downloadMbps.Value:N1} Mbps" : "Nepoznato";
        UploadThroughputText = uploadMbps.HasValue ? $"{uploadMbps.Value:N1} Mbps" : "Nepoznato";
        MeasurementStatusText = "Merenje brzine uspešno izvršeno.";
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
