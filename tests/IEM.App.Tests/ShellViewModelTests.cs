using System.IO;
using IEM.App.Hosting;
using IEM.App.ViewModels;
using IEM.Core;
using IEM.Core.Model;
using IEM.Storage;

using IEM.Core.Presentation;

namespace IEM.App.Tests;

/// <summary>
/// What the window decides on its own.
/// <para>
/// Until now the window was checked by looking at it, which catches a misplaced control and
/// misses everything else: a measurement filed as unusable, a schedule that dies with the
/// process, a refusal shown as a success. The decisions live in the view model, so that is
/// what these exercise - with no service, no network and no engine behind them.
/// </para>
/// </summary>
public sealed class ShellViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-app-tests", Guid.NewGuid().ToString("N"));

    public ShellViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over a leftover temp directory.
        }
    }

    private ShellViewModel Shell(StubMonitorHost host) => new(host, _root);

    // ---- Starting and stopping ------------------------------------------------------

    [Fact]
    public async Task Starting_asks_the_host_for_the_chosen_duration()
    {
        var host = new StubMonitorHost();
        var shell = Shell(host);

        shell.SelectedDuration = shell.Durations.Single(d => d.Duration == TimeSpan.FromHours(24));

        await shell.StartCommand.ExecuteAsync(null);

        Assert.Equal(TimeSpan.FromHours(24), host.RequestedDuration);
        Assert.True(shell.IsRunning);
        Assert.Null(shell.Fault);
    }

    /// <summary>
    /// A refused start has to say so. Shown as running, the window would sit there with a
    /// green pill recording nothing - the worst outcome for someone who thinks a two-day
    /// test is under way.
    /// </summary>
    [Fact]
    public async Task A_refused_start_is_reported_rather_than_shown_as_running()
    {
        var host = new StubMonitorHost { StartSucceeds = false };
        var shell = Shell(host);

        await shell.StartCommand.ExecuteAsync(null);

        Assert.False(shell.IsRunning);
        Assert.NotNull(shell.Fault);
    }

    [Fact]
    public async Task Stopping_goes_through_the_host()
    {
        var host = new StubMonitorHost();
        var shell = Shell(host);

        await shell.StartCommand.ExecuteAsync(null);
        await shell.StopCommand.ExecuteAsync(null);

        Assert.Equal(1, host.StopsRequested);
        Assert.False(shell.IsRunning);
    }

    /// <summary>
    /// Whether monitoring outlives the window is stated, not implied - the difference matters
    /// enormously to somebody about to commit a machine for two days.
    /// </summary>
    [Theory]
    [InlineData(HostKind.Service, true)]
    [InlineData(HostKind.InProcess, false)]
    public void The_window_says_whether_the_test_survives_being_closed(HostKind kind, bool survives)
    {
        var shell = Shell(new StubMonitorHost(kind));

        Assert.Equal(survives, shell.SurvivesClosing);
        Assert.Contains(survives ? "možete zatvoriti" : "zaustavlja", shell.HostDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_test_with_no_planned_end_says_so_instead_of_naming_an_hour()
    {
        var shell = Shell(new StubMonitorHost());

        shell.SelectedDuration = shell.Durations.Single(d => d.Duration == Timeout.InfiniteTimeSpan);

        Assert.Contains("nema rok", shell.EndsAtText, StringComparison.Ordinal);
    }

    // ---- The contracted rate --------------------------------------------------------

    /// <summary>
    /// Refused rather than half-read: a typo quietly understood as some other number is how a
    /// complaint ends up quoting a rate nobody ever contracted.
    /// </summary>
    [Fact]
    public async Task An_unreadable_contracted_rate_refuses_the_measurement_and_says_why()
    {
        var host = new StubMonitorHost(HostKind.Service);
        var shell = Shell(host);

        shell.ContractedRateText = "sto";
        shell.SpeedScheduleAmount = "10";

        await shell.ScheduleSpeedCommand.ExecuteAsync(null);

        Assert.NotNull(shell.Fault);
        Assert.Null(SpeedRequest.Read(_root));
    }

    [Fact]
    public async Task Scheduling_without_a_number_says_what_is_missing()
    {
        var shell = Shell(new StubMonitorHost(HostKind.Service));

        shell.SpeedScheduleAmount = string.Empty;

        await shell.ScheduleSpeedCommand.ExecuteAsync(null);

        Assert.NotNull(shell.Fault);
        Assert.Null(SpeedRequest.Read(_root));
    }

    // ---- Scheduling that outlives the window ----------------------------------------

    /// <summary>
    /// The one case scheduling exists for is three in the morning with nobody awake, so with
    /// the service installed the instruction is handed over rather than kept in a window that
    /// will be closed.
    /// </summary>
    [Fact]
    public async Task With_the_service_installed_the_schedule_is_handed_to_it()
    {
        var shell = Shell(new StubMonitorHost(HostKind.Service));

        shell.SpeedScheduleAmount = "90";
        shell.SelectedSpeedScheduleUnit = "minuta";
        shell.ContractedRateText = "100/20";

        await shell.ScheduleSpeedCommand.ExecuteAsync(null);

        var request = SpeedRequest.Read(_root);

        Assert.NotNull(request);
        Assert.Equal(100, request.ContractedDownloadMbps);
        Assert.Equal(20, request.ContractedUploadMbps);
        Assert.InRange(
            request.DueAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(88),
            DateTimeOffset.UtcNow.AddMinutes(92));

        Assert.NotNull(shell.SpeedStatus);
        Assert.Contains("servis", shell.SpeedStatus, StringComparison.OrdinalIgnoreCase);

        // Handed over, so the window is free again rather than sitting on a countdown.
        Assert.True(shell.CanScheduleSpeed);
    }

    [Fact]
    public async Task Hours_are_read_as_hours()
    {
        var shell = Shell(new StubMonitorHost(HostKind.Service));

        shell.SpeedScheduleAmount = "3";
        shell.SelectedSpeedScheduleUnit = "sati";

        await shell.ScheduleSpeedCommand.ExecuteAsync(null);

        var request = SpeedRequest.Read(_root);

        Assert.NotNull(request);
        Assert.InRange(
            request.DueAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(178),
            DateTimeOffset.UtcNow.AddMinutes(182));
    }

    [Fact]
    public async Task Scheduling_further_out_than_a_week_is_refused()
    {
        var shell = Shell(new StubMonitorHost(HostKind.Service));

        shell.SpeedScheduleAmount = "300";
        shell.SelectedSpeedScheduleUnit = "sati";

        await shell.ScheduleSpeedCommand.ExecuteAsync(null);

        Assert.NotNull(shell.Fault);
        Assert.Null(SpeedRequest.Read(_root));
    }

    /// <summary>
    /// The instruction outlives this window, so a window opened afterwards has to be able to
    /// say that a measurement is still coming.
    /// </summary>
    [Fact]
    public async Task A_window_opened_later_says_a_measurement_is_still_scheduled()
    {
        new SpeedRequest(DateTimeOffset.UtcNow.AddHours(2), 100, 20).Write(_root);

        var shell = Shell(new StubMonitorHost(HostKind.Service));

        await shell.ConnectAsync();

        Assert.NotNull(shell.SpeedStatus);
        Assert.Contains("zakazano", shell.SpeedStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_nothing_scheduled_the_window_says_nothing_about_it()
    {
        var shell = Shell(new StubMonitorHost(HostKind.Service));

        await shell.ConnectAsync();

        Assert.Null(shell.SpeedStatus);
    }

    // ---- What the window shows from a reading ---------------------------------------

    [Fact]
    public void The_figures_on_screen_come_from_the_snapshot()
    {
        var shell = Shell(new StubMonitorHost());

        shell.Snapshot = MonitorSnapshot.Empty with
        {
            CurrentState = NetworkState.CpeUpstreamUnreachable,
            CurrentLatency = TimeSpan.FromMilliseconds(43),
            AvailabilityPercent = 99.5,
            UpstreamIncidentCount = 2,
            IncidentCount = 3,
            UpstreamDowntime = TimeSpan.FromMinutes(7),
            Medium = LinkMedium.Wireless,
        };

        Assert.Equal("43 ms", shell.LatencyText);
        Assert.False(shell.IsOnline);
        Assert.Contains("bežična", shell.MediumText, StringComparison.Ordinal);

        // The wireless warning is up front rather than after two days, because a speed claim
        // built on Wi-Fi is the single most common reason a complaint is dismissed.
        Assert.True(shell.ShowWirelessWarning);

        var incidents = shell.Metrics.Single(m => m.Label == "Prekida iza rutera");
        Assert.Equal("2", incidents.Value);
        Assert.Contains("3", incidents.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_reading_yet_the_latency_cell_is_empty_rather_than_zero()
    {
        var shell = Shell(new StubMonitorHost());

        Assert.Equal("-", shell.LatencyText);
        Assert.Contains("NIJE POKRENUT", shell.StatusPill, StringComparison.Ordinal);
    }

    // ---- Tray notifications ----------------------------------------------------------

    /// <summary>
    /// Nobody watches this window through a two-day test, so the toast is how the person
    /// whose evidence this is learns the fault was caught at all.
    /// </summary>
    [Fact]
    public void The_connection_failing_and_returning_are_both_announced()
    {
        var host = new StubMonitorHost();
        var shell = Shell(host);

        var announcements = new List<string>();
        shell.TrayNotification += (title, _) => announcements.Add(title);

        host.Push(MonitorSnapshot.Empty with { CurrentState = NetworkState.CpeUpstreamUnreachable });
        host.Push(MonitorSnapshot.Empty with { CurrentState = NetworkState.CpeUpstreamUnreachable });
        host.Push(MonitorSnapshot.Empty with { CurrentState = NetworkState.Ok });

        // One for the outage, one for the recovery - and nothing for the sample in between,
        // which would turn a two-day test into a stream of toasts.
        Assert.Equal(2, announcements.Count);
        Assert.Contains("Prekid", announcements[0], StringComparison.Ordinal);
        Assert.Contains("vratila", announcements[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A reading arriving from the host reaches the bound state, which is the whole job of
    /// this class - and was the one thing no test could reach while an update with no
    /// dispatcher present was silently dropped.
    /// </summary>
    [Fact]
    public void A_reading_from_the_host_reaches_the_screen()
    {
        var host = new StubMonitorHost();
        var shell = Shell(host);

        host.Push(MonitorSnapshot.Empty with
        {
            SessionId = "S20260817104622",
            CurrentState = NetworkState.Ok,
            CurrentLatency = TimeSpan.FromMilliseconds(17),
            SampleCount = 107,
            InterfaceName = "Ethernet 4",
            Medium = LinkMedium.Ethernet,
        });

        Assert.True(shell.IsRunning);
        Assert.Equal("17 ms", shell.LatencyText);
        Assert.Contains("NADZOR U TOKU", shell.StatusPill, StringComparison.Ordinal);
        Assert.Contains("Ethernet 4", shell.FactsLine, StringComparison.Ordinal);
        Assert.Contains("S20260817104622", shell.FactsLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_healthy_connection_announces_nothing()
    {
        var host = new StubMonitorHost();
        var shell = Shell(host);

        var announcements = 0;
        shell.TrayNotification += (_, _) => announcements++;

        host.Push(MonitorSnapshot.Empty with { CurrentState = NetworkState.Ok });
        host.Push(MonitorSnapshot.Empty with { CurrentState = NetworkState.IcmpFiltered });

        Assert.Equal(0, announcements);
    }

    // ---- Faults and disposal ---------------------------------------------------------

    [Fact]
    public void A_host_losing_contact_reaches_the_window()
    {
        var host = new StubMonitorHost(HostKind.Service);
        var shell = Shell(host);

        host.PushFault("Prekinuta je veza između prozora i servisa.");

        Assert.Equal("Prekinuta je veza između prozora i servisa.", shell.Fault);

        host.PushFault(null);

        Assert.Null(shell.Fault);
    }

    [Fact]
    public async Task Closing_the_window_releases_the_host()
    {
        var host = new StubMonitorHost();
        var shell = Shell(host);

        await shell.DisposeAsync();

        Assert.True(host.Disposed);
    }

    /// <summary>
    /// Run without the service - which is what the portable executable is - the window must
    /// not promise that monitoring outlives it.
    /// <para>
    /// It did. "Prozor možete zatvoriti. Nadzor se nastavlja kao Windows servis" and
    /// "Preživljava restart" were fixed text with a tick beside each, so someone starting a
    /// two-day test from a single downloaded file was told they could close it. Missed until
    /// the portable build was run and its own screen read, because the machine it is
    /// developed on has the service installed.
    /// </para>
    /// </summary>
    [Fact]
    public void Without_the_service_the_window_promises_nothing_it_cannot_keep()
    {
        var window = Shell(new StubMonitorHost(HostKind.InProcess));

        Assert.False(window.SurvivesClosing);

        var promises = string.Join(
            " ",
            window.BackgroundClaimLabel,
            window.BackgroundClaimDetail,
            window.RestartClaimLabel,
            window.RestartClaimDetail,
            window.HostDescription);

        Assert.DoesNotContain("Prozor možete zatvoriti", promises, StringComparison.Ordinal);
        Assert.DoesNotContain("nastavlja kao Windows servis", promises, StringComparison.Ordinal);
        Assert.DoesNotContain("sesija se nastavlja tamo gde je stala", promises, StringComparison.Ordinal);

        Assert.Contains("test se zaustavlja", promises, StringComparison.Ordinal);
        Assert.Contains("restart računara prekida test", promises, StringComparison.Ordinal);

        // A tick beside "ne preživljava restart" would be the picture arguing with the words.
        Assert.NotEqual("M 4.5,9.2 L 7.6,12.2 L 13.5,5.8", window.ClaimGlyph);
    }

    /// <summary>Attached to the service, the same two claims are true and are made.</summary>
    [Fact]
    public void With_the_service_the_window_says_the_test_outlives_it()
    {
        var window = Shell(new StubMonitorHost(HostKind.Service));

        Assert.True(window.SurvivesClosing);
        Assert.Contains("Prozor možete zatvoriti", window.BackgroundClaimDetail, StringComparison.Ordinal);
        Assert.Contains("tamo gde je stala", window.RestartClaimDetail, StringComparison.Ordinal);
        Assert.Equal("M 4.5,9.2 L 7.6,12.2 L 13.5,5.8", window.ClaimGlyph);
    }

    /// <summary>
    /// A degraded state is neither green nor red, and the tile must not round it to either.
    /// <para>
    /// A tester saw "Dodeljeni DNS server ne odgovara" written in green under a green tick,
    /// because the tile was coloured by "is this an outage" alone. The outage strip drew the
    /// same moment amber - the window was disagreeing with itself on one screen.
    /// </para>
    /// </summary>
    [Fact]
    public void A_degraded_state_is_neither_the_ok_colour_nor_the_outage_one()
    {
        var host = new StubMonitorHost();
        var shell = Shell(host);

        host.Push(shell.Snapshot with { CurrentState = NetworkState.DnsIspFailure });

        Assert.Equal(Severity.Degraded, shell.CurrentSeverity);

        // Still "online" - the line works, name resolution does not - which is exactly why
        // that flag alone could never carry the tile's colour.
        Assert.True(shell.IsOnline);
    }

    [Fact]
    public void An_outage_and_a_clean_state_keep_their_own_severities()
    {
        var host = new StubMonitorHost();
        var shell = Shell(host);

        host.Push(shell.Snapshot with { CurrentState = NetworkState.CpeUpstreamUnreachable });
        Assert.Equal(Severity.Outage, shell.CurrentSeverity);

        host.Push(shell.Snapshot with { CurrentState = NetworkState.Ok });
        Assert.Equal(Severity.Ok, shell.CurrentSeverity);
    }

    // ---- Zaustavljanje -----------------------------------------------------

    /// <summary>
    /// The question is asked before anything stops, and "nastavi nadzor" really does mean
    /// nothing happens. A session stopped by a stray click cannot be resumed.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_question_leaves_the_session_running()
    {
        var host = new StubMonitorHost();
        var shell = new ShellViewModel(host, _root) { StopPromptAsked = (_, _) => StopChoice.Cancel };

        await shell.StartCommand.ExecuteAsync(null);
        await shell.StopCommand.ExecuteAsync(null);

        Assert.Equal(0, host.StopsRequested);
        Assert.True(shell.IsRunning);
    }

    [Fact]
    public async Task Stopping_without_a_report_stops_and_nothing_more()
    {
        var host = new StubMonitorHost();
        var shell = new ShellViewModel(host, _root) { StopPromptAsked = (_, _) => StopChoice.StopOnly };

        await shell.StartCommand.ExecuteAsync(null);
        await shell.StopCommand.ExecuteAsync(null);

        Assert.Equal(1, host.StopsRequested);
        Assert.False(shell.IsRunning);
    }

    /// <summary>
    /// What the question shows: how much has been recorded, and - first - that stopping loses
    /// nothing. That sentence is the reason people were asking in the first place.
    /// </summary>
    [Fact]
    public void The_question_says_what_is_at_stake_and_that_nothing_is_lost()
    {
        var recorded = StopPrompt.Summarise(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(14), 3);

        Assert.Contains("2h 14m", recorded, StringComparison.Ordinal);
        Assert.Contains("3", recorded, StringComparison.Ordinal);
        Assert.Contains("Nijedan prekid nije zabeležen", StopPrompt.Summarise(TimeSpan.FromMinutes(9), 0), StringComparison.Ordinal);

        Assert.Contains("ne gubi se", StopPrompt.Reassurance, StringComparison.Ordinal);

        // Every answer says what it does, so none of the three is a leap in the dark.
        foreach (var detail in new[] { StopPrompt.ReportDetail, StopPrompt.StopOnlyDetail, StopPrompt.CancelDetail })
        {
            Assert.False(string.IsNullOrWhiteSpace(detail));
        }
    }
}
