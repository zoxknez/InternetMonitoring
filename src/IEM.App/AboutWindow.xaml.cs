using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using IEM.Core.Presentation;

namespace IEM.App;

/// <summary>
/// What this program is, who made it, and where to send what you find.
/// <para>
/// The text comes from <see cref="AppInfo"/> rather than from the markup, because the same
/// sentences appear in the console's help and in the README. A Discord invite that has
/// expired in one place and not the others is worse than not offering one at all.
/// </para>
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        TitleText.Text = BuildInfo.Product;
        VersionText.Text = $"verzija {BuildInfo.Version} · {AppInfo.LicenseName} licenca · autor {AppInfo.Author}";

        SummaryText.Text = AppInfo.Summary;
        PrivacyText.Text = AppInfo.PrivacyLine;
        FeedbackText.Text = AppInfo.FeedbackLine;
        CautionText.Text = AppInfo.ReportingCaution;

        // The address as a person would type it, without the scheme - and without "mailto:",
        // which is machinery rather than an address.
        LinkList.ItemsSource = AppInfo.Links
            .Select(link => new
            {
                link.Label,
                link.Target,
                Shown = link.Target
                    .Replace("mailto:", string.Empty, StringComparison.Ordinal)
                    .Replace("https://", string.Empty, StringComparison.Ordinal),
            })
            .ToList();

        LicenseText.Text =
            $"Slobodan softver pod {AppInfo.LicenseName} licencom. Izvorni kod, istorija izmena i " +
            "spisak nedovršenog stoje na GitHubu.";
    }

    /// <summary>
    /// Opens the link in whatever the system uses for it.
    /// <para>
    /// Failure is swallowed on purpose: a machine with no browser registered, or a person who
    /// cancelled the prompt, is not a reason for a dialog about the program to throw - least
    /// of all in an application someone may have left running for two days.
    /// </para>
    /// </summary>
    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing registered to open it, or the user declined.
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
