using IEM.Core.Presentation;

namespace IEM.Core.Tests;

/// <summary>
/// The program introduces itself in four places - the window's "O programu", the console's
/// help, the README and the release notes - and they all read from one place. These are the
/// checks that keep a typo in a link from reaching all four at once.
/// </summary>
public sealed class AppInfoTests
{
    [Fact]
    public void Every_link_is_something_a_browser_or_mail_client_can_open()
    {
        Assert.NotEmpty(AppInfo.Links);

        foreach (var (label, target) in AppInfo.Links)
        {
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.True(
                Uri.TryCreate(target, UriKind.Absolute, out var uri),
                $"Nije apsolutna adresa: {target}");

            Assert.Contains(uri.Scheme, new[] { "https", "mailto" }, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// All three channels are offered, because they are for different things: GitHub for what
    /// should stay recorded, Discord for a conversation, mail for whoever wants neither.
    /// </summary>
    [Fact]
    public void All_three_ways_to_report_something_are_offered()
    {
        var targets = string.Join(" ", AppInfo.Links.Select(link => link.Target));

        Assert.Contains("github.com", targets, StringComparison.Ordinal);
        Assert.Contains("discord", targets, StringComparison.Ordinal);
        Assert.Contains(AppInfo.Email, targets, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mail_address_is_an_address_and_not_a_sentence()
    {
        Assert.Matches(@"^[^@\s]+@[^@\s]+\.[a-z]{2,}$", AppInfo.Email);
    }

    /// <summary>
    /// Whoever is about to attach a session has to be told what is in it. The folder carries
    /// the names of their networks and the addresses of their equipment.
    /// </summary>
    [Fact]
    public void Anyone_reporting_a_bug_is_warned_what_a_session_contains()
    {
        Assert.Contains("mreža", AppInfo.ReportingCaution, StringComparison.Ordinal);
        Assert.Contains("opreme", AppInfo.ReportingCaution, StringComparison.Ordinal);
    }

    /// <summary>
    /// The version shown beside the name is the build's own, so a bug report names a build
    /// that exists rather than a number somebody typed into a string.
    /// </summary>
    [Fact]
    public void The_version_line_carries_the_build_it_was_compiled_from()
    {
        Assert.Contains(BuildInfo.Version, AppInfo.VersionLine, StringComparison.Ordinal);
        Assert.Contains(BuildInfo.Product, AppInfo.VersionLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_licence_and_the_author_are_stated()
    {
        Assert.Equal("MIT", AppInfo.LicenseName);
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Author));
    }
}
