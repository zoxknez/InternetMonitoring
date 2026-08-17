namespace IEM.Core.Presentation;

/// <summary>
/// Who made this, where it lives, and where to send what you find.
/// <para>
/// In one place because it is said in four: the window's "O programu", the console's help,
/// the repository's README and whatever the next surface turns out to be. A Discord invite
/// that has expired in one of them and not the others is worse than not offering one.
/// </para>
/// <para>
/// Deliberately absent from the report. That document goes to an operator and possibly to
/// RATEL, and a personal link in the middle of it would make evidence look like advertising.
/// The report says which build produced it and nothing else about its author.
/// </para>
/// </summary>
public static class AppInfo
{
    /// <summary>One sentence, the same one the README opens with.</summary>
    public const string Summary =
        "Beleži prekide i kvalitet internet veze i pravi dokumentaciju upotrebljivu za prigovor " +
        "operateru.";

    /// <summary>
    /// The promise that decides most of the design, said out loud wherever the program
    /// introduces itself: nothing leaves the machine unless the person sends it.
    /// </summary>
    public const string PrivacyLine =
        "Sve radi lokalno. Nema naloga, nema servera, ne šalje podatke nigde. Snimljeno ostaje " +
        "u folderu sesije, a vi odlučujete kome ćete ga poslati.";

    public const string Author = "o0o0o0o";

    public const string LicenseName = "MIT";

    /// <summary>Where the source is, and where an issue is filed.</summary>
    public const string ProjectUrl = "https://github.com/zoxknez/InternetMonitoring";

    public const string IssuesUrl = "https://github.com/zoxknez/InternetMonitoring/issues";

    public const string PortfolioUrl = "https://mojportfolio.vercel.app";

    public const string DiscordUrl = "https://discord.gg/ZZbtCs942";

    public const string Email = "zoxknez@hotmail.com";

    /// <summary>Where a bug or an idea should go, and why there is more than one address.</summary>
    public const string FeedbackLine =
        "Greške i predloge pošaljite na jedno od tri mesta: GitHub (najbolje, jer ostaje " +
        "zapisano i vidi se šta je urađeno), Discord (za pitanja i razgovor) ili mejl.";

    /// <summary>
    /// What to attach, and what not to. Said here because the evidence folder contains the
    /// names of somebody's networks and the addresses of their equipment - and a person
    /// reporting a bug should not have to work out on their own that this is worth thinking
    /// about before uploading a session.
    /// </summary>
    public const string ReportingCaution =
        "Evidencija sesije sadrži imena vaših mreža i adrese vaše opreme. Šaljite samo ono što " +
        "ste spremni da objavite.";

    public static string VersionLine => $"{BuildInfo.Product} {BuildInfo.Version}";

    /// <summary>The links as a list, in the order they are worth offering.</summary>
    public static IReadOnlyList<(string Label, string Target)> Links { get; } =
    [
        ("Izvorni kod i prijava grešaka", ProjectUrl),
        ("Discord", DiscordUrl),
        ("Portfolio autora", PortfolioUrl),
        ("Mejl", $"mailto:{Email}"),
    ];
}
