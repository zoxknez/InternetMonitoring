namespace IEM.Legal;

/// <summary>
/// The event a legal period is counted from.
/// <para>
/// Per rule, not per case. The law uses different moments for different periods - the
/// complaint runs from the day the service failed or the bill fell due, the operator's answer
/// from the day the complaint arrived, the dispute before the Regulator from the answer or
/// the day it was owed. A single "case date" collapses all of that into one number and gets
/// three of the four deadlines wrong whenever the case is not a simple one.
/// </para>
/// </summary>
public enum LegalAnchor
{
    /// <summary>The day the disputed invoice fell due.</summary>
    InvoiceDueDate,

    /// <summary>The day the service was provided, where that is what the period runs from.</summary>
    ServiceProvidedDate,

    /// <summary>The day the service could not be used - what this tool actually measures.</summary>
    ServiceUnavailableDate,

    /// <summary>The day the complaint reached the operator.</summary>
    ProviderComplaintFiled,

    /// <summary>The day the operator's answer arrived.</summary>
    ProviderResponseReceived,

    /// <summary>The day the operator's answer was owed, when none came.</summary>
    ProviderResponseDue,

    /// <summary>
    /// The day the Regulator received the request for out-of-court settlement.
    /// <para>
    /// The moment the proceeding before the Regulator begins, and therefore the moment the
    /// transitional rule turns on: proceedings started before 1 January 2025 finish under the
    /// rules they started under. Filing a complaint with the operator in 2024 does not start
    /// that proceeding, and does not pull the whole case into the old regime.
    /// </para>
    /// </summary>
    RegulatorProceedingFiled,
}

/// <summary>What the subscriber is disputing.</summary>
/// <remarks>
/// Separate from <see cref="ServiceKind"/>, which says which service is involved. Both are
/// needed: a consumer on a standard service gets 30 days either way, but for a bill it runs
/// from the day the invoice fell due and for an outage from the day the service failed. The
/// number being the same is exactly why storing only the number is not enough.
/// </remarks>
public enum ComplaintKind
{
    /// <summary>Quality or availability of the service - what this tool measures.</summary>
    ServiceQuality,

    /// <summary>The amount charged.</summary>
    BillingAmount,
}

/// <summary>Whether consumer protection law applies on top of the sector rules.</summary>
public enum CustomerType
{
    /// <summary>A natural person outside their trade - the Zakon o zaštiti potrošača applies.</summary>
    Consumer,

    /// <summary>A company or entrepreneur, for whom the sector rules stand alone.</summary>
    NonConsumer,
}

/// <summary>Which service the complaint is about, where that changes the period.</summary>
public enum ServiceKind
{
    Standard,

    /// <summary>Roaming, international calls and value-added services, which have their own period.</summary>
    RoamingInternationalVas,
}

/// <summary>
/// Where a date came from.
/// <para>
/// Recorded because a year later "why does the program think the outage was on the first?" has
/// to have an answer. A session with three outages has three candidate dates, and the one the
/// complaint is about is a choice - not something the program may make silently by taking the
/// first.
/// </para>
/// </summary>
public enum FactOrigin
{
    /// <summary>Not established. The resting state, and never a licence to invent one.</summary>
    Unknown = 0,

    /// <summary>The subscriber stated it.</summary>
    UserProvided,

    /// <summary>Derived from the recorded session, with the record it came from named.</summary>
    DerivedFromSession,

    /// <summary>Read from a case file written before the program recorded provenance.</summary>
    ImportedLegacy,
}

/// <param name="Date">The date itself.</param>
/// <param name="Origin">How the program came to believe it.</param>
/// <param name="EvidenceRef">The record it was derived from, when it was derived.</param>
public sealed record AnchoredDate(DateOnly Date, FactOrigin Origin, string? EvidenceRef = null)
{
    public static AnchoredDate? From(DateOnly? date, FactOrigin origin, string? evidenceRef = null) =>
        date is { } value ? new AnchoredDate(value, origin, evidenceRef) : null;

    /// <summary>Where the date came from, for the record and for the report.</summary>
    public string Describe() => Origin switch
    {
        FactOrigin.UserProvided => "uneto ručno",
        FactOrigin.DerivedFromSession => EvidenceRef is null
            ? "izvedeno iz sesije"
            : $"izvedeno iz sesije ({EvidenceRef})",
        FactOrigin.ImportedLegacy => "preuzeto iz starijeg predmeta, bez zapisa o poreklu",
        _ => "poreklo nije zabeleženo",
    };
}
