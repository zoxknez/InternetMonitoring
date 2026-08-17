using IEM.Evidence;
using IEM.Storage;

namespace IEM.Cli;

/// <summary>
/// Produces the readable package from a recorded session.
/// <para>
/// Runs automatically when a session ends, and can be re-run by hand afterwards. Being
/// re-runnable matters: a session interrupted by a power cut still has its evidence on
/// disk, and the report for it should not require repeating a two-day test.
/// </para>
/// </summary>
public static class ReportCommand
{
    /// <summary>Builds the package for a finished session and prints where it landed.</summary>
    public static bool BuildAndPrint(SessionPaths paths, bool createZip = true)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            var result = EvidencePackage.Build(paths, createZip);

            Console.WriteLine("  ─────────────────────────────────────────────");
            Console.WriteLine("  IZVEŠTAJ");
            Console.WriteLine();
            Console.WriteLine($"  Za čitanje:  {Path.Combine(paths.Directory, "Izvestaj.html")}");

            if (result.PdfFailure is null)
            {
                Console.WriteLine($"  Za štampu:   {Path.Combine(paths.Directory, "Izvestaj.pdf")}");
            }

            if (result.ZipPath is not null)
            {
                Console.WriteLine($"  Arhiva:      {result.ZipPath}");
            }

            if (result.PdfFailure is not null)
            {
                Console.WriteLine();
                WriteError($"  Izvestaj.pdf nije napravljen: {result.PdfFailure}");
                Console.WriteLine("  Izvestaj.html sadrži isti izveštaj; paket je inače potpun.");
            }

            Console.WriteLine();
            Console.WriteLine("  Izvestaj.pdf je verzija za štampu i slanje uz prigovor.");
            Console.WriteLine("  Arhivu sa svim dokazima možete poslati operateru.");
            Console.WriteLine();
            return true;
        }
        catch (EvidenceExportRefusedException ex)
        {
            // Not a failure to render - a refusal to produce a document that would be
            // forwarded to an operator and treated as a claim.
            Console.WriteLine();
            WriteError($"  {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("  Izveštaj se ne pravi. Sirova evidencija je jedini dokaz, i ona");
            Console.WriteLine("  mora biti neizmenjena da bi izveštaj imao ikakvu težinu.");
            Console.WriteLine();
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Its own case because the remedy is different from every other failure here.
            // A session recorded by the service belongs to the service account, and the
            // person whose connection was measured cannot overwrite it - which looks like a
            // broken program rather than a permissions problem unless it says so.
            Console.WriteLine();
            WriteError("  Nema prava upisa u folder sesije.");
            Console.WriteLine();
            Console.WriteLine("  Sesiju je snimio servis, pa fajlovi pripadaju servisnom nalogu.");
            Console.WriteLine("  Rešenja:");
            Console.WriteLine("    • ponovo pokrenite instalaciju, koja dodeljuje prava korisnicima, ili");
            Console.WriteLine("    • kopirajte folder sesije negde gde imate prava pa pokrenite izveštaj tamo.");
            Console.WriteLine();
            Console.WriteLine($"  Tehnički detalj: {ex.Message}");
            Console.WriteLine();
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or IOException)
        {
            // A failed report must not discard a completed session. The raw evidence is
            // already on disk and intact; only the presentation layer failed.
            Console.WriteLine();
            WriteError($"  Izveštaj nije napravljen: {ex.Message}");
            Console.WriteLine($"  Sirova evidencija je sačuvana u: {paths.Directory}");
            Console.WriteLine("  Izveštaj možete napraviti kasnije komandom --izvestaj.");
            Console.WriteLine();
            return false;
        }
    }

    /// <summary>Rebuilds the package for a session directory named on the command line.</summary>
    public static bool Run(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var paths = new SessionPaths(directory);

        Console.WriteLine();
        Console.WriteLine("  IZRADA IZVEŠTAJA");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine($"  Folder:  {directory}");
        Console.WriteLine();

        // The raw chain, not the database: the database is a derived index, and a folder
        // with only an index in it has no evidence to report on.
        if (!File.Exists(paths.RawLog))
        {
            WriteError($"  U ovom folderu nema sirove evidencije (nedostaje {Path.GetFileName(paths.RawLog)}).");
            Console.WriteLine("  Da li ste uneli folder jedne sesije (Sesija_...), a ne nadređeni folder?");
            Console.WriteLine();
            return false;
        }

        return BuildAndPrint(paths);
    }

    private static void WriteError(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
