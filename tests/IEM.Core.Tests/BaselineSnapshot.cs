using System.Runtime.CompilerServices;
using System.Text.Json;

namespace IEM.Core.Tests;

/// <summary>
/// Where the frozen artefacts live, and the rule that they have to be there.
/// <para>
/// BASELINE_FIXTURES_ARE_RELEASE_ARTIFACTS. A fixture present on the machine that wrote it is
/// not the same thing as one present in the repository, and neither is the same as one present
/// in the release tag. 2.7.1 shipped with its fixtures excluded by a <c>.gitignore</c> rule
/// meant for real sessions: every test passed locally and the same tests failed on CI, because
/// the files simply were not there.
/// </para>
/// <para>
/// So nothing here is conditional. A missing file is a failure, never a skipped check - a test
/// that quietly does nothing when its input is absent is worse than no test, because it
/// reports success for work it did not do.
/// </para>
/// </summary>
internal static class BaselineSnapshot
{
    /// <summary>The version whose output is frozen here.</summary>
    public const string Version = "v2.7.2";

    public const string ManifestFile = "manifest-baseline.json";

    /// <summary>
    /// The chain's final hash as it was when the snapshot was frozen.
    /// <para>
    /// Recorded here rather than recomputed, because recomputing it would make the check
    /// tautological: the point is that this exact file still hashes to this exact value under
    /// whatever build reads it next.
    /// </para>
    /// </summary>
    public const string HeadHash = "beb964ee5f51e4c4572b242e584b4000448f305e3841ec52e67067b5b6ce2a43";

    public static string Root => Path.Combine(RepositoryRoot(), "baseline", Version);

    public static string Session => Path.Combine(Root, "sesija");

    /// <summary>Every file the snapshot is supposed to contain, as recorded when it was written.</summary>
    public static IReadOnlyList<string> Manifest()
    {
        var path = Require(Path.Combine(Session, ManifestFile));

        return JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Spisak {path} nije čitljiv.");
    }

    /// <summary>The path to a file of the snapshot, or a failure naming what is missing.</summary>
    public static string File(string name) => Require(Path.Combine(Session, name));

    private static string Require(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Baseline artefakt nedostaje: {path}. Fajlovi iz baseline/ su deo izdanja - " +
                "ako ih nema, provera se ne preskače nego pada. Proverite da nisu isključeni " +
                "u .gitignore i da su zaista u komitu, a ne samo na disku.",
                path);
        }

        return path;
    }

    /// <summary>
    /// Found by walking up from the output folder, because a deterministic build rewrites this
    /// file's compile-time path to <c>/_/tests/…</c> and the first lookup then finds nothing.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string here = "")
    {
        var fromSource = Path.GetDirectoryName(here);

        if (fromSource is not null && Directory.Exists(fromSource))
        {
            return Path.GetFullPath(Path.Combine(fromSource, "..", ".."));
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.GetFiles("*.slnx").Length > 0)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repozitorijum nije pronađen.");
    }
}
