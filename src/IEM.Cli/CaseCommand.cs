using System.Text;
using IEM.Core.Presentation;
using IEM.Legal;
using IEM.Storage;

namespace IEM.Cli;

/// <summary>
/// The case after the letter: what was filed, what was answered, what remains.
/// <para>
/// The session is the evidence, but the case outlives it. Without somewhere to write down
/// that the complaint went out on the 12th and the operator answered on the 20th, the
/// deadlines are recomputed from the session every time as if nothing had ever been sent -
/// and the fifteen days to reach the regulator pass unnoticed. This command is that
/// somewhere: it records the events, keeps the journal beside the sessions, and prints the
/// timetable that follows from what is known.
/// </para>
/// </summary>
public static class CaseCommand
{
    private const string SubmissionFile = "Prijava-RATEL.txt";

    /// <summary>
    /// Runs whichever case task the settings describe: recording an event, showing the
    /// state, or preparing the regulator submission.
    /// </summary>
    public static int Run(CliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var root = settings.OutputRoot;
        var journal = CaseJournalStore.Load(root);

        if (settings.ComplaintSubmitted is not null ||
            settings.OperatorResponded is not null ||
            settings.OperatorUpheld is not null ||
            settings.RegulatorFiled is not null ||
            settings.InvoiceDue is not null ||
            settings.BillingComplaint ||
            settings.NonConsumer)
        {
            return Record(root, journal, settings) ? 0 : 1;
        }

        if (settings.RegulatorDirectory is { } directory)
        {
            return Regulator(root, journal, directory) ? 0 : 1;
        }

        return Show(journal) ? 0 : 1;
    }

    /// <summary>Writes an event into the journal, then shows where the case stands.</summary>
    private static bool Record(string root, CaseJournal? journal, CliSettings settings)
    {
        Console.WriteLine();
        Console.WriteLine("  DNEVNIK PREDMETA");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine();

        if (journal is null)
        {
            WriteError("  Još nema predmeta. Prvo pripremite prigovor komandom -g, pa beležite događaje.");
            Console.WriteLine();
            return false;
        }

        var updated = journal.Case with
        {
            SubmittedDate = settings.ComplaintSubmitted ?? journal.Case.SubmittedDate,
            OperatorRespondedDate = settings.OperatorResponded ?? journal.Case.OperatorRespondedDate,
            OperatorUpheld = settings.OperatorUpheld ?? journal.Case.OperatorUpheld,
            RegulatorFiledDate = settings.RegulatorFiled ?? journal.Case.RegulatorFiledDate,
            InvoiceDueDate = settings.InvoiceDue ?? journal.Case.InvoiceDueDate,
            ComplaintKind = settings.BillingComplaint ? ComplaintKind.BillingAmount : journal.Case.ComplaintKind,
            CustomerType = settings.NonConsumer ? CustomerType.NonConsumer : journal.Case.CustomerType,
        };

        var today = DateOnly.FromDateTime(DateTime.Now);

        // Refusing a date that is in the future, or one that arrives before the thing it
        // answers: a timetable built on impossible dates is worse than an empty one, and the
        // mistake would sit quietly in every deadline computed after it.
        if (settings.ComplaintSubmitted is { } submitted && submitted > today)
        {
            WriteError("  Prigovor ne može biti podnet u budućnosti.");
            Console.WriteLine();
            return false;
        }

        if (updated.OperatorRespondedDate is { } responded &&
            (responded > today || (updated.SubmittedDate is { } filed && responded < filed)))
        {
            WriteError("  Odgovor operatera ne može biti u budućnosti, niti pre podnošenja prigovora.");
            Console.WriteLine();
            return false;
        }

        var saved = new CaseJournal { Case = updated, Notes = journal.Notes };
        CaseJournalStore.Save(root, saved, today);

        Console.WriteLine("  Zabeleženo. Stanje predmeta:");
        Console.WriteLine();

        // Printed from what was just written, so the screen and the file cannot disagree.
        Print(CaseJournalStore.Load(root) ?? saved, today);
        Console.WriteLine();

        return true;
    }

    /// <summary>Prepares the regulator submission, or explains what stands in the way.</summary>
    private static bool Regulator(string root, CaseJournal? journal, string directory)
    {
        Console.WriteLine();
        Console.WriteLine("  PRIJAVA RATEL-U");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine($"  Folder:  {directory}");
        Console.WriteLine();

        if (journal is null)
        {
            WriteError("  Još nema predmeta. Prvo pripremite prigovor komandom -g.");
            Console.WriteLine();
            return false;
        }

        var paths = new SessionPaths(directory);

        if (!File.Exists(paths.RawLog))
        {
            WriteError($"  U folderu sesije nema sirove evidencije (nedostaje {Path.GetFileName(paths.RawLog)}).");
            Console.WriteLine();
            return false;
        }

        var rebuild = EvidenceIndexRebuilder.RebuildForExport(paths);
        SessionSnapshot? session;

        try
        {
            using var reader = SessionReader.Open(rebuild.DatabasePath);
            session = reader.Load();
        }
        finally
        {
            TryDelete(rebuild.DatabasePath);
        }

        if (session is null)
        {
            WriteError("  Sirova evidencija ne sadrži nijednu sesiju.");
            Console.WriteLine();
            return false;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        // Judged against the rules the case carries, not against today's registry.
        var legal = journal.Legal ?? journal.Case.Resolve(today);
        var obstacle = RegulatorSubmission.CanSubmit(journal.Case, today, legal);

        // Refusing before writing is the point of the whole module: a submission filed too
        // early is sent back, and the person has spent effort and part of their window.
        if (obstacle != RegulatorSubmission.Obstacle.None)
        {
            WriteError($"  Prijava se još ne može pripremiti: {RegulatorSubmission.Explain(obstacle)}");
            Console.WriteLine();
            return false;
        }

        var submission = RegulatorSubmission.Build(journal.Case, session, today, out var _, legal);

        if (submission is null)
        {
            WriteError("  Prijava nije mogla biti sastavljena.");
            Console.WriteLine();
            return false;
        }

        var path = Path.Combine(directory, SubmissionFile);
        File.WriteAllText(path, submission, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Console.WriteLine($"  Prijava:  {path}");
        Console.WriteLine();
        Console.WriteLine("  Popunite podatke označene sa ____, potpišite i pošaljite:");
        Console.WriteLine("  poštom, lično, e-poštom ili preko web forme na portal.ratel.rs.");
        Console.WriteLine();

        // Sastavljena prijava nije i podneta prijava. Dok se datum ne zabeleži, predmet s
        // pravom prikazuje rok kao otvoren - a upravo taj datum odlučuje po kom se režimu
        // vodi postupak pred Regulatorom.
        ConsoleText.WriteWrapped(
            "Kada prijavu stvarno pošaljete, zabeležite datum: iem --predmet --prijavljeno " +
            $"{today:dd.MM.yyyy.} Bez toga predmet i dalje računa rok za obraćanje Regulatoru " +
            "kao otvoren.");

        Console.WriteLine();

        return true;
    }

    private static bool Show(CaseJournal? journal)
    {
        Console.WriteLine();
        Console.WriteLine("  PREDMET");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine();

        if (journal is null)
        {
            Console.WriteLine("  Još nema predmeta. Pripremite prigovor komandom -g, pa se ovde");
            Console.WriteLine("  beleži šta je posle toga.");
            Console.WriteLine();
            return true;
        }

        Print(journal, DateOnly.FromDateTime(DateTime.Now));
        Console.WriteLine();

        return true;
    }

    private static void Print(CaseJournal journal, DateOnly today)
    {
        var complaint = journal.Case;

        // The rules the case was recorded under, where it carries them. Recomputing would
        // hand an old case today's periods, which is the one thing a record of a dispute must
        // never do.
        var legal = journal.Legal ?? complaint.Resolve(today);
        var stage = complaint.StageOn(today, legal);
        var milestones = complaint.Milestones(legal);
        var regulatorFiled = complaint.RegulatorFiledDate;

        Console.WriteLine($"  Operater:        {complaint.OperatorName}");
        Console.WriteLine($"  Događaj:         {complaint.EventDate:dd.MM.yyyy.}  " +
                          $"({new AnchoredDate(complaint.EventDate, complaint.EventOrigin, complaint.EventEvidenceRef).Describe()})");
        Console.WriteLine($"  Vrsta prigovora: {Describe(complaint.ComplaintKind)}");
        Console.WriteLine($"  Stanje:          {stage.Label()}");
        Console.WriteLine($"  Pravila:         {legal.Ruleset}");
        Console.WriteLine();

        foreach (var milestone in milestones)
        {
            Console.WriteLine($"  {Date(milestone.Date)}  {milestone.Step.Label(),-45}{Note(milestone)}");

            if (milestone.Rule is { Citations.Count: > 0 } rule)
            {
                Console.WriteLine($"  {' ',12}  ↳ {rule.Value} dana, {string.Join("; ", rule.Citations)}");
            }
            else if (milestone.Rule?.Impediment is { } impediment)
            {
                Console.WriteLine($"  {' ',12}  ↳ {impediment}");
            }

            // A period that was superseded, is still provisional, or whose facts now
            // disagree says so here. Recording it in the journal and not showing it left the
            // screen claiming a settled date the file knew was contested.
            if (milestone.Explain(milestone.Step.Label()) is { } note)
            {
                foreach (var line in ConsoleText.Wrap(note, 62))
                {
                    Console.WriteLine($"  {' ',12}  {line}");
                }
            }
        }

        if (regulatorFiled is { } filed)
        {
            Console.WriteLine();
            Console.WriteLine($"  RATEL-u prijavljeno: {filed:dd.MM.yyyy.}");
        }

        if (legal.State != LegalContextState.Resolved)
        {
            Console.WriteLine();
            ConsoleText.WriteWrapped(legal.State.Explain());
        }

        Console.WriteLine();
        Console.WriteLine("  Šta sada:");
        Console.WriteLine();

        foreach (var line in ConsoleText.Wrap(stage.WhatNow()))
        {
            Console.WriteLine($"    {line}");
        }

        var missed = ComplaintDeadlines.Missed(milestones, today, regulatorFiled);

        if (missed.Count > 0)
        {
            Console.WriteLine();

            foreach (var milestone in missed)
            {
                Console.WriteLine($"  ⚠ Propušteno: {milestone.Step.Label()} ({milestone.Date:dd.MM.yyyy.})");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {CaseText.Disclaimer}");

        string Note(ComplaintMilestone milestone) => !milestone.IsDeadline
            ? string.Empty
            : milestone.DaysFrom(today) switch
            {
                null => "  ← rok nije utvrđen",
                < 0 => "  ← rok je prošao",
                0 => "  ← danas je poslednji dan",
                1 => "  ← ostao još 1 dan",
                { } days => $"  ← ostalo još {days} {SessionVerdict.Plural(days, "dan", "dana", "dana")}",
            };
    }

    /// <summary>A deadline nobody could work out prints as such, never as a date.</summary>
    private static string Date(DateOnly? date) =>
        date is { } value ? value.ToString("dd.MM.yyyy.", SerbianText.Culture) : "nije utvrđeno";

    private static string Describe(ComplaintKind kind) =>
        kind == ComplaintKind.BillingAmount ? "iznos računa" : "kvalitet usluge";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary index in the system temp folder harms nothing.
        }
    }

    private static void WriteError(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
