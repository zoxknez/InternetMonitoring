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
    /// <summary>The case as of the last recorded event.</summary>
    public required ComplaintCase Case { get; init; }

    /// <summary>
    /// When the matter was taken to the regulator, once it was.
    /// <para>
    /// The one date the case itself cannot carry: the regulator deadline is the last
    /// milestone, and recording that it was met is what stops the report from nagging about
    /// a window that was used in time.
    /// </para>
    /// </summary>
    public DateOnly? RegulatorFiledDate { get; init; }

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
    };

    public static string PathOf(string outputRoot) => Path.Combine(outputRoot, FileName);

    /// <summary>Saves the journal, creating the folder when it does not exist yet.</summary>
    public static void Save(string outputRoot, CaseJournal journal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(journal);

        Directory.CreateDirectory(outputRoot);
        File.WriteAllText(PathOf(outputRoot), JsonSerializer.Serialize(journal, Json));
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
            return JsonSerializer.Deserialize<CaseJournal>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
