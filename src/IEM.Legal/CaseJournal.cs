using System.Text.Json;
using System.Text.Json.Serialization;

namespace IEM.Legal;

/// <summary>
/// The complaint as a living thing: what happened after the letter was sent.
/// <para>
/// The session holds the evidence, but the case outlives it. The complaint is filed, the
/// operator answers or does not, the regulator is contacted or is not - and none of that is
/// in the monitoring data, because none of it happened to the connection. Without a place to
/// write it down, the deadlines are recomputed from the session every time as if the letter
/// had never been sent, which is exactly how the window to escalate quietly closes.
/// </para>
/// </summary>
public sealed record CaseJournal
{
    /// <summary>
    /// The layout of this file.
    /// <para>
    /// Absent - and therefore 0 - in every file written before 2.7, which is how those are
    /// recognised: they carry dates but no record of which rules were applied to them.
    /// </para>
    /// </summary>
    public int SchemaVersion { get; init; }

    /// <summary>The version this build writes.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The case as of the last recorded event.</summary>
    public required ComplaintCase Case { get; init; }

    /// <summary>
    /// When the matter was taken to the regulator, as older files recorded it.
    /// </summary>
    /// <remarks>
    /// Now lives on the case, where it belongs: it is the day the proceeding before the
    /// Regulator began, which is what the transitional rule turns on. Still read from here
    /// when an older file has it, and no longer written.
    /// </remarks>
    public DateOnly? RegulatorFiledDate { get; init; }

    /// <summary>
    /// The rules that were applied to this case, exactly as they were applied.
    /// <para>
    /// Frozen rather than recomputed. An identifier alone would let a later correction to the
    /// registry rewrite what an old case meant - and a case file is a record of a dispute,
    /// which is the one kind of document that must never quietly change.
    /// </para>
    /// </summary>
    public ResolvedLegalContext? Legal { get; init; }

    /// <summary>Anything worth remembering that fits nowhere else, newest last.</summary>
    public string? Notes { get; init; }
}

/// <summary>
/// Reads and writes the journal of the one case this output folder is about.
/// <para>
/// One file beside the sessions rather than a database: the case is a handful of dates, the
/// owner is one person, and a JSON file a person can open in Notepad is auditable in a way
/// an embedded database is not.
/// </para>
/// </summary>
public static class CaseJournalStore
{
    public const string FileName = "DnevnikSlucaja.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Names rather than ordinals. The whole reason this is a file a person can open in
        // Notepad is that they can check it - and "3" for the anchor a deadline was counted
        // from tells them nothing, while renumbering the enum would silently change what an
        // old file meant.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathOf(string outputRoot) => Path.Combine(outputRoot, FileName);

    /// <summary>
    /// Saves the journal, freezing the legal position into it.
    /// <para>
    /// Resolved here rather than by the caller, so no path can write a case file without the
    /// rules that were applied to it. The alternative - remembering to do it at four call
    /// sites - is how the periods came to be unrecorded in the first place.
    /// </para>
    /// </summary>
    /// <param name="asOf">The day the position is being recorded on.</param>
    public static void Save(string outputRoot, CaseJournal journal, DateOnly asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(journal);

        var stamped = journal with
        {
            SchemaVersion = CaseJournal.CurrentSchemaVersion,
            Legal = journal.Case.Resolve(asOf),
            RegulatorFiledDate = null,
        };

        Directory.CreateDirectory(outputRoot);
        File.WriteAllText(PathOf(outputRoot), JsonSerializer.Serialize(stamped, Json));
    }

    /// <summary>
    /// Loads the journal, or null when none exists.
    /// <para>
    /// An unparseable file is reported as absent rather than thrown, with the same reasoning
    /// as everywhere else in this tool: a broken journal must not prevent the work around it,
    /// and the user can read the file themselves to see what went wrong.
    /// </para>
    /// </summary>
    public static CaseJournal? Load(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        var path = PathOf(outputRoot);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var journal = JsonSerializer.Deserialize<CaseJournal>(File.ReadAllText(path), Json);

            return journal is null ? null : Migrate(journal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Brings a file written by an earlier build up to the current shape, without inventing
    /// anything it did not contain.
    /// <para>
    /// A pre-2.7 file has no record of which rules were applied to it. It is emphatically
    /// <em>not</em> read as the old regime: that would hand somebody a fifteen-day deadline
    /// which stopped existing at the start of 2025, on no evidence at all. Its dates are
    /// marked as imported, which makes every period derived from them come back as
    /// reconstructed rather than established - and the output says so.
    /// </para>
    /// </summary>
    private static CaseJournal Migrate(CaseJournal journal)
    {
        if (journal.SchemaVersion >= CaseJournal.CurrentSchemaVersion)
        {
            return journal;
        }

        return journal with
        {
            Case = journal.Case with
            {
                EventOrigin = journal.Case.EventOrigin == FactOrigin.Unknown
                    ? FactOrigin.ImportedLegacy
                    : journal.Case.EventOrigin,

                // Older files kept this beside the case rather than on it.
                RegulatorFiledDate = journal.Case.RegulatorFiledDate ?? journal.RegulatorFiledDate,
            },
        };
    }
}
