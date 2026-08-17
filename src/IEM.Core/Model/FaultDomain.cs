namespace IEM.Core.Model;

/// <summary>
/// How far along the path the fault was isolated to.
/// <para>
/// This replaces the old habit of asking "whose fault is it". A monitor running on the
/// customer's own computer cannot answer that question, and the previous model pretended it
/// could: it labelled a state <c>Upstream</c> and the report then printed "confirmed outages
/// on the operator's side. You have grounds for a complaint." Nothing measured from inside
/// the house establishes that.
/// </para>
/// <para>
/// What can be established is where the problem stops being visible - and that is worth a
/// great deal. A complaint saying "everything up to and including the router answered
/// normally, nothing beyond it did, here is the evidence" is far harder to dismiss than one
/// that opens by assigning blame the customer was never in a position to assign.
/// </para>
/// </summary>
public enum FaultDomain
{
    /// <summary>Not enough was observed to isolate anything.</summary>
    Unknown,

    /// <summary>Nothing is wrong.</summary>
    None,

    /// <summary>This computer: its adapter, its driver, its own configuration.</summary>
    LocalHost,

    /// <summary>The radio link between this computer and the access point.</summary>
    LocalWifi,

    /// <summary>The local wired network between this computer and the router.</summary>
    LocalLan,

    /// <summary>The router itself - customer equipment, whoever supplied it.</summary>
    Cpe,

    /// <summary>
    /// The path beyond the local network.
    /// <para>
    /// Means the fault was isolated past the router, not that the operator is at fault. The
    /// router answering while nothing beyond it does is a strong, specific finding; it is
    /// still not a measurement taken inside the operator's network, and the wording used
    /// everywhere downstream has to keep saying so.
    /// </para>
    /// </summary>
    UpstreamPath,

    /// <summary>Name resolution, with the underlying path demonstrably carrying traffic.</summary>
    Dns,
}

public static class FaultDomainInfo
{
    /// <summary>
    /// Where the fault sits, as far as the observations reach.
    /// <para>
    /// The medium decides whether a local failure is a Wi-Fi problem or a cable problem, so
    /// it has to be passed in: an adapter going down over Wi-Fi and over Ethernet are the
    /// same state and quite different faults.
    /// </para>
    /// </summary>
    public static FaultDomain DomainOf(this NetworkState state, LinkMedium medium = LinkMedium.Unknown) => state switch
    {
        NetworkState.Ok or
        NetworkState.MonitoringGap or
        NetworkState.SelfTest or
        NetworkState.WifiRoaming or
        NetworkState.IcmpFiltered => FaultDomain.None,

        NetworkState.AdapterDown => medium switch
        {
            LinkMedium.Wireless => FaultDomain.LocalWifi,
            LinkMedium.Ethernet => FaultDomain.LocalLan,
            _ => FaultDomain.LocalHost,
        },

        // The access point stopped broadcasting while this computer was working normally.
        NetworkState.WifiRadioDown => FaultDomain.Cpe,

        NetworkState.GatewayDown => medium == LinkMedium.Wireless ? FaultDomain.LocalWifi : FaultDomain.LocalLan,

        NetworkState.CpeReboot => FaultDomain.Cpe,

        // The router answered throughout; nothing past it did.
        NetworkState.CpeUpstreamUnreachable => FaultDomain.UpstreamPath,

        // Nothing answered and there was no gateway to test against, so the fault could sit
        // anywhere along the path. Guessing "upstream" here was the old model's worst habit.
        NetworkState.InternetDown => FaultDomain.Unknown,

        NetworkState.DnsIspFailure or
        NetworkState.DnsGlobalFailure => FaultDomain.Dns,

        NetworkState.PacketLoss or
        NetworkState.HighLatency or
        NetworkState.HighJitter or
        NetworkState.CaptivePortalSuspected => FaultDomain.Unknown,

        _ => FaultDomain.Unknown,
    };

    /// <summary>Serbian label for the interface and the report.</summary>
    public static string Label(this FaultDomain domain) => domain switch
    {
        FaultDomain.None => "bez kvara",
        FaultDomain.LocalHost => "ovaj računar",
        FaultDomain.LocalWifi => "Wi-Fi veza do rutera",
        FaultDomain.LocalLan => "lokalna mreža do rutera",
        FaultDomain.Cpe => "ruter",
        FaultDomain.UpstreamPath => "putanja iza rutera",
        FaultDomain.Dns => "razrešavanje imena (DNS)",
        _ => "nije izolovano",
    };

    /// <summary>
    /// What the finding actually supports, written the way it can stand in a complaint.
    /// <para>
    /// Never "confirmed at the operator". The strongest honest phrasing is that the problem
    /// was isolated beyond the customer's own equipment, and that is what an operator has to
    /// answer to - a claim it cannot simply reject by pointing at the customer's router.
    /// </para>
    /// </summary>
    public static string Explain(this FaultDomain domain) => domain switch
    {
        FaultDomain.None =>
            "Nema kvara.",

        FaultDomain.LocalHost =>
            "Problem je na ovom računaru. Nije osnov za prigovor operateru.",

        FaultDomain.LocalWifi =>
            "Problem je na Wi-Fi vezi između računara i rutera. Ako je ruter operaterov, " +
            "ovo jeste osnov za prijavu, ali kao kvar opreme, ne kao prekid usluge.",

        FaultDomain.LocalLan =>
            "Problem je na lokalnoj mreži između računara i rutera. Proverite kabl i port " +
            "pre prijave operateru.",

        FaultDomain.Cpe =>
            "Problem je na ruteru. Ako je ruter operaterov, prijavljuje se kao kvar opreme.",

        FaultDomain.UpstreamPath =>
            "Problem je izolovan iza vašeg rutera: ruter je tokom celog prekida odgovarao " +
            "normalno, a nijedna spoljna meta nije. To snažno smanjuje verovatnoću da je uzrok " +
            "na putanji između računara i rutera. Nisu isključeni: WAN strana samog rutera, " +
            "njegov firmver, PPPoE sesija ni stanje NAT tabele - sve to sa ovog računara " +
            "izgleda isto kao i prekid dalje u mreži.",

        FaultDomain.Dns =>
            "Problem je u razrešavanju imena. Sama veza je tokom toga prenosila saobraćaj.",

        _ =>
            "Nije bilo dovoljno podataka da se problem izoluje na jedan deo putanje.",
    };

    /// <summary>
    /// Whether the finding is worth putting to the operator at all.
    /// <para>
    /// The router domain is included because on most Serbian connections the router belongs
    /// to the operator - but as an equipment fault, which follows a different path than a
    /// service interruption, and the wording above keeps them apart.
    /// </para>
    /// </summary>
    public static bool WorthReportingToOperator(this FaultDomain domain) =>
        domain is FaultDomain.UpstreamPath or FaultDomain.Cpe or FaultDomain.Dns;
}
