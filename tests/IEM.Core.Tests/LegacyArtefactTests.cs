using System.Runtime.CompilerServices;
using System.Text.Json;
using IEM.Core.Speed;
using IEM.Legal;

namespace IEM.Core.Tests;

/// <summary>
/// LEGACY_DERIVED_CONCLUSION_IS_NEVER_TRUSTED_AS_RAW_EVIDENCE.
/// <para>
/// Files written by 2.6, read by this build. Both defects these guard against survived the
/// whole of 2.7.0 because every other test builds its data in code: the rules were corrected,
/// the tests passed, and a measurement already on disk carried the old conclusion straight
/// through the new presentation. What a previous build measured is evidence; what it
/// concluded is not.
/// </para>
/// </summary>
public sealed class LegacyArtefactTests
{
    /// <summary>
    /// The finding as 2.6 wrote it: no route state, no schema version, and a stored verdict
    /// reached when an unchecked path counted as a verified one.
    /// </summary>
    [Fact]
    public void A_speed_finding_from_2_6_does_not_keep_its_verdict()
    {
        var note = ReadNote();

        Assert.True(note.IsLegacyFinding);
        Assert.Equal(MeasurementRouteState.Unknown, note.RouteState);
        Assert.True(note.ValidForComplaint);

        var assessment = note.Assess();

        // The stored "valid" is history, not a finding.
        Assert.Equal(SpeedAssessmentState.Undetermined, assessment.State);
        Assert.True(assessment.RecordedAssessment);
        Assert.Contains("ranija ocena valjanosti ne preuzima", assessment.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stored band label is text, so the regulatory phrasing P0-7 removed came back with
    /// it. The numbers are the evidence; the band is derived from them again.
    /// </summary>
    [Fact]
    public void A_speed_finding_from_2_6_has_its_band_derived_again()
    {
        var note = ReadNote();

        Assert.Contains("uobičajene", note.BandLabel!, StringComparison.OrdinalIgnoreCase);

        var assessment = note.Assess();

        // 61.4 of 100 contracted is below the 70 % floor, and says so in today's terms.
        Assert.Equal("ISPOD 70 % UGOVORENE", assessment.BandLabel);
        Assert.DoesNotContain("uobičajen", assessment.BandLabel!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The measured numbers are never touched. They are what the file is for.</summary>
    [Fact]
    public void The_recorded_figures_are_taken_exactly_as_written()
    {
        var note = ReadNote();

        Assert.Equal(61.4, note.DownloadMbps);
        Assert.Equal(100, note.ContractedMbps);
        Assert.Equal(76_000_000, note.BytesTransferred);
        Assert.Equal(TimeSpan.FromSeconds(10), note.Duration);
    }

    /// <summary>
    /// A finding this build writes carries the version, so its conclusions are its own and
    /// are read back as such.
    /// </summary>
    [Fact]
    public void A_finding_written_now_is_read_at_face_value()
    {
        var note = ReadNote() with
        {
            FindingSchemaVersion = SpeedMeasurementNote.CurrentFindingSchemaVersion,
            RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
        };

        Assert.False(note.IsLegacyFinding);
        Assert.Equal(SpeedAssessmentState.MeetsConditions, note.Assess().State);
        Assert.Null(note.Assess().RecordedAssessment);
    }

    /// <summary>
    /// The case file as 2.6 wrote it. Read as the old regime it would hand somebody a
    /// fifteen-day window that stopped existing at the start of 2025.
    /// </summary>
    [Fact]
    public void A_case_file_from_2_6_is_reconstructed_rather_than_assumed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"iem-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            File.Copy(
                Path.Combine(Fixtures, "DnevnikSlucaja.json"),
                Path.Combine(directory, CaseJournalStore.FileName));

            var journal = CaseJournalStore.Load(directory)!;

            Assert.Null(journal.Legal);
            Assert.Equal(FactOrigin.ImportedLegacy, journal.Case.EventOrigin);

            var legal = journal.Case.Resolve(new DateOnly(2026, 8, 18));

            Assert.Equal(LegalContextState.InferredFromRecordedDates, legal.State);
            Assert.Equal(8, legal.For(ComplaintStep.OperatorResponseDue)!.Value);
            Assert.Equal(60, legal.For(ComplaintStep.RegulatorDisputeDue)!.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SpeedMeasurementNote ReadNote() =>
        JsonSerializer.Deserialize<SpeedMeasurementNote>(
            File.ReadAllText(Path.Combine(Fixtures, SpeedMeasurementNote.FileName)),
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;

    /// <summary>The artefacts as they were written, kept in the repository rather than rebuilt.</summary>
    private static string Fixtures => Path.Combine(RepositoryRoot(), "baseline", "legacy-2.6");

    private static string RepositoryRoot([CallerFilePath] string here = "")
    {
        var fromSource = Path.GetDirectoryName(here);

        if (fromSource is not null && Directory.Exists(fromSource))
        {
            return Path.GetFullPath(Path.Combine(fromSource, "..", ".."));
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.GetFiles("*.slnx").Length > 0)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repozitorijum nije pronađen.");
    }
}
