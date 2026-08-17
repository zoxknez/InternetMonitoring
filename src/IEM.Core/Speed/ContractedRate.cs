using System.Globalization;

namespace IEM.Core.Speed;

/// <summary>
/// The contracted rate as a person has it written down.
/// <para>
/// Operators advertise and contracts state a pair - "100/20" - so that is what the field
/// accepts, along with a bare download figure for someone who only knows that half. Parsed
/// here rather than in the window so the console, the window and the service read the same
/// string the same way, and so the rule can be tested without a window.
/// </para>
/// <para>
/// Both the decimal comma and the decimal point are accepted. A field that refuses "10,5"
/// on a Serbian keyboard would be refusing the way the number is written here.
/// </para>
/// </summary>
public static class ContractedRate
{
    /// <summary>
    /// Reads "100", "100/20", "100 / 20" or "10,5/2" into the two figures.
    /// <para>
    /// An empty entry is not a mistake: it means no contract was stated, which the
    /// measurement records honestly as having nothing to compare against. Anything else that
    /// does not parse is refused rather than half-read - a typo silently understood as some
    /// other number is how a complaint ends up quoting a rate nobody ever contracted.
    /// </para>
    /// </summary>
    /// <returns>False when the text was given but is not a rate.</returns>
    public static bool TryParse(string? text, out double? downloadMbps, out double? uploadMbps)
    {
        downloadMbps = null;
        uploadMbps = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        // Empty halves are kept rather than dropped, so "100/" is refused as the half-typed
        // pair it is instead of quietly passing as a lone download figure.
        var parts = text.Split('/', StringSplitOptions.TrimEntries);

        if (parts.Length is 0 or > 2)
        {
            return false;
        }

        if (!TryParseRate(parts[0], out var download))
        {
            return false;
        }

        if (parts.Length == 2)
        {
            if (!TryParseRate(parts[1], out var upload))
            {
                return false;
            }

            uploadMbps = upload;
        }

        downloadMbps = download;
        return true;
    }

    /// <summary>What the parsed pair looks like written back out, for a status line.</summary>
    public static string Describe(double? downloadMbps, double? uploadMbps) => (downloadMbps, uploadMbps) switch
    {
        ({ } down, { } up) =>
            $"{down.ToString("0.##", Presentation.SerbianText.Culture)}/{up.ToString("0.##", Presentation.SerbianText.Culture)} Mbit/s",
        ({ } down, null) => $"{down.ToString("0.##", Presentation.SerbianText.Culture)} Mbit/s",
        _ => "nije uneto",
    };

    private static bool TryParseRate(string text, out double mbps)
    {
        // Invariant first, then the Serbian reading, so "10.5" and "10,5" both arrive as the
        // same number rather than one of them becoming a hundred and five.
        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out mbps) ||
                     double.TryParse(text, NumberStyles.Float, Presentation.SerbianText.Culture, out mbps);

        return parsed && mbps > 0 && double.IsFinite(mbps);
    }
}
