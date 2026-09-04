using IEM.App.Linux.Hosting;
using IEM.Presentation.Hosting;

namespace IEM.App.Linux.Tests;

/// <summary>
/// 3.1-10 gate: "Portable process exit ends observability and is not an outage."
/// Tearing the portable host down - the process-exit path - must finish quietly. It is the
/// end of watching, never a recorded fault or a network event.
/// </summary>
public sealed class LinuxPortableMonitorHostGateTests
{
    [Fact]
    public async Task Tearing_down_a_portable_host_that_never_ran_raises_no_fault()
    {
        if (!OperatingSystem.IsLinux())
        {
            // The portable composition resolves a real euid/egid through libc; only meaningful
            // on the Linux lane. The service-host gate covers the cross-platform contract.
            return;
        }

        string? observedFault = null;
        var host = new LinuxPortableMonitorHost();
        host.FaultChanged += fault => observedFault = fault;

        Assert.Equal(HostKind.InProcess, host.Kind);
        Assert.False(host.IsRunning);

        // Stop before start is a no-op, and disposing is the process-exit path.
        await host.StopSessionAsync(CancellationToken.None);
        await host.DisposeAsync();

        Assert.Null(observedFault);
        Assert.False(host.IsRunning);
    }
}
