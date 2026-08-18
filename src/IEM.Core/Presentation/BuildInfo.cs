using System.Reflection;

namespace IEM.Core.Presentation;

/// <summary>
/// What produced a report.
/// <para>
/// Stated on every report because reproducibility is part of what makes the evidence worth
/// anything. A reader who wants to check a figure has to be able to run the same build over
/// the same raw log and get the same answer - and to do that they need to know which build
/// it was, and whether its dependencies were pinned or whatever NuGet resolved that day.
/// </para>
/// </summary>
public static class BuildInfo
{
    public static string Product => "Internet Monitoring";

    /// <summary>
    /// The build's own version, prerelease suffix and all.
    /// <para>
    /// Taken from the informational version rather than the assembly version, because the
    /// assembly version is numeric only: a beta build would introduce itself as "2.8.0" - a
    /// release that does not exist - and a reader trying to reproduce a figure would go
    /// looking for it. The commit hash the compiler appends is trimmed; the version is for
    /// naming a build, and the rest belongs in the release notes.
    /// </para>
    /// </summary>
    public static string Version { get; } = Describe();

    private static string Describe()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);

            return plus < 0 ? informational : informational[..plus];
        }

        return typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Whether every package version was fixed in advance rather than resolved on the day.
    /// <para>
    /// True for release builds: each version is pinned centrally, so no dependency can float
    /// to a newer one between two builds of the same source.
    /// </para>
    /// <para>
    /// Claims exactly that and no more. Lock files recording the full transitive graph are
    /// kept as well, but they are rewritten by every restore - and a publish for a different
    /// architecture restores in a different shape - so they cannot be presented as an
    /// unbroken seal. The pinned versions are the part that actually holds, and overstating
    /// the rest would put a claim in the report that someone checking could take apart.
    /// </para>
    /// </summary>
    public static bool DependenciesLocked { get; } =
#if DEBUG
        false;
#else
        true;
#endif
}
