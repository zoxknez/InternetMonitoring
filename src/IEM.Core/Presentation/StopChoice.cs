namespace IEM.Core.Presentation;

/// <summary>What someone who pressed "Zaustavi nadzor" actually wants to happen.</summary>
public enum StopChoice
{
    /// <summary>Keep monitoring. The button was pressed by mistake, or the answer changed their mind.</summary>
    Cancel,

    /// <summary>Stop, and leave the report for later.</summary>
    StopOnly,

    /// <summary>Stop and build the report, tables and archive at once.</summary>
    StopAndReport,
}

/// <summary>
/// The question asked when monitoring is about to be stopped.
/// <para>
/// Pressing the button used to stop the session with no word about what had just happened to
/// two days of evidence. People pressed it and did not know whether they had a report, whether
/// the recording was lost, or what to do next - so they asked, which is the clearest possible
/// sign that the program was not telling them.
/// </para>
/// <para>
/// The text lives here rather than in the window so it can be read by a test, and so the same
/// sentences are available to any other surface that ever has to ask this.
/// </para>
/// </summary>
public static class StopPrompt
{
    public const string Title = "Zaustavljanje nadzora";

    public const string Question = "Da li da uz zaustavljanje napravim i izveštaj?";

    /// <summary>
    /// The first thing to say, because it is the thing people are afraid of. Stopping loses
    /// nothing: the raw evidence has been on disk since the first sample.
    /// </summary>
    public const string Reassurance =
        "Evidencija je već snimljena i ne gubi se ni u jednom slučaju. Izveštaj se pravi iz nje, " +
        "sada ili kad god kasnije.";

    public const string ReportLabel = "Zaustavi i napravi izveštaj";

    public const string ReportDetail =
        "Pravi Izvestaj.pdf, Izvestaj.html, tabele i arhivu spremnu za slanje operateru, pa " +
        "otvara izveštaj.";

    public const string StopOnlyLabel = "Samo zaustavi";

    public const string StopOnlyDetail =
        "Nadzor se zaustavlja, evidencija ostaje. Izveštaj kasnije pravi dugme „Izveštaj“.";

    public const string CancelLabel = "Nastavi nadzor";

    public const string CancelDetail = "Ništa se ne menja, sesija se nastavlja.";

    /// <summary>
    /// What was recorded so far, said in one line above the question, so the decision is made
    /// knowing how much is at stake rather than in the dark.
    /// </summary>
    public static string Summarise(TimeSpan monitored, int incidents) =>
        incidents == 0
            ? $"Nadzor traje {SerbianText.Duration(monitored)}. Nijedan prekid nije zabeležen."
            : $"Nadzor traje {SerbianText.Duration(monitored)}. " +
              $"Zabeleženih prekida: {incidents}.";
}
