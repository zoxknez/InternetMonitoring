using System.IO;
using System.Text;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Legal;
using IEM.Storage;

namespace IEM.App.Hosting;

/// <param name="LetterPath">Where the complaint was written, when one was.</param>
/// <param name="Refusal">Why nothing was written, when nothing was.</param>
public sealed record ComplaintOutcome(string? LetterPath, string? TimelinePath, string? Refusal)
{
    public bool Written => LetterPath is not null;
}

/// <param name="SubmissionPath">Where the regulator submission was written, when one was.</param>
public sealed record RegulatorOutcome(string? SubmissionPath, string? Refusal)
{
    public bool Written => SubmissionPath is not null;
}

/// <summary>
/// Prepares the complaint from a finished session, for the window.
/// <para>
/// The same rules as the console command, called from one place so the two cannot drift.
/// A window that offers to write a complaint the console would refuse - or the reverse -
/// would be worse than either behaviour on its own.
/// </para>
/// </summary>
public static class ComplaintBuilder
{
    public static ComplaintOutcome Prepare(string directory, string? operatorName, string? outputRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var paths = new SessionPaths(directory);

        if (!File.Exists(paths.RawLog))
        {
            return new ComplaintOutcome(null, null, "U ovom folderu nema sirove evidencije.");
        }

        var session = Load(paths);

        if (session is null)
        {
            return new ComplaintOutcome(null, null, "Sirova evidencija ne sadrži nijednu sesiju.");
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var prepared = ComplaintPreparation.From(session, operatorName, today);

        // Refused when there is nothing to complain about, and the refusal is the point. A
        // letter demanding the cause of outages that were never recorded is the first thing
        // an operator would notice, and it takes the rest of the evidence down with it.
        if (!prepared.Prepared)
        {
            return new ComplaintOutcome(null, null, prepared.Refusal);
        }

        // The journal keeps the case alive after the letter goes out: prepared once, it
        // carries the filed and answered dates into every later run, so the timetable is the
        // case as it stands rather than as it stood when the evidence was recorded.
        var journal = outputRoot is null ? null : CaseJournalStore.Load(outputRoot);
        var complaint = MergeWithJournal(prepared.Case!, journal);

        // The timeline comes from the context the journal now holds rather than from a fresh
        // resolution, so a case prepared twice does not restate deadlines already settled.
        var legal = outputRoot is not null
            ? CaseJournalStore.Save(
                outputRoot,
                new CaseJournal { Case = complaint, Notes = journal?.Notes },
                today)
            : journal?.Legal ?? complaint.Resolve(today);

        var letterPath = Path.Combine(directory, "Prigovor-operateru.txt");
        var timelinePath = Path.Combine(directory, "Rokovi.txt");

        Write(letterPath, ComplaintLetter.ToOperator(complaint, session, today));
        Write(timelinePath, Timeline(complaint, legal, today, prepared.AnchorNote));

        return new ComplaintOutcome(letterPath, timelinePath, null);
    }

    /// <summary>
    /// Prepares the submission to the regulator, or refuses with the reason - before the
    /// document is written, because a submission filed too early is sent back and the person
    /// has spent their effort and part of their window on it.
    /// </summary>
    public static RegulatorOutcome PrepareRegulator(string directory, string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        var journal = CaseJournalStore.Load(outputRoot);

        if (journal is null)
        {
            return new RegulatorOutcome(null,
                "Još nema predmeta. Prvo pripremite prigovor operateru.");
        }

        var paths = new SessionPaths(directory);

        if (!File.Exists(paths.RawLog))
        {
            return new RegulatorOutcome(null, "U ovom folderu nema sirove evidencije.");
        }

        var session = Load(paths);

        if (session is null)
        {
            return new RegulatorOutcome(null, "Sirova evidencija ne sadrži nijednu sesiju.");
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var legal = journal.Legal ?? journal.Case.Resolve(today);
        var obstacle = RegulatorSubmission.CanSubmit(journal.Case, today, legal);

        if (obstacle != RegulatorSubmission.Obstacle.None)
        {
            return new RegulatorOutcome(null, RegulatorSubmission.Explain(obstacle));
        }

        var submission = RegulatorSubmission.Build(journal.Case, session, today, out var _, legal);

        if (submission is null)
        {
            return new RegulatorOutcome(null, "Prijava nije mogla biti sastavljena.");
        }

        var path = Path.Combine(directory, "Prijava-RATEL.txt");
        Write(path, submission);

        return new RegulatorOutcome(path, null);
    }

    /// <summary>
    /// Reads the session snapshot, or null with the reason already carried in the outcome.
    /// Shared by both documents so a session that cannot be read is refused identically.
    /// </summary>
    private static SessionSnapshot? Load(SessionPaths paths)
    {
        var rebuild = EvidenceIndexRebuilder.RebuildForExport(paths);

        try
        {
            using var reader = SessionReader.Open(rebuild.DatabasePath);
            return reader.Load();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            TryDelete(rebuild.DatabasePath);
        }
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

    private static string Timeline(
        ComplaintCase complaint,
        ResolvedLegalContext legal,
        DateOnly today,
        string? anchorNote = null)
    {
        var builder = new StringBuilder();
        var stage = complaint.StageOn(today, legal);

        builder.AppendLine("ROKOVI");
        builder.AppendLine();
        builder.AppendLine($"Stanje predmeta:  {stage.Label()}");
        builder.AppendLine($"Pravila:          {legal.Ruleset}");
        builder.AppendLine();

        foreach (var milestone in complaint.Milestones(legal))
        {
            var note = !milestone.IsDeadline
                ? string.Empty
                : milestone.DaysFrom(today) switch
                {
                    null => "  <- ROK NIJE UTVRDJEN",
                    < 0 => "  <- ROK JE PROSAO",
                    0 => "  <- danas je poslednji dan",
                    1 => "  <- ostao jos 1 dan",
                    { } days => $"  <- ostalo jos {days} {SessionVerdict.Plural(days, "dan", "dana", "dana")}",
                };

            var date = milestone.Date is { } value
                ? value.ToString("dd.MM.yyyy.", SerbianText.Culture)
                : "nije utvrdjeno";

            builder.AppendLine($"{date}  {milestone.Step.Label(),-45}{note}");

            if (milestone.Rule is { Citations.Count: > 0 } rule)
            {
                builder.AppendLine($"{' ',12}  -> {rule.Value} dana, {string.Join("; ", rule.Citations)}");
            }
            else if (milestone.Rule?.Impediment is { } impediment)
            {
                builder.AppendLine($"{' ',12}  -> {impediment}");
            }

            if (milestone.Explain(milestone.Step.Label()) is { } change)
            {
                builder.AppendLine($"{' ',12}  {change}");
            }
        }

        if (complaint.RegulatorFiledDate is { } filed)
        {
            builder.AppendLine();
            builder.AppendLine($"RATEL-u prijavljeno: {filed:dd.MM.yyyy.}");
        }

        if (anchorNote is not null)
        {
            builder.AppendLine();
            builder.AppendLine(anchorNote);
        }

        if (legal.State != LegalContextState.Resolved)
        {
            builder.AppendLine();
            builder.AppendLine(legal.State.Explain());
        }

        builder.AppendLine();
        builder.AppendLine("Šta sada:");
        builder.AppendLine();
        builder.AppendLine(stage.WhatNow());
        builder.AppendLine();
        builder.AppendLine(CaseText.Disclaimer);

        return builder.ToString();
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
}
