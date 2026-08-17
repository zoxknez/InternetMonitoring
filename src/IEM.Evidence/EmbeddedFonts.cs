using System.Reflection;
using PdfSharp.Fonts;

namespace IEM.Evidence;

/// <summary>
/// Serves the report's typefaces out of this assembly, and never off the machine.
/// <para>
/// PDFsharp asks a resolver for font bytes and embeds what it is given. Left to its own
/// devices under Windows it would take whatever Segoe UI the machine has, which quietly
/// breaks two things this document claims. The same build over the same evidence would
/// produce a different file on a different Windows version, and the checksum beside it
/// would say so. And a machine missing the face - a stripped server image, a different
/// locale build - would fall back to something that has no <c>č ć ž š đ</c>, printing
/// boxes through the middle of a document somebody is sending to their operator.
/// </para>
/// <para>
/// Liberation ships inside the assembly instead: licensed for redistribution and embedding
/// (SIL OFL 1.1), and metrically compatible with Arial, so the page reads as an ordinary
/// business document.
/// </para>
/// </summary>
public static class EmbeddedFonts
{
    /// <summary>Body and headings.</summary>
    public const string Sans = "Liberation Sans";

    /// <summary>Hashes and anything else meant to be compared character by character.</summary>
    public const string Mono = "Liberation Mono";

    private const string SansRegular = "LiberationSans-Regular";
    private const string SansBold = "LiberationSans-Bold";
    private const string MonoRegular = "LiberationMono-Regular";

    private static readonly Lock Gate = new();
    private static bool _installed;

    /// <summary>
    /// Points PDFsharp at this resolver. Safe to call repeatedly and from several threads;
    /// the underlying setting is process-wide and PDFsharp refuses to have it changed once
    /// fonts exist, so it is set exactly once.
    /// </summary>
    public static void Install()
    {
        // Checked outside the lock as well, because every page of every report calls this
        // and after the first one there is nothing left to do.
        if (_installed)
        {
            return;
        }

        lock (Gate)
        {
            if (_installed)
            {
                return;
            }

            GlobalFontSettings.FontResolver = new Resolver();
            _installed = true;
        }
    }

    /// <summary>The raw bytes of one face, by the internal name the resolver hands out.</summary>
    internal static byte[] Load(string faceName)
    {
        var assembly = typeof(EmbeddedFonts).Assembly;
        var resource = $"IEM.Evidence.Fonts.{faceName}.ttf";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"Ugrađeni font '{resource}' nedostaje u programu. Izveštaj u PDF formatu " +
                "ne može biti napravljen ovom kopijom programa.");

        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed class Resolver : IFontResolver
    {
        public byte[]? GetFont(string faceName) => Load(faceName);

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            // Only three faces are carried, so italic is drawn by slanting the upright one.
            // Shipping four more files to render text that appears nowhere in the report
            // would add a megabyte to every copy of the program for nothing.
            if (string.Equals(familyName, Mono, StringComparison.OrdinalIgnoreCase))
            {
                return new FontResolverInfo(MonoRegular, isBold, isItalic);
            }

            return isBold
                ? new FontResolverInfo(SansBold, false, isItalic)
                : new FontResolverInfo(SansRegular, false, isItalic);
        }
    }
}
