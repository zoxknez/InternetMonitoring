using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IEM.Core.Presentation;
using IEM.Core.Reports;
using IEM.Core.Reports.Renderers;

namespace IEM.App.ViewModels;

/// <summary>
/// Case workspace ViewModel allowing user annotations and read-only complaint/regulatory previews.
/// Invariants:
/// 164. USER_CASE_METADATA_AND_ANNOTATIONS_NEVER_MUTATE_SOURCE_EVIDENCE
/// 165. USER_AUTHORED_CASE_STATEMENT_IS_NEVER_PROMOTED_TO_EVIDENCE_CLAIM
/// 166. REPORT_PREVIEW_IS_A_READ_ONLY_PROJECTION_OF_THE_CANONICAL_REPORT_DOCUMENT_MODEL
/// </summary>
public sealed class CaseViewModel : INotifyPropertyChanged
{
    private string _operatorName = string.Empty;
    private string _contractNumber = string.Empty;
    private string _userContact = string.Empty;
    private string _selectedProfile = "Complaint";
    private string _previewText = string.Empty;
    private ReportDocumentModel? _canonicalReport;

    public string OperatorName
    {
        get => _operatorName;
        set
        {
            if (SetProperty(ref _operatorName, value))
            {
                UpdatePreview();
            }
        }
    }

    public string ContractNumber
    {
        get => _contractNumber;
        set
        {
            if (SetProperty(ref _contractNumber, value))
            {
                UpdatePreview();
            }
        }
    }

    public string UserContact
    {
        get => _userContact;
        set => SetProperty(ref _userContact, value);
    }

    public string SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                UpdatePreview();
            }
        }
    }

    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    public ObservableCollection<UserStatement> UserStatements { get; } = new();

    public void AddUserStatement(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Invariant 165: USER_AUTHORED_CASE_STATEMENT_IS_NEVER_PROMOTED_TO_EVIDENCE_CLAIM
        var stmt = new UserStatement(
            StatementId: $"user-note-{Guid.NewGuid():N}",
            Text: text.Trim(),
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Author: "User");

        UserStatements.Add(stmt);
        UpdatePreview();
    }

    public void ApplySnapshot(PresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _canonicalReport = snapshot.CanonicalReport;
        UpdatePreview();
    }

    public void UpdatePreview()
    {
        if (_canonicalReport == null)
        {
            PreviewText = "Dokument još uvek nije dostupan (čekanje na podatke sesije).";
            return;
        }

        if (SelectedProfile == "Complaint")
        {
            var composer = new ComplaintNarrativeComposer();
            PreviewText = composer.RenderToString(_canonicalReport);
        }
        else
        {
            var composer = new RatelRegulatoryComposer();
            PreviewText = composer.RenderToString(_canonicalReport);
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
