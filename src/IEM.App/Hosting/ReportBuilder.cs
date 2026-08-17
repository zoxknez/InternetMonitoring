using System.IO;
using IEM.Evidence;
using IEM.Storage;

namespace IEM.App.Hosting;

/// <param name="Path">The report to open, when there is one.</param>
/// <param name="Refusal">Why nothing was produced, when nothing was.</param>
public sealed record ReportOutcome(string? Path, string? Refusal)
{
    public bool Ready => Path is not null;
}

/// <summary>
/// Produces the report for the window's "Izveštaj" button.
/// <para>
/// The button used to fall back to opening the folder whenever no report existed, which is
/// most of a running test: the report is written when a session closes. So somebody two days
/// into a forty-eight hour run, wanting to see what they had, got a file listing instead.
/// The console has always been able to build one mid-session, and now that a running
/// session's totals are read from its checkpoints rather than from column defaults, the
/// document that comes out is worth looking at.
/// </para>
/// <para>
/// The same <see cref="EvidencePackage"/> the console and the service use, so a report built
/// from this button cannot differ from one built any other way.
/// </para>
/// </summary>
public static class ReportBuilder
{
    private static readonly string[] Reports = ["Izvestaj.pdf", "Izvestaj.html"];

    /// <summary>Returns the existing report, or builds one and returns that.</summary>
    public static ReportOutcome Prepare(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (Existing(directory) is { } ready)
        {
            return new ReportOutcome(ready, null);
        }

        var paths = new SessionPaths(directory);

        if (!File.Exists(paths.RawLog))
        {
            return new ReportOutcome(null, "U ovom folderu nema sirove evidencije.");
        }

        try
        {
            // No archive: zipping a session that is still being written costs seconds for a
            // file that will be stale before the test ends. The one at the close is the one
            // that gets sent.
            EvidencePackage.Build(paths, createZip: false);
        }
        catch (EvidenceExportRefusedException ex)
        {
            // A refusal, not a failure. It carries its own explanation and that explanation
            // is the point - the export declined to produce a document it could not stand
            // behind, and softening it here would undo that.
            return new ReportOutcome(null, ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            // The likely one. A session recorded by the service belongs to the service
            // account, and the person whose connection was measured cannot write into it -
            // which looks like a broken button unless it says otherwise.
            return new ReportOutcome(
                null,
                "Nema prava upisa u folder sesije, pa izveštaj ne može biti napravljen odavde. " +
                "Sesiju je snimio servis, pa fajlovi pripadaju servisnom nalogu. Izveštaj se " +
                "pravi sam kada se test završi, ili ga možete napraviti komandom: iem --izvestaj");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            return new ReportOutcome(null, $"Izveštaj nije napravljen: {ex.Message}");
        }

        return Existing(directory) is { } built
            ? new ReportOutcome(built, null)
            : new ReportOutcome(null, "Izveštaj nije napravljen.");
    }

    /// <summary>
    /// The PDF first: it is the copy that gets printed, attached to a complaint and filed,
    /// and somebody who never opens the evidence folder would otherwise not learn it exists.
    /// </summary>
    private static string? Existing(string directory) => Reports
        .Select(name => System.IO.Path.Combine(directory, name))
        .FirstOrDefault(File.Exists);
}
