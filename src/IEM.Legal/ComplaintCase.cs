namespace IEM.Legal;

/// <summary>Where the complaint has got to.</summary>
public enum CaseStage
{
    /// <summary>Evidence is being gathered; nothing has been filed.</summary>
    Gathering,

    /// <summary>The complaint can be filed, and the window is still open.</summary>
    ReadyToFile,

    /// <summary>Filed, waiting for the operator.</summary>
    AwaitingOperator,

    /// <summary>The operator's deadline passed without an answer.</summary>
    OperatorSilent,

    /// <summary>The operator answered and accepted the complaint.</summary>
    Upheld,

    /// <summary>The operator answered and refused. The regulator is the next step.</summary>
    Refused,

    /// <summary>
    /// The window to file, or to escalate, has closed.
    /// <para>
    /// Stated plainly rather than hidden. Someone whose window has shut is better served by
    /// being told so than by a form that lets them spend an evening writing something nobody
    /// will read.
    /// </para>
    /// </summary>
    Expired,
}

/// <param name="OperatorName">Whoever the contract is with.</param>
/// <param name="SubscriberName">The person whose name is on the contract.</param>
public sealed record ComplaintCase
{
    public required string OperatorName { get; init; }

    public required string SubscriberName { get; init; }

    /// <summary>Subscriber or contract number, as it appears on the bill.</summary>
    public string? ContractNumber { get; init; }

    /// <summary>Address the service is delivered to.</summary>
    public string? ServiceAddress { get; init; }

    public string? ContactPhone { get; init; }

    public string? ContactEmail { get; init; }

    /// <summary>Contracted download rate, when the complaint concerns speed.</summary>
    public double? ContractedDownloadMbps { get; init; }

    /// <summary>
    /// When the service could not be used - the day a quality complaint is counted from.
    /// <para>
    /// A session with three outages offers three candidate dates, and which one the complaint
    /// is about is a choice. The program proposes the first and records what was chosen; it
    /// does not take the first silently, which is why <see cref="EventOrigin"/> travels with
    /// this date.
    /// </para>
    /// </summary>
    public required DateOnly EventDate { get; init; }

    /// <summary>Where <see cref="EventDate"/> came from.</summary>
    /// <remarks>
    /// Absent from case files written before 2.7, which therefore read back as
    /// <see cref="FactOrigin.Unknown"/> - the truth about them. Those cases are reconstructed
    /// from their recorded dates and say so.
    /// </remarks>
    public FactOrigin EventOrigin { get; init; } = FactOrigin.Unknown;

    /// <summary>The record <see cref="EventDate"/> was derived from, when it was derived.</summary>
    public string? EventEvidenceRef { get; init; }

    /// <summary>
    /// The day the disputed invoice fell due, for a complaint about the amount charged.
    /// <para>
    /// No fallback to the outage date. A billing complaint runs from the day the invoice fell
    /// due, and taking an incident date instead would be the same substitution this release
    /// exists to remove - in the legal layer this time.
    /// </para>
    /// </summary>
    public DateOnly? InvoiceDueDate { get; init; }

    /// <summary>The day the service was provided, where the complaint runs from that instead.</summary>
    public DateOnly? ServiceProvidedDate { get; init; }

    /// <summary>What is being disputed, which decides what the period is counted from.</summary>
    public ComplaintKind ComplaintKind { get; init; } = ComplaintKind.ServiceQuality;

    /// <summary>Whether consumer protection law applies on top of the sector rules.</summary>
    public CustomerType CustomerType { get; init; } = CustomerType.Consumer;

    public ServiceKind ServiceKind { get; init; } = ServiceKind.Standard;

    public DateOnly? SubmittedDate { get; init; }

    public DateOnly? OperatorRespondedDate { get; init; }

    /// <summary>
    /// When the request reached the Regulator, once it did.
    /// <para>
    /// The day the proceeding before the Regulator begins, which is what the transitional rule
    /// turns on - and what stops the report from nagging about a window that was used in time.
    /// </para>
    /// </summary>
    public DateOnly? RegulatorFiledDate { get; init; }

    /// <summary>True when the operator's answer accepted the complaint.</summary>
    public bool? OperatorUpheld { get; init; }

    /// <summary>Reference the operator gave the complaint, once they give one.</summary>
    public string? OperatorReference { get; init; }

    /// <summary>The events the law counts from, as this case records them.</summary>
    public CaseFacts Facts => new()
    {
        CustomerType = CustomerType,
        ServiceKind = ServiceKind,
        ComplaintKind = ComplaintKind,
        InvoiceDue = AnchoredDate.From(InvoiceDueDate, Origin),
        ServiceProvided = AnchoredDate.From(ServiceProvidedDate, Origin),
        ServiceUnavailable = new AnchoredDate(EventDate, Origin, EventEvidenceRef),
        ComplaintFiled = AnchoredDate.From(SubmittedDate, Origin),
        ResponseReceived = AnchoredDate.From(OperatorRespondedDate, Origin),
        RegulatorProceedingFiled = AnchoredDate.From(RegulatorFiledDate, Origin),
    };

    private FactOrigin Origin => EventOrigin;

    /// <summary>The rules that apply to this case, worked out step by step.</summary>
    public ResolvedLegalContext Resolve(DateOnly today) => LegalRegistry.Resolve(Facts, today);

    public IReadOnlyList<ComplaintMilestone> Milestones(ResolvedLegalContext context) =>
        ComplaintDeadlines.Build(
            context,
            new AnchoredDate(EventDate, Origin, EventEvidenceRef),
            SubmittedDate,
            OperatorRespondedDate,
            RegulatorFiledDate);

    /// <summary>The timetable as of today, resolving the rules from scratch.</summary>
    public IReadOnlyList<ComplaintMilestone> Milestones(DateOnly today) => Milestones(Resolve(today));

    /// <summary>
    /// Where the case stands today.
    /// <para>
    /// Derived rather than stored, so it can never disagree with the dates it is derived
    /// from - a case marked "waiting for the operator" while its deadline sits three weeks
    /// in the past would be worse than no status at all.
    /// </para>
    /// </summary>
    public CaseStage StageOn(DateOnly today) => StageOn(today, Resolve(today));

    /// <summary>
    /// The same, against rules that were already worked out - the case file's own, when it
    /// carries them, so an old case keeps the position it had.
    /// </summary>
    public CaseStage StageOn(DateOnly today, ResolvedLegalContext context)
    {
        var milestones = Milestones(context);

        if (SubmittedDate is null)
        {
            // An unsettled deadline never expires the case. "I could not work out your
            // deadline" must not become "your deadline has passed", which would stop somebody
            // filing a complaint they were still entitled to file.
            return Passed(ComplaintStep.ComplaintDue) ? CaseStage.Expired : CaseStage.ReadyToFile;
        }

        if (OperatorRespondedDate is null)
        {
            if (Passed(ComplaintStep.RegulatorDisputeDue))
            {
                return CaseStage.Expired;
            }

            return Passed(ComplaintStep.OperatorResponseDue)
                ? CaseStage.OperatorSilent
                : CaseStage.AwaitingOperator;
        }

        if (OperatorUpheld == true)
        {
            return CaseStage.Upheld;
        }

        return Passed(ComplaintStep.RegulatorDisputeDue) ? CaseStage.Expired : CaseStage.Refused;

        bool Passed(ComplaintStep step) =>
            milestones.FirstOrDefault(m => m.Step == step)?.Date is { } date && today > date;
    }
}

/// <summary>Serbian wording for the case, kept beside the rules so every surface agrees.</summary>
public static class CaseText
{
    public static string Label(this ComplaintStep step) => step switch
    {
        ComplaintStep.Event => "Datum kvara ili spornog računa",
        ComplaintStep.ComplaintDue => "Krajnji rok za podnošenje prigovora operateru",
        ComplaintStep.ComplaintSubmitted => "Prigovor podnet",
        ComplaintStep.OperatorResponseDue => "Krajnji rok da operater odgovori",
        ComplaintStep.OperatorResponded => "Operater odgovorio",
        ComplaintStep.RegulatorDisputeDue => "Krajnji rok za obraćanje RATEL-u",
        ComplaintStep.RegulatorDecisionTarget => "Rok za odluku RATEL-a",
        _ => step.ToString(),
    };

    /// <summary>
    /// What happened to a period since it was first worked out, in the words a person needs.
    /// <para>
    /// Two very different things end up here and must not read alike. A fallback anchor giving
    /// way to the primary one is the law working as written - the event it counts from finally
    /// happened - and the new date is the answer. A date that was already used being given a
    /// different value is a disagreement the program will not resolve on its own.
    /// </para>
    /// </summary>
    public static string? Explain(this ComplaintMilestone milestone, string stepLabel)
    {
        ArgumentNullException.ThrowIfNull(milestone);

        if (milestone.Rule is not { } rule)
        {
            return null;
        }

        if (rule.Conflict is { } conflict)
        {
            return $"UPOZORENJE - {conflict}";
        }

        if (rule.Superseded is not { Reason: ResolutionChange.PrimaryAnchorBecameAvailable } was)
        {
            return rule.Resolution == ResolutionState.Provisional
                ? "Rok je privremen: računat je od dana do kog je odgovor bio dužan, jer odgovor " +
                  "još nije evidentiran. Kada se odgovor unese, rok se računa od dana njegovog prijema."
                : null;
        }

        return
            $"Rok je prvobitno privremeno izračunat do {was.Due:dd.MM.yyyy.} na osnovu roka za " +
            $"odgovor operatera ({was.AnchoredOn:dd.MM.yyyy.}). Odgovor je naknadno evidentiran " +
            $"{rule.AnchoredOn?.Date:dd.MM.yyyy.} pa primarno zakonsko uporište daje rok " +
            $"{rule.Due:dd.MM.yyyy.} Pravila predmeta nisu promenjena.";
    }

    /// <summary>
    /// What the program could establish about the legal position, said plainly.
    /// <para>
    /// A case file written before 2.7 has no record of which rules were applied to it. The
    /// honest answer is that they were reconstructed, or that they could not be - not a
    /// silent fallback to the old regime, which would hand somebody a fifteen-day deadline
    /// that stopped existing at the start of 2025.
    /// </para>
    /// </summary>
    public static string Explain(this LegalContextState state) => state switch
    {
        LegalContextState.Resolved => string.Empty,

        LegalContextState.InferredFromRecordedDates =>
            "Pravni režim nije bio zapisan uz predmet, nego je rekonstruisan iz datuma koji u " +
            "njemu stoje. Proverite rokove pre nego što se na njih oslonite.",

        _ =>
            "Pravni režim: nije moguće pouzdano utvrditi. Predmet je napravljen starijom " +
            "verzijom aplikacije koja nije beležila pravni režim, ili nema dovoljno datuma. " +
            "Rok neće biti automatski proglašen isteklim ni otvorenim.",
    };

    public static string Label(this CaseStage stage) => stage switch
    {
        CaseStage.Gathering => "Prikupljanje dokaza",
        CaseStage.ReadyToFile => "Spremno za podnošenje",
        CaseStage.AwaitingOperator => "Čeka se odgovor operatera",
        CaseStage.OperatorSilent => "Operater nije odgovorio u roku",
        CaseStage.Upheld => "Prigovor usvojen",
        CaseStage.Refused => "Prigovor odbijen",
        _ => "Rok je istekao",
    };

    /// <summary>What to do next, in the words of someone explaining it to a neighbour.</summary>
    public static string WhatNow(this CaseStage stage) => stage switch
    {
        // Not "48 sati je minimum koji se ne može osporiti". Nothing prescribes a minimum
        // monitoring period; the forty-eight hours in the Pravilnik is the time an operator
        // has to clear a fault once the throughput has fallen below the minimum, which is a
        // different thing entirely and was quoted here as if it were a rule about evidence.
        CaseStage.Gathering =>
            "Pustite nadzor da radi dovoljno dugo da prekidi budu nesumnjivi. Nekoliko dana " +
            "neprekidnog nadzora daleko je ubedljivije od nekoliko sati, jer pokazuje da se " +
            "prekidi ponavljaju.",

        CaseStage.ReadyToFile =>
            "Podnesite prigovor operateru u pisanom obliku i tražite potvrdu prijema sa brojem " +
            "predmeta. Bez potvrde prijema kasnije nemate čime da dokažete da ste se javili u roku.",

        CaseStage.AwaitingOperator =>
            "Sačekajte odgovor. Ako ga ne bude do isteka roka, to samo po sebi ide u vašu " +
            "korist - ćutanje operatera je razlog više za obraćanje RATEL-u.",

        CaseStage.OperatorSilent =>
            "Rok je istekao bez odgovora. Možete pokrenuti vansudsko rešavanje spora pred " +
            "RATEL-om, i u zahtevu navedite da operater nije odgovorio u zakonskom roku. " +
            "Konkretan rok za taj zahtev, sa izvorom, stoji uz rokove ovog predmeta.",

        CaseStage.Upheld =>
            "Prigovor je usvojen. Sačuvajte odgovor operatera - ako se isti kvar ponovi, on je " +
            "dokaz da je problem već bio priznat.",

        CaseStage.Refused =>
            "Prigovor je odbijen. Sledeći korak je RATEL. Priložite i odgovor operatera i " +
            "celu evidenciju, jer RATEL ceni obe strane.",

        _ =>
            "Rok je prošao. Za nove prekide pokrenite nov predmet - rokovi teku od svakog " +
            "događaja posebno, pa stariji propušten rok ne gasi pravo na prigovor za nov kvar.",
    };

    /// <summary>
    /// The sentence that has to appear on anything this module generates.
    /// <para>
    /// The tool prepares documents; it does not give legal advice, and the periods it counts
    /// with are defaults that a particular contract or a change in regulation can override.
    /// Saying so is not a disclaimer for its own sake - someone who checks their own contract
    /// because of this line is better off than someone who trusted a generated date.
    /// </para>
    /// </summary>
    public const string Disclaimer =
        "Ovaj program priprema dokumentaciju i računa rokove prema uobičajenim vrednostima. " +
        "Nije pravni savet. Proverite rokove u svom ugovoru i u važećim propisima, jer se " +
        "mogu razlikovati od podrazumevanih.";
}
