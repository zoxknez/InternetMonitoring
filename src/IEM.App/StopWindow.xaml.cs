using System.Windows;
using IEM.Core.Presentation;

namespace IEM.App;

/// <summary>
/// Asks what should happen to a session that is about to be stopped.
/// <para>
/// The button used to stop monitoring and say nothing, which left people who had just ended a
/// two-day recording with no idea whether they had a report, whether anything was lost, or
/// what to press next. Enough of them asked that the silence was clearly the defect.
/// </para>
/// <para>
/// The sentences come from <see cref="StopPrompt"/> rather than from the markup, so a test can
/// read exactly what was offered without standing up a window.
/// </para>
/// </summary>
public partial class StopWindow : Window
{
    public StopWindow(TimeSpan monitored, int incidents)
    {
        InitializeComponent();

        SummaryText.Text = StopPrompt.Summarise(monitored, incidents);
        QuestionText.Text = StopPrompt.Question;
        ReassuranceText.Text = StopPrompt.Reassurance;

        ReportLabel.Text = StopPrompt.ReportLabel;
        ReportDetail.Text = StopPrompt.ReportDetail;
        StopOnlyLabel.Text = StopPrompt.StopOnlyLabel;
        StopOnlyDetail.Text = StopPrompt.StopOnlyDetail;
        CancelLabel.Text = StopPrompt.CancelLabel;
        CancelDetail.Text = StopPrompt.CancelDetail;
    }

    /// <summary>
    /// Cancel by default. Closing the dialog with the title bar, Escape, or anything else that
    /// is not an answer must leave the session running: a session stopped by a stray keypress
    /// cannot be resumed, and nobody meant it.
    /// </summary>
    public StopChoice Choice { get; private set; } = StopChoice.Cancel;

    private void OnReport(object sender, RoutedEventArgs e) => Answer(StopChoice.StopAndReport);

    private void OnStopOnly(object sender, RoutedEventArgs e) => Answer(StopChoice.StopOnly);

    private void OnCancel(object sender, RoutedEventArgs e) => Answer(StopChoice.Cancel);

    private void Answer(StopChoice choice)
    {
        Choice = choice;
        DialogResult = choice != StopChoice.Cancel;
    }
}
