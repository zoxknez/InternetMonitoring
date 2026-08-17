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
            "Saobraćaj je izlazio kroz više mrežnih adaptera ili kroz VPN, pa se ne zna šta je " +
            "tačno mereno. Isključite VPN i ostale adaptere, pa ponovite.",

        SpeedMeasurementDefect.ContractUnknown =>
            "Ugovorena brzina nije uneta, pa nema sa čim da se uporedi izmereno.",

        _ => string.Empty,
    };

    public static string Label(this SpeedBand band) => band switch
    {
        SpeedBand.BelowMinimum => "ISPOD MINIMALNE",
        SpeedBand.AboveMinimum => "iznad minimalne, ispod uobičajene",
        SpeedBand.NormallyAvailable => "uobičajeno dostupna",
        _ => "na nivou oglašene",
    };

    /// <summary>What the band means for a complaint.</summary>
    public static string Explain(this SpeedBand band) => band switch
    {
        SpeedBand.BelowMinimum =>
            "Izmerena brzina je ispod minimalne koju ugovor predviđa. Ovo je osnov za prigovor, " +
            "pod uslovom da je merenje rađeno preko kabla i uz mirnu vezu.",

        SpeedBand.AboveMinimum =>
            "Brzina je iznad ugovorenog minimuma, ali ispod uobičajeno dostupne. Jedno ovakvo " +
            "merenje nije osnov za prigovor; ponovljena merenja tokom više dana jesu argument.",

        SpeedBand.NormallyAvailable =>
            "Brzina odgovara uobičajeno dostupnoj. Nema osnova za prigovor po brzini.",

        _ =>
            "Brzina odgovara oglašenoj. Nema osnova za prigovor po brzini.",
    };

    /// <summary>Short label for the band, said of the sending direction.</summary>
    public static string UploadLabel(this SpeedBand band) => band switch
    {
        SpeedBand.BelowMinimum => "ISPOD MINIMALNE (slanje)",
        SpeedBand.AboveMinimum => "iznad minimalne, ispod uobičajene (slanje)",
        SpeedBand.NormallyAvailable => "uobičajeno dostupna (slanje)",
        _ => "na nivou oglašene (slanje)",
    };

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
