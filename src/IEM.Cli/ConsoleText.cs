using IEM.Core.Presentation;

namespace IEM.Cli;

/// <summary>
/// Wrapping for the paragraphs the console prints, at the indent every command uses.
/// <para>
/// The breaking itself lives in <see cref="TextWrap"/>, so the console and the text files
/// written beside a session flow their paragraphs the same way.
/// </para>
/// </summary>
internal static class ConsoleText
{
    public static IEnumerable<string> Wrap(string text, int width = TextWrap.DefaultWidth) =>
        TextWrap.Lines(text, width);

    /// <summary>Writes a paragraph at the two-space indent every command uses.</summary>
    public static void WriteWrapped(string text, int width = TextWrap.DefaultWidth)
    {
        foreach (var line in TextWrap.Lines(text, width))
        {
            Console.WriteLine($"  {line}");
        }
    }
}
