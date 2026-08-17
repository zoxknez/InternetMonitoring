namespace IEM.Core.Presentation;

/// <summary>
/// What the hash chain proves, said the same way everywhere it is said.
/// <para>
/// The chain links each entry to the one before it, so altering an early record breaks every
/// hash after it. That is a real and useful property, and it is a property of the file's
/// <em>internal consistency</em> - nothing more. The verifier reads the same folder the
/// records live in, and <c>SHA256SUMS.txt</c> sits in that folder too: whoever can rewrite a
/// record can recompute the chain and the checksums along with it.
/// </para>
/// <para>
/// Up to 2.6 the report, the console and the README all said "dokazano je da paket nije
/// menjan nakon snimanja". Against a careless edit that is true; against anyone who wanted to
/// forge the file it is not, and it is the second reading that matters in a dispute. An
/// operator's technician who noticed the gap would be entitled to discount the whole
/// document, so the claim is now stated as what it is - and the caveat travels with it rather
/// than sitting a page away.
/// </para>
/// <para>
/// Independent proof of time and origin needs a signature and a third-party timestamp. This
/// release does not do that; saying so plainly is what keeps the rest credible.
/// </para>
/// </summary>
public static class ChainText
{
    /// <summary>The finding, when every hash checks out.</summary>
    public const string Consistent =
        "Lanac otisaka je unutrašnje dosledan: nijedan zapis nije izmenjen a da to ne pokvari " +
        "sve otiske posle njega.";

    /// <summary>The finding, when one does not.</summary>
    public const string Broken =
        "Lanac otisaka je narušen: neki zapis je izmenjen, obrisan ili premešten posle snimanja.";

    /// <summary>
    /// The sentence that has to stand beside the finding above, every time it is stated.
    /// </summary>
    public const string NotProofOfOrigin =
        "Ovo je provera doslednosti, a ne dokaz porekla. Ko ima pravo upisa u folder sesije " +
        "mogao bi da preračuna i lanac i kontrolne zbirove. Nezavisan dokaz vremena i porekla " +
        "zahteva potpis i vremenski žig treće strane, što ovo izdanje ne radi.";
}
