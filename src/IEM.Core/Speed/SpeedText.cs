using System.Net.Sockets;

namespace IEM.Core.Speed;

/// <summary>
/// Serbian wording for speed findings.
/// <para>
/// Kept beside the rules rather than in the report builder, because the console, the window
/// and the exported document have to say the same thing. A measurement described as usable
/// on screen and unusable in the report would be worse than either.
/// </para>
/// </summary>
public static class SpeedText
{
    /// <summary>What is wrong with the measurement, and what to do about it.</summary>
    public static string Explain(this SpeedMeasurementDefect defect) => defect switch
    {
        SpeedMeasurementDefect.NotWired =>
            "Merenje je rađeno preko Wi-Fi veze. Za dokazivanje ugovorene brzine merenje mora " +
            "biti preko mrežnog kabla, direktno na ruter. Wi-Fi je vaša veza do rutera i po " +
            "pravilu je najsporiji deo putanje - operater će s pravom pokazati na nju.",

        SpeedMeasurementDefect.LinkSlowerThanContract =>
            "Brzina mrežnog porta je manja ili jednaka ugovorenoj brzini, pa merenje pokazuje " +
            "ograničenje kabla ili mrežne kartice, a ne usluge. Potreban je gigabitni port i kabl.",

        SpeedMeasurementDefect.LinkBusy =>
            "Tokom merenja računar je koristio vezu za nešto drugo. Izmerena je preostala " +
            "brzina, ne raspoloživa. Zatvorite preuzimanja, video pozive i striming, pa ponovite.",

        SpeedMeasurementDefect.ConnectionDegraded =>
            "Tokom merenja veza nije radila ispravno, pa rezultat opisuje kvar, a ne brzinu.",

        SpeedMeasurementDefect.PathAmbiguous =>
            "Deo saobraćaja bi izašao kroz drugi adapter ili kroz VPN, pa se ne zna šta je " +
            "tačno mereno. Isključite VPN i ostale adaptere, pa ponovite.",

        SpeedMeasurementDefect.PathElsewhere =>
            "Saobraćaj ka meti merenja ne izlazi kroz adapter koji se nadzire, nego kroz drugi. " +
            "Izmereno je nešto, ali ne ova veza. Isključite VPN, ili merite adapter kroz koji " +
            "saobraćaj stvarno izlazi.",

        SpeedMeasurementDefect.PathUnverified =>
            "Nije se moglo utvrditi kroz koji adapter saobraćaj izlazi, pa se ne zna na koju se " +
            "vezu rezultat odnosi. Merenje ostaje zapisano, ali ne može uz prigovor. Navedite " +
            "adapter opcijom --interfejs i proverite da ime mete merenja može da se razreši.",

        SpeedMeasurementDefect.ActualPathMismatch =>
            "Veze koje je merenje otvorilo nisu sve izašle kroz adapter na koji se rezultat " +
            "odnosi. Ovo nije predviđanje iz tabele ruta nego ono što se stvarno desilo, pa " +
            "figura opisuje drugu vezu. Isključite VPN i ostale adaptere, pa ponovite.",

        SpeedMeasurementDefect.ContractUnknown =>
            "Ugovorena brzina nije uneta, pa nema sa čim da se uporedi izmereno.",

        _ => string.Empty,
    };

    /// <summary>
    /// What the route table established, said as what it is.
    /// <para>
    /// The best case is deliberately not called a confirmed measurement path. The route table
    /// describes the choice the operating system would make; the socket that carried the
    /// transfer was never inspected, and a wording that implied otherwise would be the same
    /// overreach this release exists to remove.
    /// </para>
    /// </summary>
    public static string Label(this MeasurementRouteState state) => state switch
    {
        MeasurementRouteState.AllResolvedRoutesMatch => "tabela ruta je saglasna sa izabranim adapterom",
        MeasurementRouteState.MixedRoutes => "deo ruta izlazi kroz drugi adapter",
        MeasurementRouteState.OtherRouteOnly => "rute izlaze kroz drugi adapter",
        _ => "putanja merenja nije proverena",
    };

    /// <summary>
    /// The same finding with the addresses named, so a mixed result is actionable.
    /// <para>
    /// "Putanja je dvosmislena" tells nobody what to change. "IPv6 ide kroz drugi adapter"
    /// does, and it is the one detail the check already had in hand.
    /// </para>
    /// </summary>
    public static string Describe(this MeasurementRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        var text = route.State.Label();

        var elsewhere = route.Elsewhere
            .Select(candidate => candidate.Family.Label())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (elsewhere.Length > 0)
        {
            text += $" ({string.Join(" i ", elsewhere)} drugom rutom)";
        }

        if (route.UnresolvedCount > 0)
        {
            text += $"; {route.UnresolvedCount} adresa se nije mogla razrešiti";
        }

        return text;
    }

    private static string Label(this AddressFamily family) => family switch
    {
        AddressFamily.InterNetwork => "IPv4",
        AddressFamily.InterNetworkV6 => "IPv6",
        _ => family.ToString(),
    };

    /// <summary>
    /// What was observed about the sockets, said as observation.
    /// <para>
    /// „Potvrđena putanja" ostaje rezervisano i posle ovoga: ovo jeste jače od tabele ruta, ali
    /// govori o vezama koje je merenje otvorilo, ne o svemu što bi korisnikov saobraćaj uradio.
    /// </para>
    /// </summary>
    public static string Label(this PathAgreementState state) => state switch
    {
        PathAgreementState.Match => "veze merenja izašle su kroz izabrani adapter",
        PathAgreementState.Mismatch => "veze merenja nisu sve izašle kroz izabrani adapter",
        _ => "veze merenja nisu posmatrane",
    };

    /// <summary>The same, with the addresses named where they disagree.</summary>
    public static string Describe(this PathAgreement agreement)
    {
        ArgumentNullException.ThrowIfNull(agreement);

        var text = agreement.State.Label();

        if (agreement.Attempts.Count > 0)
        {
            text += $" ({agreement.Attempts.Count} veza)";
        }

        // How many of them, not merely that some did. The first live run of the mismatch case
        // had every single connection leaving through the other adapter while the sentence
        // said "some" - understating the one finding the reader most needs.
        var elsewhere = agreement.Elsewhere.ToArray();

        if (elsewhere.Length > 0)
        {
            var adapters = elsewhere
                .Select(attempt => attempt.Observed!.Name)
                .Distinct(StringComparer.Ordinal);

            text += $"; {elsewhere.Length} od {agreement.Attempts.Count} izašlo kroz: {string.Join(", ", adapters)}";
        }

        if (agreement.UnresolvedCount > 0)
        {
            text += $"; za {agreement.UnresolvedCount} veza adapter nije utvrđen";
        }

        return text;
    }

    /// <summary>
    /// What a recorded note says its own sockets did.
    /// <para>
    /// Built from the fields the note stores rather than from a live <see cref="PathAgreement"/>,
    /// because a report is usually rendered from a file - and often from one written by a build
    /// that never watched the sockets at all. That file must read as "not observed", which is
    /// what it is, and not quietly borrow the agreement of the session rendering it.
    /// </para>
    /// </summary>
    public static string DescribeObservedPath(this SpeedMeasurementNote note)
    {
        ArgumentNullException.ThrowIfNull(note);

        var text = note.ActualPathState.Label();

        if (note.ObservedConnections > 0)
        {
            text += $" ({note.ObservedConnections} veza)";
        }

        if (note.ObservedInterfaces.Count > 0)
        {
            text += $"; izašlo kroz: {string.Join(", ", note.ObservedInterfaces)}";
        }

        if (note.UnresolvedConnections > 0)
        {
            text += $"; za {note.UnresolvedConnections} veza adapter nije utvrđen";
        }

        return text;
    }

    /// <summary>
    /// Where one measurement falls, as a share of the contracted rate.
    /// <para>
    /// Neutral bands rather than the regulator's terms. "Uobičajeno dostupna brzina" is
    /// defined by the Pravilnik as at least 80 % of the contracted rate <em>over 90 % of the
    /// measurement time</em> - a condition about a series, which a single measurement cannot
    /// satisfy however good the figure. Printing that phrase beside one snapshot claimed a
    /// regulatory conclusion the measurement had not established, and handed an operator an
    /// easy objection to the whole document.
    /// </para>
    /// </summary>
    public static string Label(this SpeedBand band) => band switch
    {
        SpeedBand.BelowMinimum => "ISPOD 70 % UGOVORENE",
        SpeedBand.AboveMinimum => "70-80 % ugovorene",
        SpeedBand.NormallyAvailable => "80-90 % ugovorene",
        _ => "90 % ugovorene ili više",
    };

    /// <summary>
    /// What the band means for a complaint, and what one measurement can and cannot settle.
    /// <para>
    /// The minimum is the one criterion a single measurement can speak to: the Pravilnik sets
    /// it as a floor the service must not fall below, without a share-of-time condition. Every
    /// band above it is defined over time, so a snapshot can only support or fail to support -
    /// never conclude.
    /// </para>
    /// </summary>
    public static string Explain(this SpeedBand band) => band switch
    {
        SpeedBand.BelowMinimum =>
            "Izmerena brzina je ispod 70 % ugovorene, što je minimum koji propis predviđa. Ovo je " +
            "osnov za prigovor, pod uslovom da je merenje rađeno preko kabla i uz mirnu vezu. " +
            SeriesNote,

        SpeedBand.AboveMinimum =>
            "Brzina je iznad propisanog minimuma od 70 %, ali ispod 80 % ugovorene. Jedno ovakvo " +
            "merenje nije osnov za prigovor; ponovljena merenja tokom više dana jesu argument. " +
            SeriesNote,

        SpeedBand.NormallyAvailable =>
            "Brzina je između 80 i 90 % ugovorene. Ovo merenje ne pokazuje pad ispod propisanog " +
            "minimuma. " + NotProofOfService + " " + SeriesNote,

        _ =>
            "Brzina je na nivou ugovorene ili iznad njega. Ovo merenje ne pokazuje pad ispod " +
            "propisanog minimuma. " + NotProofOfService + " " + SeriesNote,
    };

    /// <summary>
    /// Said of a good figure, because the substitution runs both ways.
    /// <para>
    /// The old wording answered a healthy snapshot with "Nema osnova za prigovor po brzini" -
    /// a conclusion about the service drawn from one sample, in the operator's favour. A
    /// connection that fails every evening measures perfectly at eleven in the morning.
    /// </para>
    /// </summary>
    public const string NotProofOfService =
        "To ne znači da je usluga uredna: jedno merenje opisuje jedan trenutak, a veza koja " +
        "pada svako veče izmeri se uredno pre podne.";

    /// <summary>
    /// The condition a single measurement cannot meet, said wherever a band is explained.
    /// <para>
    /// Without it a reader takes "80-90 % ugovorene" for the regulator's "uobičajeno dostupna
    /// brzina", which is that share <em>held over 90 % of the measurement time</em>. The
    /// difference is the difference between a figure and a finding.
    /// </para>
    /// </summary>
    public const string SeriesNote =
        "Zaključak o uobičajeno dostupnoj brzini po propisu traži da brzina bude najmanje 80 % " +
        "ugovorene u 90 % vremena merenja, što jedno merenje ne može da pokaže - za to je " +
        "potrebna serija merenja kroz vreme, a za sam postupak merenje RATEL NetTest aplikacijom.";

    /// <summary>The same band, said of the sending direction.</summary>
    public static string UploadLabel(this SpeedBand band) => $"{band.Label()} (slanje)";

    public static string Label(this LoadedLatencyGrade grade) => grade switch
    {
        LoadedLatencyGrade.Severe => "VELIKO",
        LoadedLatencyGrade.Noticeable => "primetno",
        _ => "neznatno",
    };

    /// <summary>
    /// What the increase means for the way the connection is actually used.
    /// <para>
    /// Written in terms of calls, video and games rather than milliseconds, because that is
    /// the complaint someone is trying to describe when they say the internet "radi, ali
    /// zapinje" - and it is the one thing a download figure alone can never show.
    /// </para>
    /// </summary>
    public static string Explain(this LoadedLatencyGrade grade) => grade switch
    {
        LoadedLatencyGrade.Severe =>
            "Dok je veza opterećena, odziv se povećava toliko da pozivi, video sastanci i igre " +
            "postaju neupotrebljivi - iako brzina može biti u redu. Uzrok je po pravilu prevelik " +
            "bafer na ruteru ili na pristupnoj opremi operatera. Ovo je merljiv osnov za prigovor " +
            "zbog kvaliteta usluge, odvojen od prigovora zbog brzine.",

        LoadedLatencyGrade.Noticeable =>
            "Dok je veza opterećena, odziv se primetno povećava: u pozivu se ljudi presecaju, " +
            "a u igri se javlja zastajkivanje. Nije toliko da veza postane neupotrebljiva, ali " +
            "je merljivo i vredi ga priložiti uz opis smetnje.",

        _ =>
            "Odziv se pod opterećenjem gotovo ne menja. Preuzimanje ili slanje u pozadini ne " +
            "kvari pozive ni igre.",
    };

    /// <summary>
    /// What the latency-under-load figure is and is not, in one paragraph the report can carry.
    /// </summary>
    public const string LoadedLatencyNote =
        "Kašnjenje pod opterećenjem meri se kao razlika između odziva dok je veza mirna i " +
        "odziva dok je namerno opterećena u istom smeru. Meri se HTTP odzivom ka istoj meti " +
        "u oba slučaja, pa se ono što meta sama dodaje poništava u razlici. Ovo je merenje " +
        "kvaliteta veze, ne brzine, i ne podleže pravilu o Ethernet kablu - ali preko Wi-Fi " +
        "veze meri i sopstveni bežični link, pa i ovde kabl daje čistiji nalaz.";

    /// <summary>
    /// The one sentence that has to appear beside every speed figure this tool produces.
    /// <para>
    /// The official procedure uses RATEL's own measurement, and a figure from anywhere else
    /// - including this application - is supporting material rather than proof. Saying so
    /// plainly is what keeps the rest of the report credible.
    /// </para>
    /// </summary>
    public const string OfficialMeasurementNote =
        "Za zvaničan postupak koristi se RATEL NetTest aplikacija. Merenje iz ovog programa " +
        "je pomoćni dokaz koji pokazuje kada je i pod kojim uslovima brzina padala, ali ne " +
        "zamenjuje zvanično merenje.";

    /// <summary>
    /// Said in the report because the measurement leaves a mark on the evidence beside it.
    /// <para>
    /// The transfer deliberately fills the line, so the latency chart shows a spike at that
    /// moment. Explaining it in advance is the difference between a document that anticipates
    /// the obvious question and one that has to answer it afterwards.
    /// </para>
    /// </summary>
    public const string SaturationNote =
        "Merenje namerno zauzima vezu do kraja, u oba smera. Skok kašnjenja na vremenskoj traci " +
        "u vreme merenja potiče od samog merenja, a ne od smetnje na vezi.";

    /// <summary>
    /// Said of a measurement recorded before the rules were corrected.
    /// <para>
    /// The earlier build wrote its own verdict into the file. It is kept, because throwing it
    /// away would lose part of the record, and it is not adopted, because it was reached under
    /// a rule that counted an unchecked measurement path as a verified one.
    /// </para>
    /// </summary>
    public const string LegacyFindingNote =
        "Ocena je sačuvana iz starije verzije programa. Mrežna putanja merenja tada nije bila " +
        "proverena, pa se ranija ocena valjanosti ne preuzima kao današnji nalaz. Izmerene " +
        "vrednosti su onakve kakve su zapisane; pojas je preračunat po važećim pravilima.";

    /// <summary>What a recorded measurement supports today, in one line.</summary>
    public static string Label(this SpeedAssessmentState state) => state switch
    {
        SpeedAssessmentState.MeetsConditions => "Merenje ispunjava uslove za korišćenje uz prigovor.",
        SpeedAssessmentState.DoesNotMeetConditions => "Merenje NE ispunjava uslove za dokazivanje ugovorene brzine:",
        _ => "Valjanost ovog merenja se po važećim pravilima ne može utvrditi.",
    };

    /// <summary>Conditions the measurement itself cannot check, so the person has to.</summary>
    public static IReadOnlyList<string> Checklist { get; } =
    [
        "Računar je povezan mrežnim kablom direktno na ruter, bez svičeva i produžetaka.",
        "Ostali uređaji na toj vezi su isključeni ili odvojeni od mreže.",
        "Na računaru ne rade preuzimanja, sinhronizacije, video pozivi ni striming.",
        "VPN je isključen.",
        "Wi-Fi na računaru je isključen, da saobraćaj ne bi otišao tim putem.",
    ];
}
