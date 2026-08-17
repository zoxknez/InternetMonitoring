using System.Text;

namespace IEM.Core.Presentation;

/// <summary>
/// Breaks a paragraph into lines of at most a given width.
/// <para>
/// One implementation for the whole product. There were three identical private copies in the
/// console commands alone, and the plain-text artefacts written beside a session wrapped their
/// paragraphs by hand - so the same sentence came out at a different width depending on which
/// file it landed in, and editing it meant re-flowing the literal by eye.
/// </para>
/// </summary>
public static class TextWrap
{
    /// <summary>Comfortable for a default console window and for the text files beside a session.</summary>
    public const int DefaultWidth = 70;

    public static IEnumerable<string> Lines(string text, int width = DefaultWidth)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

        var line = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
