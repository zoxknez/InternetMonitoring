namespace IEM.Legal;

/// <summary>
/// One provision, with the dates between which it applies and when somebody last checked.
/// <para>
/// Carried with every period this program states. A deadline printed without its source is a
/// number the reader has to take on trust, and the numbers in this area move: the old ZEK's
/// article 113 was left standing only until the act under article 140 of the new one was
/// adopted, which happened, so a case opened today runs on entirely different periods from
/// one opened in 2024.
/// </para>
/// <para>
/// <see cref="EffectiveTo"/> matters as much as <see cref="EffectiveFrom"/>. A case from July
/// 2026 and one from August 2026 both give the consumer eight days for the operator's answer,
/// but under different laws - and a report that cites the wrong one has quietly changed what
/// an old case meant.
/// </para>
/// </summary>
public sealed record LegalCitation
{
    public required string SourceId { get; init; }

    public required string Title { get; init; }

    /// <summary>Where it was published, as it is cited in a submission.</summary>
    public required string Gazette { get; init; }

    public string? Article { get; init; }

    public string? Paragraph { get; init; }

    /// <summary>
    /// The day it starts to apply - which is not always the day it enters into force. The
    /// quality pravilnik 82/2024 entered into force on 19 October 2024 and applies three
    /// months later, and it is the later date that decides which rules a measurement is
    /// judged by.
    /// </summary>
    public DateOnly? AppliesFrom { get; init; }

    /// <summary>The last day it applies, where it has been superseded.</summary>
    public DateOnly? AppliesTo { get; init; }

    public required string Url { get; init; }

    /// <summary>When a person last read the source and confirmed this entry against it.</summary>
    public required DateOnly VerifiedAt { get; init; }

    public bool AppliesOn(DateOnly date) =>
        (AppliesFrom is not { } from || date >= from) &&
        (AppliesTo is not { } to || date <= to);

    /// <summary>The citation as it goes into a letter.</summary>
    public override string ToString()
    {
        var text = $"{Title} (\"{Gazette}\")";

        if (Article is not null)
        {
            text += $", čl. {Article}";
        }

        if (Paragraph is not null)
        {
            text += $" st. {Paragraph}";
        }

        return text;
    }
}

/// <summary>
/// Every provision this program relies on, current and superseded alike.
/// <para>
/// Superseded sources are kept rather than deleted. A case from 2024 was governed by them,
/// and a program that quietly re-cites today's law over yesterday's case has changed what
/// that case meant - which is the one thing a record of a dispute must never do.
/// </para>
/// <para>
/// Every entry carries the day it was checked. These are facts about the world, not
/// constants: when the law moves, a new entry is added with its own dates and the old one
/// stays exactly as it was.
/// </para>
/// </summary>
public static class LegalSources
{
    private static readonly DateOnly Checked = new(2026, 8, 17);

    /// <summary>The period regime that ended when the act under article 140 was adopted.</summary>
    public static readonly LegalCitation ZekLegacy113 = new()
    {
        SourceId = "RS-ZEK-LEGACY-113",
        Title = "Zakon o elektronskim komunikacijama (raniji)",
        Gazette = "Sl. glasnik RS 44/2010, 60/2013-US, 62/2014, 95/2018",
        Article = "113",
        Paragraph = "6",
        AppliesTo = new DateOnly(2024, 12, 31),
        Url = "https://www.paragraf.rs/propisi/zakon_o_elektronskim_komunikacijama.html",
        VerifiedAt = Checked,
    };

    /// <summary>Complaint to the operator, the operator's answer, and the way to the Regulator.</summary>
    public static readonly LegalCitation Zek139 = new()
    {
        SourceId = "RS-ZEK-35-2023-139",
        Title = "Zakon o elektronskim komunikacijama",
        Gazette = "Sl. glasnik RS 35/2023",
        Article = "139",
        Url = "https://www.paragraf.rs/propisi/zakon-o-elektronskim-komunikacijama.html",
        VerifiedAt = Checked,
    };

    /// <summary>The out-of-court settlement procedure itself.</summary>
    public static readonly LegalCitation Zek140 = new()
    {
        SourceId = "RS-ZEK-35-2023-140",
        Title = "Zakon o elektronskim komunikacijama",
        Gazette = "Sl. glasnik RS 35/2023",
        Article = "140",
        Url = "https://www.paragraf.rs/propisi/zakon-o-elektronskim-komunikacijama.html",
        VerifiedAt = Checked,
    };

    /// <summary>
    /// The act adopted under article 140, which is what ended the transitional regime.
    /// Applies from 1 January 2025; proceedings started before that finish under the old
    /// rules.
    /// </summary>
    public static readonly LegalCitation Pravilnik58_2024 = new()
    {
        SourceId = "RS-PRAVILNIK-58-2024",
        Title = "Pravilnik o vansudskom rešavanju sporova između krajnjih korisnika i operatora",
        Gazette = "Sl. glasnik RS 58/2024",
        AppliesFrom = new DateOnly(2025, 1, 1),
        Url = "https://www.ratel.rs/",
        VerifiedAt = Checked,
    };

    /// <summary>The consumer's answer period, for complaints made up to 1 August 2026.</summary>
    public static readonly LegalCitation Zzp88_2021 = new()
    {
        SourceId = "RS-ZZP-88-2021",
        Title = "Zakon o zaštiti potrošača",
        Gazette = "Sl. glasnik RS 88/2021",
        AppliesTo = new DateOnly(2026, 8, 1),
        Url = "https://www.paragraf.rs/propisi/zakon_o_zastiti_potrosaca.html",
        VerifiedAt = Checked,
    };

    /// <summary>The same period under the law that replaced it, from 2 August 2026.</summary>
    public static readonly LegalCitation Zzp35_2026 = new()
    {
        SourceId = "RS-ZZP-35-2026",
        Title = "Zakon o zaštiti potrošača",
        Gazette = "Sl. glasnik RS 35/2026",
        AppliesFrom = new DateOnly(2026, 8, 2),
        Url = "https://www.paragraf.rs/propisi/zakon_o_zastiti_potrosaca.html",
        VerifiedAt = Checked,
    };

    /// <summary>Quality parameters, superseded. Kept for measurements taken while it applied.</summary>
    public static readonly LegalCitation QualityPravilnik23_2023 = new()
    {
        SourceId = "RS-PRAVILNIK-KVALITET-23-2023",
        Title = "Pravilnik o parametrima kvaliteta javno dostupnih elektronskih komunikacionih usluga",
        Gazette = "Sl. glasnik RS 23/2023",
        AppliesTo = new DateOnly(2025, 1, 18),
        Url = "https://www.ratel.rs/",
        VerifiedAt = Checked,
    };

    /// <summary>
    /// Quality parameters, current. In force from 19 October 2024 and applied three months
    /// later - the applied date, not the in-force date, is what a measurement is judged by.
    /// </summary>
    public static readonly LegalCitation QualityPravilnik82_2024 = new()
    {
        SourceId = "RS-PRAVILNIK-KVALITET-82-2024",
        Title = "Pravilnik o parametrima kvaliteta javno dostupnih elektronskih komunikacionih usluga",
        Gazette = "Sl. glasnik RS 82/2024",
        AppliesFrom = new DateOnly(2025, 1, 19),
        Url = "https://www.ratel.rs/",
        VerifiedAt = Checked,
    };

    public static IReadOnlyList<LegalCitation> All { get; } =
    [
        ZekLegacy113, Zek139, Zek140, Pravilnik58_2024,
        Zzp88_2021, Zzp35_2026,
        QualityPravilnik23_2023, QualityPravilnik82_2024,
    ];

    /// <summary>
    /// Which consumer protection act governs an answer owed on this date.
    /// <para>
    /// Both prescribe eight days. The number matching is not enough: a report on a case from
    /// July 2026 has to cite the law that was in force in July 2026, or it has changed what
    /// that case meant.
    /// </para>
    /// </summary>
    public static LegalCitation ConsumerActOn(DateOnly date) =>
        Zzp35_2026.AppliesOn(date) ? Zzp35_2026 : Zzp88_2021;

    /// <summary>Which quality pravilnik a measurement taken on this date is judged by.</summary>
    public static LegalCitation QualityPravilnikOn(DateOnly date) =>
        QualityPravilnik82_2024.AppliesOn(date) ? QualityPravilnik82_2024 : QualityPravilnik23_2023;
}
