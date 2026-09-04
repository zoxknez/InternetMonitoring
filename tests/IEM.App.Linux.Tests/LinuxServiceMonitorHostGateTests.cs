using IEM.App.Linux.Hosting;
using IEM.Presentation.Hosting;

namespace IEM.App.Linux.Tests;

/// <summary>
/// 3.1-10 gate: "GUI loss does not affect measurement in SystemService mode."
/// The Avalonia shell attached to the installed service is a reader. Nothing it does on its
/// own - polling, closing, crashing - may reach the running session.
/// </summary>
public sealed class LinuxServiceMonitorHostGateTests
{
    [Fact]
    public async Task Poll_loop_only_reads_status_and_never_touches_the_session()
    {
        await using var socket = new FakeControlSocket();
        await using var host = new LinuxServiceMonitorHost(socket.Path);

        await host.ConnectAsync(CancellationToken.None);
        await WaitUntil(() => socket.Received("GetServiceStatus"));

        // Give the 1 s poll loop room for several cycles.
        await Task.Delay(TimeSpan.FromMilliseconds(2600));

        Assert.NotEmpty(socket.ReceivedCommands);
        Assert.All(socket.ReceivedCommands, command => Assert.Equal("GetServiceStatus", command));
    }

    [Fact]
    public async Task Disposing_the_shell_host_sends_no_session_mutation()
    {
        await using var socket = new FakeControlSocket();
        var host = new LinuxServiceMonitorHost(socket.Path);

        await host.ConnectAsync(CancellationToken.None);
        await WaitUntil(() => socket.Received("GetServiceStatus"));

        await host.DisposeAsync();

        Assert.DoesNotContain("StartSession", socket.ReceivedCommands);
        Assert.DoesNotContain("StopSession", socket.ReceivedCommands);
        Assert.DoesNotContain("PauseSession", socket.ReceivedCommands);
        Assert.DoesNotContain("FinalizeSession", socket.ReceivedCommands);
    }

    [Fact]
    public async Task Only_an_explicit_user_stop_reaches_the_session()
    {
        await using var socket = new FakeControlSocket();
        await using var host = new LinuxServiceMonitorHost(socket.Path);

        await host.ConnectAsync(CancellationToken.None);

        var started = await host.StartSessionAsync(TimeSpan.FromHours(1), null, CancellationToken.None);
        await host.StopSessionAsync(CancellationToken.None);

        Assert.True(started);
        Assert.Contains("StartSession", socket.ReceivedCommands);
        Assert.Contains("FinalizeSession", socket.ReceivedCommands);
    }

    [Fact]
    public async Task Service_host_declares_itself_a_service_attachment_that_is_not_running_until_told()
    {
        await using var host = new LinuxServiceMonitorHost("/run/internet-evidence-monitor/control.sock");

        Assert.Equal(HostKind.Service, host.Kind);
        Assert.False(host.IsRunning);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "expected condition was not met within the timeout");
    }
}
