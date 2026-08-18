using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using IEM.Core.Presentation;
using IEM.Core.Speed;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Evidence;

/// <param name="ZipPath">Null when packaging produced no archive.</param>
/// <param name="Rebuild">How the index reconstructed from the chain compared to the stored one.</param>
/// <param name="PdfFailure">
/// Why the printable report is missing from this package, or null when it was written.
/// Carried rather than thrown because the HTML report, the tables and the raw chain are the
/// evidence; losing the convenience copy must not lose the rest of them.
/// </param>
public sealed record PackageResult(
    string Directory,
    string? ZipPath,
    IReadOnlyList<string> Files,
    ChainVerification Verification,
    IndexRebuild Rebuild,
    string? PdfFailure = null);

/// <summary>
/// Raised when the evidence cannot be exported as it stands.
/// <para>
/// Deliberately fatal rather than a warning on the report. A document that says "some
/// records could not be verified" still gets forwarded to an operator and still gets
/// treated as a claim. Refusing outright is the only response that cannot be misread.
/// </para>
/// </summary>
public sealed class EvidenceExportRefusedException : Exception
{
    public EvidenceExportRefusedException()
    {
    }

    public EvidenceExportRefusedException(string message) : base(message)
    {
    }

    public EvidenceExportRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Turns a recorded session into something a person can actually send somewhere.
/// <para>
/// The package deliberately carries both the readable report and the raw material behind
/// it. A report on its own invites "where do these numbers come from"; the raw log on its
/// own is unreadable. Shipping both, with checksums over everything, means the recipient
/// can either take the summary at face value or check it line by line.
/// </para>
/// </summary>
public static class EvidencePackage
{
    private const string ReportHtml = "Izvestaj.html";
    private const string ReportPdf = "Izvestaj.pdf";
    private const string SummaryTxt = "Rezime.txt";
    private const string IncidentsCsv = "Prekidi.csv";
    private const string MeasurementsCsv = "Merenja.csv";
    private const string GapsCsv = "Pauze-nadzora.csv";
    private const string ChecksumFile = SessionPaths.ChecksumFileName;

    /// <summary>
    /// Builds every derived file in the session directory and checksums the result.
    /// Safe to run repeatedly; each run regenerates from the stored session.
    /// </summary>
    public static PackageResult Build(SessionPaths paths, bool createZip = true)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Rule one: no raw chain, no report. The chain is the evidence; the database is a
        // cache over it. Building a report from the cache alone let someone delete the
        // inconvenient half of a session and still get a document declaring the chain
        // unbroken - which is worse than no report at all.
        if (!File.Exists(paths.RawLog))
        {
            throw new EvidenceExportRefusedException(
                "Sirova evidencija ne postoji, pa izveštaj ne može biti napravljen. " +
                "Baza podataka je samo izvedeni indeks i sama po sebi nije dokaz.");
        }

        var verification = ChainVerifier.Verify(paths.RawLog);

        // Rule two: an export never repairs evidence. A hash that does not match part-way
        // through the chain means someone or something changed the file, and truncating to
        // the last good entry would quietly discard the part that proves it.
        if (!verification.Valid)
        {
            throw new EvidenceExportRefusedException(
                $"IZVOZ ODBIJEN. Narušen integritet lanca na zapisu {verification.FirstBrokenLine}. " +
                $"Razlog: {verification.Reason}");
        }

        // Rule three: the report is built from an index reconstructed out of the entries
        // just verified, not from whatever database happens to sit in the folder.
        var rebuild = EvidenceIndexRebuilder.RebuildForExport(paths);
        var written = new List<string>();
        string? zipPath;
        string? pdfFailure;

        try
        {
            using (var reader = SessionReader.Open(rebuild.DatabasePath))
            {
                var session = reader.Load()
                    ?? throw new EvidenceExportRefusedException(
                        "Sirova evidencija ne sadrži nijednu sesiju.");

                // A measurement taken beside the session, when one was. Absent for sessions
                // that never had one, and a file that cannot be parsed reads as absent rather
                // than taking the export down with it.
                var speed = SpeedMeasurementNote.Read(paths.Directory) ?? Adopt(paths, session);

                Write(ReportHtml, p => HtmlReportBuilder.Write(p, session, verification.HeadHash, verification.Valid, speed));

                pdfFailure = WritePdf(paths.Directory, session, verification, speed);

                if (pdfFailure is null)
                {
                    written.Add(ReportPdf);
                }

                Write(SummaryTxt, p => CsvExporter.WriteSummary(p, session));
                Write(IncidentsCsv, p => CsvExporter.WriteIncidents(p, session));
                Write(MeasurementsCsv, p => CsvExporter.WriteMeasurements(p, session));

                if (session.Gaps.Count > 0)
                {
                    Write(GapsCsv, p => CsvExporter.WriteGaps(p, session));
                }
            }

            WriteChecksums(paths.Directory);
            zipPath = createZip ? CreateZip(paths) : null;
        }
        finally
        {
            TryDelete(rebuild.DatabasePath);
        }

        return new PackageResult(paths.Directory, zipPath, written, verification, rebuild, pdfFailure);

        void Write(string name, Action<string> writer)
        {
            var full = Path.Combine(paths.Directory, name);
            writer(full);
            written.Add(name);
        }
    }

    /// <summary>
    /// Writes the printable report, and says why if it could not.
    /// <para>
    /// The realistic failure is not a bug: it is the previous PDF being open in a viewer
    /// that holds an exclusive handle while the report is rebuilt. Letting that throw would
    /// abandon a rebuild that had already produced a perfectly good HTML report, so the
    /// failure is reported instead.
    /// </para>
    /// <para>
    /// What is <em>not</em> tolerated is the previous run's PDF surviving into this package.
    /// A document with last week's figures, checksummed and zipped alongside this week's
    /// evidence, is worse than no document - so if the stale file cannot be removed either,
    /// the export refuses rather than shipping it.
    /// </para>
    /// </summary>
    private static string? WritePdf(string directory, SessionSnapshot session, ChainVerification verification, SpeedMeasurementNote? speed)
    {
        var path = Path.Combine(directory, ReportPdf);

        try
        {
            PdfReportBuilder.Write(path, session, verification.HeadHash, verification.Valid, speed);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                throw new EvidenceExportRefusedException(
                    $"IZVOZ ODBIJEN. {ReportPdf} iz prethodne izrade ne može biti ni prepisan ni " +
                    "obrisan, najverovatnije zato što je otvoren u drugom programu. Zatvorite ga " +
                    "pa pokrenite izradu ponovo - paket sa izveštajem iz ranije sesije bio bi " +
                    "netačan dokument.",
                    ex);
            }

            return ex.Message;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary index in the system temp folder harms nothing.
        }
    }

    /// <summary>
    /// Checksums every file in the package except the checksum file itself.
    /// <para>
    /// Written in the familiar <c>sha256sum</c> layout so it can be checked with ordinary
    /// tools rather than only with this application.
    /// </para>
    /// </summary>
    /// <summary>
    /// Takes up a measurement that was waiting in the output root, if one is.
    /// <para>
    /// A measurement run while no session was open is filed beside the sessions rather than
    /// inside one, because at that moment there is no session to file it into. It is taken up
    /// here, before the checksums are written, so it is hashed and archived with the rest.
    /// </para>
    /// <para>
    /// Moved rather than copied, so it is claimed exactly once. Copied, the same measurement
    /// would reappear in every session from now on, each report presenting it as its own.
    /// </para>
    /// <para>
    /// A measurement taken after this session ended belongs to no session yet and is left
    /// where it is. One taken before it began is taken up, and the report says so rather than
    /// letting the date sit there for the reader to catch.
    /// </para>
    /// </summary>
    private static SpeedMeasurementNote? Adopt(SessionPaths paths, SessionSnapshot session)
    {
        var root = Directory.GetParent(paths.Directory)?.FullName;

        if (root is null)
        {
            return null;
        }

        var waiting = SpeedMeasurementNote.Read(root);

        if (waiting is null || waiting.MeasuredAtUtc > (session.EndedUtc ?? DateTimeOffset.UtcNow))
        {
            return null;
        }

        try
        {
            File.Move(
                Path.Combine(root, SpeedMeasurementNote.FileName),
                Path.Combine(paths.Directory, SpeedMeasurementNote.FileName));

            return waiting;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The measurement stays where it is and the report goes out without it. Losing a
            // supporting figure is bad; losing the session's own evidence over it is worse.
            return null;
        }
    }

    private static void WriteChecksums(string directory)
    {
        var checksumPath = Path.Combine(directory, ChecksumFile);

        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(f, checksumPath, StringComparison.OrdinalIgnoreCase))
            // SQLite side files come and go with the connection; hashing them would produce
            // a checksum list that fails to verify five seconds later.
            .Where(f => !f.EndsWith("-wal", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var lines = new List<string>(files.Count);

        foreach (var file in files)
        {
            using var stream = OpenShared(file);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            lines.Add($"{hash}  {relative}");
        }

        File.WriteAllLines(checksumPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Opens a file for reading without excluding the writer.
    /// <para>
    /// Necessary because a report can legitimately be produced while the session is still
    /// running - the console offers exactly that - and the service holds the raw log open
    /// for writing the whole time. An ordinary read asks for a share mode that excludes
    /// writers, so packaging a live session failed part-way through, after the CSVs had
    /// already been rewritten.
    /// </para>
    /// <para>
    /// The hash then covers the file as it stood at that moment, which is what a checksum
    /// of a growing log can honestly mean. The chain itself is what proves nothing earlier
    /// was altered.
    /// </para>
    /// </summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static string CreateZip(SessionPaths paths)
    {
        var name = new DirectoryInfo(paths.Directory).Name;

        // The archive is written beside the session directory rather than inside it, so a
        // rebuild never ends up packaging the previous archive into the next one.
        var parent = Directory.GetParent(paths.Directory)?.FullName ?? paths.Directory;
        var zipPath = Path.Combine(parent, $"Dokazi_{name}.zip");

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var file in Directory.EnumerateFiles(paths.Directory, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entryName = Path.GetRelativePath(paths.Directory, file).Replace('\\', '/');

            // Written through a shared handle rather than CreateEntryFromFile, for the same
            // reason as the checksums: the service may still be writing the raw log.
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            using var source = OpenShared(file);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }

        return zipPath;
    }

    /// <summary>Human-readable description of what was produced, for the console.</summary>
    public static IEnumerable<string> Describe(PackageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        yield return $"Folder:   {result.Directory}";

        if (result.ZipPath is not null)
        {
            yield return $"Arhiva:   {result.ZipPath}";
        }

        yield return $"Fajlova:  {result.Files.Count + 3}";
        yield return result.Verification.Valid
            ? $"Integritet: ISPRAVAN ({result.Verification.EntriesChecked} zapisa)"
            : $"Integritet: NARUŠEN - red {result.Verification.FirstBrokenLine}";

        // Said out loud rather than left to be noticed, because the folder otherwise looks
        // complete. The evidence is untouched either way; what is missing is a convenience
        // copy of a report that is also sitting right there as HTML.
        if (result.PdfFailure is not null)
        {
            yield return
                $"UPOZORENJE: {ReportPdf} nije napravljen ({result.PdfFailure}). " +
                $"{ReportHtml} sadrži isti izveštaj i paket je inače potpun.";
        }

        // Records the chain carries that this build could not read. Said out loud, because
        // the alternative is a report that is quietly short of evidence that exists.
        if (result.Rebuild.Incomplete)
        {
            yield return
                $"UPOZORENJE: {result.Rebuild.UnreadableEntries} zapisa iz sirove evidencije " +
                "nije moglo da se pročita ovom verzijom programa i nije ušlo u izveštaj. " +
                "Sirova evidencija je i dalje potpuna i ispravna.";
        }

        // Reported rather than passed over: a difference between the chain and the index is
        // ordinary after a crash, but silence about it would look like concealment.
        if (result.Rebuild.Reconciled)
        {
            yield return
                $"Indeks:   USKLAĐEN - u sirovoj evidenciji {result.Rebuild.DerivedRecords} zapisa, " +
                $"u bazi {result.Rebuild.ExistingIndexRecords}. Izveštaj je napravljen iz sirove evidencije.";
        }
    }

    /// <summary>Total duration formatted for the console, kept here so callers stay terse.</summary>
    public static string FormatDuration(TimeSpan value) => SerbianText.Duration(value);
}
