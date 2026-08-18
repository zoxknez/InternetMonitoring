using System.Text.Json;
using System.Text.Json.Serialization;
using IEM.Core.Model;
using IEM.Core.Speed;
using IEM.Legal;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// What 2.7.2 wrote, read by whatever build is running now.
/// <para>
/// These are not tests of the current version's behaviour - the rest of the suite does that.
/// They are the guarantee that the next version can still read the last one: the chain still
/// verifies, the index still rebuilds to the same figures, the findings still say what they
/// said. Every claim about evidence surviving a version change rests on somebody checking, and
/// this is where that happens.
/// </para>
/// <para>
/// The artefacts are a real recorded session - a real chain, real reports - written by the
/// recorder rather than assembled in code, from synthetic probe cycles so that nothing private
/// is in them. See <see cref="BaselineSnapshotWriter"/>.
/// </para>
/// </summary>
public sealed class CharacterizationTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// BASELINE_FIXTURES_ARE_RELEASE_ARTIFACTS. Present on disk, present in the repository and
    /// present in the tag are three different things, and 2.7.1 shipped having confused them.
    /// </summary>
    [Fact]
    public void Every_artefact_the_snapshot_promises_is_actually_there()
    {
        var manifest = BaselineSnapshot.Manifest();

        Assert.NotEmpty(manifest);

        foreach (var name in manifest)
        {
            // Throws with the path and the reason when one is missing. Never a skip.
            Assert.True(new FileInfo(BaselineSnapshot.File(name)).Length > 0, $"{name} je prazan");
        }

        // The four that carry meaning rather than presentation. Losing any of them would leave
        // the suite green while the snapshot stopped proving anything.
        foreach (var required in new[]
                 {
                     "SirovaEvidencija.jsonl", "MerenjeBrzine.json", "DnevnikSlucaja.json", "sesija.db",
                 })
        {
            Assert.Contains(required, manifest);
        }
    }

    /// <summary>
    /// The chain from the previous version still verifies. This is the single most important
    /// promise the program makes, and the one a format change would break silently.
    /// </summary>
    [Fact]
    public void The_recorded_chain_still_verifies()
    {
        var verification = ChainVerifier.Verify(BaselineSnapshot.File("SirovaEvidencija.jsonl"));

        Assert.True(verification.Valid, verification.Reason);
        Assert.True(verification.EntriesChecked > 90);

        // The head hash as it was when the snapshot was frozen. If this moves, either the file
        // changed or the way it is hashed did - and both are things to find out about
        // deliberately, rather than through a report that no longer matches a package sent
        // months ago.
        Assert.Equal(BaselineSnapshot.HeadHash, verification.HeadHash);
    }

    /// <summary>
    /// The index is derived and disposable - the claim being that it can always be rebuilt
    /// from the chain. Rebuilt here, and checked against what the frozen session says.
    /// </summary>
    [Fact]
    public void The_index_rebuilds_from_the_chain_to_the_same_figures()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"iem-char-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        try
        {
            const string raw = "SirovaEvidencija.jsonl";
            File.Copy(BaselineSnapshot.File(raw), Path.Combine(scratch, raw));

            var rebuild = EvidenceIndexRebuilder.RebuildForExport(new SessionPaths(scratch));

            using var reader = SessionReader.Open(rebuild.DatabasePath);
            var session = reader.Load();

            Assert.NotNull(session);
            Assert.Equal("S20260818060000", session.SessionId);
            Assert.Equal("TEST-PC", session.Machine);
            Assert.Single(session.Incidents);
            Assert.Equal(FaultAttribution.Upstream, session.Incidents[0].Attribution);

            // The versions the session was recorded under, not this build's.
            Assert.Equal("2.3.0", session.ClassifierVersion);
            Assert.Equal(3, session.SchemaVersion);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// A measurement written by 2.7.2 carries its schema version, so its conclusions are its
    /// own and are read at face value - unlike one from 2.6, which is not.
    /// </summary>
    [Fact]
    public void The_recorded_measurement_is_read_at_face_value()
    {
        var note = JsonSerializer.Deserialize<SpeedMeasurementNote>(
            File.ReadAllText(BaselineSnapshot.File(SpeedMeasurementNote.FileName)), Json)!;

        Assert.False(note.IsLegacyFinding);
        Assert.Equal(MeasurementRouteState.AllResolvedRoutesMatch, note.RouteState);
        Assert.Equal(SpeedAssessmentState.MeetsConditions, note.Assess().State);
        Assert.Null(note.Assess().RecordedAssessment);
    }

    /// <summary>
    /// The case carries the rules it was decided under, and reading it again does not change
    /// them - neither by re-resolving nor by picking up whatever registry ships today.
    /// </summary>
    [Fact]
    public void The_recorded_case_keeps_the_rules_it_was_decided_under()
    {
        var journal = CaseJournalStore.Load(BaselineSnapshot.Session)!;

        Assert.Equal(CaseJournal.CurrentSchemaVersion, journal.SchemaVersion);

        var legal = journal.Legal!;

        Assert.Equal("RS-ZEK-2025", legal.Ruleset.Id);
        Assert.Equal(30, legal.For(ComplaintStep.ComplaintDue)!.Value);
        Assert.Equal(8, legal.For(ComplaintStep.OperatorResponseDue)!.Value);

        // Extending it with nothing new leaves every period exactly as it was, however much
        // later it is opened.
        var again = LegalRegistry.Extend(journal.Case.Facts, legal, new DateOnly(2026, 12, 1));

        Assert.Equal(legal.Ruleset, again.Ruleset);

        foreach (var step in legal.AppliedRules.Select(rule => rule.Step))
        {
            Assert.Equal(legal.For(step)!.Due, again.For(step)!.Due);
            Assert.Equal(legal.For(step)!.RuleId, again.For(step)!.RuleId);
        }
    }

    /// <summary>
    /// The wording the report carried. Not every sentence - the ones this project spent two
    /// releases getting right, where a regression would be a claim rather than a typo.
    /// </summary>
    [Theory]
    [InlineData("Prekidi izolovani iza vaše opreme")]
    [InlineData("Lanac otisaka je unutrašnje dosledan")]
    [InlineData("provera doslednosti, a ne dokaz porekla")]
    [InlineData("Putanja merenja")]
    [InlineData("tabela ruta je saglasna sa izabranim adapterom")]
    public void The_report_says_what_it_said(string phrase)
    {
        Assert.Contains(phrase, Report(), StringComparison.Ordinal);
    }

    /// <summary>And the wording it must not carry, from before those two releases.</summary>
    [Theory]
    [InlineData("Veza je bila stabilna")]
    [InlineData("kod operatera")]
    [InlineData("Gubitak paketa")]
    [InlineData("dokazano je da paket nije menjan")]
    [InlineData("uobičajeno dostupna")]
    public void The_report_does_not_say_what_it_stopped_saying(string phrase)
    {
        Assert.DoesNotContain(phrase, Report(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The checksums cover the package, including the findings written beside it.</summary>
    [Fact]
    public void The_checksums_cover_the_findings_beside_the_session()
    {
        var sums = File.ReadAllText(BaselineSnapshot.File("SHA256SUMS.txt"));

        foreach (var name in new[] { "Izvestaj.pdf", "MerenjeBrzine.json", "SirovaEvidencija.jsonl" })
        {
            Assert.Contains(name, sums, StringComparison.Ordinal);
        }
    }

    private static string Report() => File.ReadAllText(BaselineSnapshot.File("Izvestaj.html"));
}
