using System.Windows;
using System.Windows.Threading;
using IEM.Core.Presentation;

namespace IEM.App.Tests;

/// <summary>
/// The "O programu" dialog, built and shown for real.
/// <para>
/// Worth a test rather than a look, because everything in it comes from
/// <see cref="AppInfo"/> at construction time - and a dialog whose parse fails, or which
/// shows with the version line empty, is exactly the sort of thing that goes unnoticed until
/// somebody wants the address to send a bug to.
/// </para>
/// <para>
/// Each case runs on its own STA thread with its own dispatcher. WPF requires that, and a
/// shared one would let one test's window outlive it into the next.
/// </para>
/// </summary>
public sealed class AboutWindowTests
{
    private static T OnStaThread<T>(Func<T> work)
    {
        var result = default(T)!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // No Application is created on purpose. The dialog merges the theme itself, so
                // it stands up without one - and setting Application.Current here would set it
                // for the whole process and break every other test in this assembly.
                result = work();
            }
#pragma warning disable CA1031 // Carried across the thread boundary and rethrown below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (failure is not null)
        {
            throw new InvalidOperationException("Dijalog nije uspeo.", failure);
        }

        return result;
    }

    /// <summary>
    /// Constructs and shows it. A parse failure or a missing resource shows up here as an
    /// exception rather than as a button that does nothing when a person clicks it.
    /// </summary>
    [Fact]
    public void The_dialog_opens_and_is_visible()
    {
        var shown = OnStaThread(() =>
        {
            var about = new AboutWindow();
            about.Show();

            var visible = about.IsVisible && about.ActualWidth > 400 && about.ActualHeight > 200;
            about.Close();

            return visible;
        });

        Assert.True(shown);
    }

    /// <summary>
    /// The version has to be the build's own, so a bug report names a build that exists.
    /// </summary>
    [Fact]
    public void It_states_the_version_licence_and_author()
    {
        var text = OnStaThread(() =>
        {
            var about = new AboutWindow();
            return $"{about.Title} | {VersionLineOf(about)}";
        });

        Assert.Contains("O programu", text, StringComparison.Ordinal);
        Assert.Contains(BuildInfo.Version, text, StringComparison.Ordinal);
        Assert.Contains(AppInfo.LicenseName, text, StringComparison.Ordinal);
        Assert.Contains(AppInfo.Author, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// All three channels reach the dialog, with the addresses shown the way a person reads
    /// them - no scheme, no "mailto:".
    /// </summary>
    [Fact]
    public void It_offers_every_way_to_report_something()
    {
        var links = OnStaThread(() =>
        {
            var about = new AboutWindow();
            return string.Join(
                " | ",
                about.LinkList.Items.Cast<object>().Select(item => item.ToString() ?? string.Empty));
        });

        Assert.Contains("github.com/zoxknez/InternetMonitoring", links, StringComparison.Ordinal);
        Assert.Contains("discord.gg", links, StringComparison.Ordinal);
        Assert.Contains("mojportfolio.vercel.app", links, StringComparison.Ordinal);
        Assert.Contains(AppInfo.Email, links, StringComparison.Ordinal);

        // Shown without the machinery: nobody reads "mailto:" as part of an address.
        Assert.DoesNotContain("Shown = mailto:", links, StringComparison.Ordinal);
    }

    /// <summary>
    /// Says what a session folder contains, because the person about to attach one has to
    /// know it carries the names of their networks and the addresses of their equipment.
    /// </summary>
    [Fact]
    public void It_warns_what_a_session_folder_contains()
    {
        var caution = OnStaThread(() => new AboutWindow().CautionText.Text);

        Assert.Equal(AppInfo.ReportingCaution, caution);
    }

    private static string VersionLineOf(AboutWindow about) => about.VersionText.Text;
}
