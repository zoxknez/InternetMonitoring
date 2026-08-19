using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IEM.App.Presentation;
using IEM.Core.Presentation;
using IEM.Core.Reports;

namespace IEM.App.ViewModels;

public sealed record ClaimDisplayItem(
    string ClaimId,
    string StatementKey,
    string EpistemicBadge,
    string ValueText,
    string SupportState,
    string? QualityAssessmentRef);

/// <summary>
/// Evidence dashboard ViewModel displaying distinct Integrity, Trust, and Claim-Level Quality breakdowns.
/// Invariants:
/// 162. UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
/// 163. OVERALL_UI_QUALITY_NEVER_HIDES_CLAIM_SPECIFIC_QUALITY
/// </summary>
public sealed class EvidenceViewModel : INotifyPropertyChanged
{
    private string _overallQualityBand = "Unknown";
    private string _integrityState = "Nepoznato";
    private string _trustState = "Nepoznato";
    private string _packageVerificationSummary = "Paket nije finalizovan.";

    public string OverallQualityBand
    {
        get => _overallQualityBand;
        private set => SetProperty(ref _overallQualityBand, value);
    }

    public string IntegrityState
    {
        get => _integrityState;
        private set => SetProperty(ref _integrityState, value);
    }

    public string TrustState
    {
        get => _trustState;
        private set => SetProperty(ref _trustState, value);
    }

    public string PackageVerificationSummary
    {
        get => _packageVerificationSummary;
        private set => SetProperty(ref _packageVerificationSummary, value);
    }

    public ObservableCollection<ClaimDisplayItem> Claims { get; } = new();

    public void ApplySnapshot(PresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Analysis == null)
        {
            PackageVerificationSummary = "Nema aktivnog dokaznog paketa.";
            return;
        }

        var a = snapshot.Analysis;
        IntegrityState = SemanticVisualTokens.GetIntegrityBadgeText(a.PackageIntegrityState);
        TrustState = SemanticVisualTokens.GetTrustBadgeText(a.PackageTrustState);

        var q = a.QualityAssessments.Count > 0 ? a.QualityAssessments[0].OverallEvidenceBand.ToString() : "Unknown";
        OverallQualityBand = snapshot.RuntimeState == SessionRuntimeState.Monitoring
            ? $"{q} (Provisional)"
            : q;

        PackageVerificationSummary = a.PackageIntegrityState == "Verified" && a.PackageTrustState == "Established"
            ? "Paket je verifikovan, digitalno potpisan i poseduje važeći RFC 3161 žig."
            : (a.PackageIntegrityState == "Verified"
                ? "Integritet paketa je verifikovan, ali RFC 3161 žig nije uspostavljen (Trust = NotEstablished)."
                : "Integritet dokaznog paketa nije validan.");

        Claims.Clear();
        foreach (var c in a.Claims)
        {
            Claims.Add(new ClaimDisplayItem(
                ClaimId: c.ClaimId,
                StatementKey: c.StatementKey,
                EpistemicBadge: SemanticVisualTokens.GetEpistemicBadgeText(c.EpistemicClass),
                ValueText: c.StructuredValue?.Format() ?? "Nije utvrđeno (Unknown)",
                SupportState: c.SupportState.ToString(),
                QualityAssessmentRef: c.QualityAssessmentRef));
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
