using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace IEM.Core.Tests;

/// <summary>
/// The one rule this whole tool exists to keep, enforced against the source itself.
/// <para>
/// Every other test states what some function returns for some input. This one states what
/// the code is not allowed to contain, because the defect it guards against does not look
/// like a bug at the call site: <c>?? true</c> and <c>!= false</c> read as tidy defensive
/// code, and each one silently converts "I could not check" into "I checked and it was
/// fine". Both were in the shipped 2.6, both on the paths that decide whether a measurement
/// can go into a complaint, and neither broke a single test.
/// </para>
/// <para>
/// A source scan rather than a behavioural test, deliberately. Behaviour tests catch the
/// substitution one call site at a time, after somebody has thought to write the case. This
/// catches the next one, on a call site nobody has written yet.
/// </para>
/// </summary>
public sealed class SourceInvariantTests
{
    /// <summary>
    /// UNKNOWN_NEVER_BECOMES_CONFIRMED.
    /// <para>
    /// A nullable that means "not established" must never be collapsed to the affirmative by
    /// a default. Where a value genuinely has three states, the code says so with a three or
    /// four state enum whose default is the unknown one, and every consumer handles it.
    /// </para>
    /// </summary>
    [Fact]
    public void Unknown_never_becomes_confirmed()
    {
        string[] forbidden =
        [
            // The speed measurement's own path. Three call sites turned "the route table had
            // no answer" into "it left through the monitored adapter" - which is the single
            // fact that decides whether the figure can be attributed to this connection.
            @"\?\?\s*true",

            // The wireless radio. Null means the radio could not be read; treating that as
            // "on" reported the access point as having stopped broadcasting.
            @"RadioOn\s*!=\s*false",
        ];

        var offences = new List<string>();

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var code = WithoutComment(lines[i]);

                if (IsTheFoldIdentity(code))
                {
                    continue;
                }

                foreach (var pattern in forbidden)
                {
                    if (Regex.IsMatch(code, pattern))
                    {
                        offences.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.Empty(offences);
    }

    /// <summary>
    /// The scan has to be able to find the source, or it passes by finding nothing and proves
    /// the opposite of what it claims.
    /// </summary>
    [Fact]
    public void The_scan_is_actually_reading_the_source()
    {
        var files = SourceFiles();
        var names = files.Select(Path.GetFileName).ToArray();

        Assert.True(files.Count >= 60, $"Pregledano je samo {files.Count} fajlova; provera ne bi ništa našla.");

        // The three files that carried the substitution in 2.6, named so that moving or
        // renaming one cannot quietly take it out of the scan's reach.
        Assert.Contains("SpeedCommand.cs", names, StringComparer.Ordinal);
        Assert.Contains("SpeedWorker.cs", names, StringComparer.Ordinal);
        Assert.Contains("ShellViewModel.cs", names, StringComparer.Ordinal);
        Assert.Contains("StateClassifier.cs", names, StringComparer.Ordinal);
    }

    /// <summary>
    /// The one legitimate <c>?? true</c> in this codebase: the seed of a fold that narrows a
    /// claim across samples. True is the identity for <c>&amp;&amp;</c>, so the first
    /// observation decides the value and null survives only when no sample was ever seen -
    /// the opposite of a substitution. Matched on the whole expression rather than allowing a
    /// file, so a second one appearing in that file still fails.
    /// </summary>
    private static bool IsTheFoldIdentity(string code) =>
        code.Contains("(held ?? true) && observation", StringComparison.Ordinal);

    /// <summary>Every compiled source file in the product, tests and generated code aside.</summary>
    private static IReadOnlyList<string> SourceFiles() =>
    [
        .. Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(file => file, StringComparer.Ordinal),
    ];

    /// <summary>
    /// Everything before a line comment. The prose in this codebase quotes the very patterns
    /// being banned - including in the comment explaining why they are banned - and a scan
    /// that tripped over its own explanation would be useless.
    /// </summary>
    private static string WithoutComment(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }

    /// <summary>
    /// Found from this file's own compile-time path rather than from the working directory,
    /// which under a test runner is the output folder and several levels from anywhere useful.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
