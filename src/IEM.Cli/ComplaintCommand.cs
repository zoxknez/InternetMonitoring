using System.Text;
using IEM.Core.Presentation;
using IEM.Legal;
using IEM.Storage;

namespace IEM.Cli;

/// <summary>
/// Turns a recorded session into the complaint the user actually has to send.
/// <para>
/// This is where most complaints quietly stop. Someone who has just spent two days
/// collecting evidence should not then have to work out how to phrase the thing, which
/// figures matter, what to attach and by when - so the document arrives written, with the
/// dates worked out and the attachments named.
/// </para>
/// </summary>
public static class ComplaintCommand
{
    private const string LetterFile = "Prigovor-operateru.txt";
    private const string TimelineFile = "Rokovi.txt";

    /// <param name="incidentNumber">
    /// Which recorded outage the complaint is about, where the session has more than one.
    /// Every deadline is counted from it, so the program proposes the first and says so
    /// rather than choosing silently.
    /// </param>
    public static bool Run(
        string directory,
        string? operatorName,
        string? outputRoot = null,
        int? incidentNumber = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var paths = new SessionPaths(directory);

        Console.WriteLine();
        Console.WriteLine("  PRIGOVOR OPERATERU");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine($"  Folder:  {directory}");
        Console.WriteLine();

        if (!File.Exists(paths.RawLog))
        {
            WriteError($"  U ovom folderu nema sirove evidencije (nedostaje {Path.GetFileName(paths.RawLog)}).");
            Console.WriteLine();
            return false;
        }

        // The complaint quotes figures from the session, so the session has to be sound
        // first. A letter built on a chain that does not verify would be worse than none.
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
        var prepared = ComplaintPreparation.From(session, operatorName, today, incidentNumber);

        // Refused when there is nothing to complain about, and that refusal is the point.
        // A letter demanding the cause of outages that were never recorded is the exact
        // overclaiming this tool exists to avoid - and the first thing an operator would
        // notice, taking the rest of the evidence down with it.
        if (!prepared.Prepared)
        {
            WriteError($"  {prepared.Refusal}");
            Console.WriteLine();
            Console.WriteLine("  Prigovor nije napravljen. Pustite nadzor da radi duže, ili ga pokrenite");
            Console.WriteLine("  ponovo kada se problem javi - evidencija bez prekida nije osnov za prigovor.");
            Console.WriteLine();
            return false;
        }

        // The journal is what keeps the case alive after this letter goes out: prepared once,
        // it carries the filed and answered dates into every later run, so the timetable is
        // the case as it stands rather than as it stood when the evidence was recorded.
        var journal = outputRoot is null ? null : CaseJournalStore.Load(outputRoot);

        var complaint = MergeWithJournal(prepared.Case!, journal);
        var letter = ComplaintLetter.ToOperator(complaint, session, today);

        // The timeline is written from the context the journal now holds, not from a fresh
        // resolution. Re-preparing a complaint for a case that already has one must not
        // restate deadlines that were settled when it was first prepared.
        var legal = outputRoot is not null
            ? CaseJournalStore.Save(
                outputRoot,
                new CaseJournal { Case = complaint, Notes = journal?.Notes },
                today)
            : journal?.Legal ?? complaint.Resolve(today);

        var timeline = BuildTimeline(complaint, legal, today, prepared.AnchorNote);

        Write(Path.Combine(directory, LetterFile), letter);
        Write(Path.Combine(directory, TimelineFile), timeline);

        Console.WriteLine($"  Prigovor:  {Path.Combine(directory, LetterFile)}");
        Console.WriteLine($"  Rokovi:    {Path.Combine(directory, TimelineFile)}");
        Console.WriteLine();

        Console.Write(timeline);
        Console.WriteLine();
        Console.WriteLine("  Otvorite prigovor, popunite polja označena sa ____ i pošaljite ga");
        Console.WriteLine("  operateru u pisanom obliku. Obavezno tražite potvrdu prijema.");
        Console.WriteLine();

        if (outputRoot is not null)
        {
            Console.WriteLine("  Kada ga pošaljete, zabeležite dan: --podnet 12.09.2026");
            Console.WriteLine("  Stanje predmeta uvek možete videti sa: --predmet");
            Console.WriteLine();
        }

        return true;
    }

    /// <summary>
    /// The dates already recorded in the journal win over the fresh ones: a case re-prepared
    /// a week after the letter went out must not forget that it went out.
    /// </summary>
    private static ComplaintCase MergeWithJournal(ComplaintCase fresh, CaseJournal? journal) =>
        journal?.Case is not { } recorded
            ? fresh
            : fresh with
            {
                SubmittedDate = recorded.SubmittedDate ?? fresh.SubmittedDate,
                OperatorRespondedDate = recorded.OperatorRespondedDate ?? fresh.OperatorRespondedDate,
                OperatorUpheld = recorded.OperatorUpheld ?? fresh.OperatorUpheld,
                RegulatorFiledDate = recorded.RegulatorFiledDate ?? fresh.RegulatorFiledDate,
                InvoiceDueDate = recorded.InvoiceDueDate ?? fresh.InvoiceDueDate,
                ComplaintKind = recorded.ComplaintKind,
                CustomerType = recorded.CustomerType,
                ServiceKind = recorded.ServiceKind,
                OperatorReference = recorded.OperatorReference ?? fresh.OperatorReference,
                ContractNumber = recorded.ContractNumber ?? fresh.ContractNumber,
                ContactPhone = recorded.ContactPhone ?? fresh.ContactPhone,
                ContactEmail = recorded.ContactEmail ?? fresh.ContactEmail,
                SubscriberName = recorded.SubscriberName != "____________________"
                    ? recorded.SubscriberName
                    : fresh.SubscriberName,
            };

    // Two dead copies used to live here - one deciding whether a session supports a complaint
    // and one finding the first upstream outage - both duplicating ComplaintPreparation with
    // wording that had already drifted apart from it. Neither was called.

    private static string BuildTimeline(
        ComplaintCase complaint,
        ResolvedLegalContext legal,
        DateOnly today,
        string? anchorNote = null)
    {
        var builder = new StringBuilder();
        var stage = complaint.StageOn(today, legal);

        builder.AppendLine("  ROKOVI");
        builder.AppendLine();
        builder.AppendLine($"  Stanje predmeta:  {stage.Label()}");
        builder.AppendLine($"  Pravila:          {legal.Ruleset}");
        builder.AppendLine();

        foreach (var milestone in complaint.Milestones(legal))
        {
            var note = !milestone.IsDeadline
                ? string.Empty
                : milestone.DaysFrom(today) switch
                {
                    null => "  ← ROK NIJE UTVRĐEN",
                    < 0 => "  ← ROK JE PROŠAO",
                    0 => "  ← danas je poslednji dan",
                    1 => "  ← ostao još 1 dan",
                    { } days => $"  ← ostalo još {days} {SessionVerdict.Plural(days, "dan", "dana", "dana")}",
                };

            var date = milestone.Date is { } value
                ? value.ToString("dd.MM.yyyy.", SerbianText.Culture)
                : "nije utvrđeno";

            builder.AppendLine($"  {date}  {milestone.Step.Label(),-45}{note}");

            // The source beside the deadline, so the person can check it rather than take it
            // on trust - and so a period that changes can be seen to have changed.
            if (milestone.Rule is { Citations.Count: > 0 } rule)
            {
                builder.AppendLine($"  {' ',12}  ↳ {rule.Value} dana, {string.Join("; ", rule.Citations)}");
            }
            else if (milestone.Rule?.Impediment is { } impediment)
            {
                builder.AppendLine($"  {' ',12}  ↳ {impediment}");
            }
        }

        if (complaint.RegulatorFiledDate is { } filed)
        {
            builder.AppendLine();
            builder.AppendLine($"  RATEL-u prijavljeno: {filed:dd.MM.yyyy.}");
        }

        if (anchorNote is not null)
        {
            builder.AppendLine();
            Paragraph(anchorNote);
        }

        if (legal.State != LegalContextState.Resolved)
        {
            builder.AppendLine();
            Paragraph(legal.State.Explain());
        }

        builder.AppendLine();
        builder.AppendLine("  Šta sada:");
        builder.AppendLine();

        foreach (var line in ConsoleText.Wrap(stage.WhatNow()))
        {
            builder.AppendLine($"    {line}");
        }

        builder.AppendLine();
        Paragraph(CaseText.Disclaimer);

        return builder.ToString();

        void Paragraph(string text)
        {
            foreach (var line in ConsoleText.Wrap(text))
            {
                builder.AppendLine($"  {line}");
            }
        }
    }

    private static void Write(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

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
