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

    public static bool Run(string directory, string? operatorName, string? outputRoot = null)
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
        var prepared = ComplaintPreparation.From(session, operatorName, today);

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
        var timeline = BuildTimeline(complaint, today, journal?.RegulatorFiledDate);

        if (outputRoot is not null)
        {
            CaseJournalStore.Save(outputRoot, new CaseJournal
            {
                Case = complaint,
                RegulatorFiledDate = journal?.RegulatorFiledDate,
                Notes = journal?.Notes,
            });
        }

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
                OperatorReference = recorded.OperatorReference ?? fresh.OperatorReference,
                ContractNumber = recorded.ContractNumber ?? fresh.ContractNumber,
                ContactPhone = recorded.ContactPhone ?? fresh.ContactPhone,
                ContactEmail = recorded.ContactEmail ?? fresh.ContactEmail,
                SubscriberName = recorded.SubscriberName != "____________________"
                    ? recorded.SubscriberName
                    : fresh.SubscriberName,
            };

    /// <summary>
    /// Whether this session supports a complaint at all.
    /// <para>
    /// Two separate ways it might not: nothing went wrong, or something did but the
    /// recording is too short for anyone to draw a conclusion from. Both produce a refusal
    /// rather than a document, because a complaint that overstates what was measured is
    /// worse for its author than no complaint - the operator answers the weakest sentence
    /// in it and the rest goes with it.
    /// </para>
    /// </summary>
    private static bool HasSomethingToComplainAbout(SessionSnapshot session, out string reason)
    {
        var upstream = session.Incidents
            .Count(i => i.Attribution == IEM.Core.Model.FaultAttribution.Upstream);

        if (upstream == 0)
        {
            var local = session.Incidents.Count;

            reason = local > 0
                ? $"Zabeleženo je {local} prekida, ali nijedan nije isključio vašu opremu kao uzrok. " +
                  "Takvi prekidi se ne mogu pripisati operateru."
                : "U ovoj sesiji nije zabeležen nijedan prekid usluge.";

            return false;
        }

        if (session.MonitoredTime < SessionVerdict.MinimumUsefulDuration)
        {
            reason =
                $"Nadzor je trajao samo {SerbianText.Duration(session.MonitoredTime)}, što je prekratko " +
                "da bi se izveo zaključak koji se može braniti.";

            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The date the clock runs from: the first outage that ruled out the customer's own
    /// equipment, since that is the one the complaint is about.
    /// </summary>
    private static DateOnly? FirstUpstreamOutage(SessionSnapshot session)
    {
        var first = session.Incidents
            .Where(i => i.Attribution == IEM.Core.Model.FaultAttribution.Upstream)
            .OrderBy(i => i.StartedUtc)
            .FirstOrDefault();

        return first is null ? null : DateOnly.FromDateTime(first.StartedUtc.LocalDateTime);
    }

    private static string BuildTimeline(ComplaintCase complaint, DateOnly today, DateOnly? regulatorFiled = null)
    {
        var builder = new StringBuilder();
        var stage = complaint.StageOn(today);

        builder.AppendLine("  ROKOVI");
        builder.AppendLine();
        builder.AppendLine($"  Stanje predmeta:  {stage.Label()}");
        builder.AppendLine();

        foreach (var milestone in complaint.Milestones())
        {
            var days = milestone.DaysFrom(today);

            var note = !milestone.IsDeadline
                ? string.Empty
                : days switch
                {
                    < 0 => "  ← ROK JE PROŠAO",
                    0 => "  ← danas je poslednji dan",
                    1 => "  ← ostao još 1 dan",
                    _ => $"  ← ostalo još {days} {SessionVerdict.Plural(days, "dan", "dana", "dana")}",
                };

            builder.AppendLine($"  {milestone.Date:dd.MM.yyyy.}  {milestone.Step.Label(),-45}{note}");
        }

        if (regulatorFiled is { } filed)
        {
            builder.AppendLine();
            builder.AppendLine($"  RATEL-u prijavljeno: {filed:dd.MM.yyyy.}");
        }

        builder.AppendLine();
        builder.AppendLine("  Šta sada:");
        builder.AppendLine();

        foreach (var line in WrapLines(stage.WhatNow(), 70))
        {
            builder.AppendLine($"    {line}");
        }

        builder.AppendLine();

        foreach (var line in WrapLines(CaseText.Disclaimer, 70))
        {
            builder.AppendLine($"  {line}");
        }

        return builder.ToString();
    }

    private static IEnumerable<string> WrapLines(string text, int width)
    {
        var line = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
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
