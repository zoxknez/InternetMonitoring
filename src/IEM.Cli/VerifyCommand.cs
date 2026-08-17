using IEM.Core.Presentation;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Cli;

/// <summary>
/// Re-checks a recorded session.
/// <para>
/// Anyone can run this against a package they were sent - the customer, an operator's
/// technician, a third party. Evidence that only its author can validate is not evidence
/// at all, so verification is a first-class command rather than an internal detail.
/// </para>
/// </summary>
public static class VerifyCommand
{
    public static bool Run(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var paths = new SessionPaths(directory);

        Console.WriteLine();
        Console.WriteLine("  PROVERA INTEGRITETA SESIJE");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine($"  Folder:  {directory}");

        if (!File.Exists(paths.RawLog))
        {
            WriteLine(ConsoleColor.Red, $"  Nije pronađen fajl {Path.GetFileName(paths.RawLog)}.");
            Console.WriteLine("  Da li ste uneli folder jedne sesije (Sesija_...), a ne nadređeni folder?");
            Console.WriteLine();
            return false;
        }

        var verification = ChainVerifier.Verify(paths.RawLog);

        Console.WriteLine($"  Zapisa:  {verification.EntriesChecked}");
        Console.WriteLine();

        if (verification.Valid)
        {
            WriteLine(ConsoleColor.Green, "  ISPRAVAN - lanac otisaka je neprekinut.");
            Console.WriteLine();
            Console.WriteLine($"  Završni otisak: {verification.HeadHash}");
            Console.WriteLine();
            ConsoleText.WriteWrapped(ChainText.Consistent);
            Console.WriteLine();
            ConsoleText.WriteWrapped(ChainText.NotProofOfOrigin);
            Console.WriteLine();
            return true;
        }

        WriteLine(ConsoleColor.Red, "  NARUŠEN - neki zapis je izmenjen posle snimanja.");
        Console.WriteLine();
        Console.WriteLine($"  Prvi narušen red: {verification.FirstBrokenLine}");
        Console.WriteLine($"  Razlog:           {TranslateReason(verification.Reason)}");
        Console.WriteLine();
        Console.WriteLine($"  Zapisi do reda {verification.FirstBrokenLine - 1} su ispravni i ostaju upotrebljivi.");
        Console.WriteLine();
        return false;
    }

    private static string TranslateReason(string? reason) => reason switch
    {
        "Entry contents do not match its hash" => "sadržaj zapisa ne odgovara njegovom otisku (izmena)",
        "Entry does not chain from the previous one" => "zapis se ne nadovezuje na prethodni (brisanje ili premeštanje)",
        "Line is not a valid chain entry" => "red nije ispravan zapis lanca",
        _ => reason ?? "nepoznat",
    };

    private static void WriteLine(ConsoleColor color, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
