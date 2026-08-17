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

    /// <summary>When the fault happened, or the disputed bill arrived.</summary>
    public required DateOnly EventDate { get; init; }

    public DateOnly? SubmittedDate { get; init; }

    public DateOnly? OperatorRespondedDate { get; init; }

    /// <summary>True when the operator's answer accepted the complaint.</summary>
    public bool? OperatorUpheld { get; init; }

    /// <summary>Reference the operator gave the complaint, once they give one.</summary>
    public string? OperatorReference { get; init; }

    public IReadOnlyList<ComplaintMilestone> Milestones(ComplaintPeriods? periods = null) =>
        ComplaintDeadlines.Build(EventDate, SubmittedDate, OperatorRespondedDate, periods);

    /// <summary>
    /// Where the case stands today.
    /// <para>
    /// Derived rather than stored, so it can never disagree with the dates it is derived
    /// from - a case marked "waiting for the operator" while its deadline sits three weeks
    /// in the past would be worse than no status at all.
    /// </para>
    /// </summary>
    public CaseStage StageOn(DateOnly today, ComplaintPeriods? periods = null)
    {
        var p = periods ?? ComplaintPeriods.Default;
        var milestones = Milestones(p);

        if (SubmittedDate is null)
        {
            var due = milestones.First(m => m.Step == ComplaintStep.ComplaintDue).Date;
            return today > due ? CaseStage.Expired : CaseStage.ReadyToFile;
        }

        if (OperatorRespondedDate is null)
        {
            var responseDue = milestones.First(m => m.Step == ComplaintStep.OperatorResponseDue).Date;
            var regulatorDue = milestones.First(m => m.Step == ComplaintStep.RegulatorDue).Date;

            if (today > regulatorDue)
            {
                return CaseStage.Expired;
            }

            return today > responseDue ? CaseStage.OperatorSilent : CaseStage.AwaitingOperator;
        }

        if (OperatorUpheld == true)
        {
            return CaseStage.Upheld;
        }

        var escalationDue = milestones.First(m => m.Step == ComplaintStep.RegulatorDue).Date;
        return today > escalationDue ? CaseStage.Expired : CaseStage.Refused;
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
        ComplaintStep.RegulatorDue => "Krajnji rok za obraćanje RATEL-u",
        _ => step.ToString(),
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
        CaseStage.Gathering =>
            "Pustite nadzor da radi dovoljno dugo da prekidi budu nesumnjivi. Za prigovor je " +
            "obično dovoljno nekoliko dana, ali 48 sati je minimum koji se ne može osporiti.",

        CaseStage.ReadyToFile =>
            "Podnesite prigovor operateru u pisanom obliku i tražite potvrdu prijema sa brojem " +
            "predmeta. Bez potvrde prijema kasnije nemate čime da dokažete da ste se javili u roku.",

        CaseStage.AwaitingOperator =>
            "Sačekajte odgovor. Ako ga ne bude do isteka roka, to samo po sebi ide u vašu " +
            "korist - ćutanje operatera je razlog više za obraćanje RATEL-u.",

        CaseStage.OperatorSilent =>
            "Rok je istekao bez odgovora. Možete se obratiti RATEL-u, i u prijavi navedite da " +
            "operater nije odgovorio u zakonskom roku.",

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
