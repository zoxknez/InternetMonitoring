using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace IEM.Service;

/// <summary>
/// The standard Windows service lifetime, extended to listen for power events.
/// <para>
/// The default lifetime does not opt into power notifications, so a service using it
/// cannot tell a sleeping machine from a stalled one. Subclassing is the supported way to
/// get at <see cref="ServiceBase.OnPowerEvent"/>, and it costs one override.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IemWindowsServiceLifetime : WindowsServiceLifetime
{
    private readonly PowerEventBroker _powerEvents;

    public IemWindowsServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptions,
        PowerEventBroker powerEvents)
        : base(environment, applicationLifetime, loggerFactory, hostOptions, windowsServiceOptions)
    {
        _powerEvents = powerEvents;

        // Without this the service control manager never delivers power notifications,
        // and the override below would sit there doing nothing.
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
    }

    /// <summary>
    /// Reports the process exit code to the service control manager on the way out.
    /// <para>
    /// The manager does not read <see cref="Environment.ExitCode"/>; it reads what the
    /// service reports when it stops. Without this bridge a fatal engine error would set an
    /// exit code that nothing ever looked at, the stop would count as normal, and the
    /// restart action configured at install time would never fire - leaving a two-day test
    /// dead at hour thirty with nothing to bring it back.
    /// </para>
    /// </summary>
    protected override void OnStop()
    {
        if (Environment.ExitCode != 0)
        {
            ExitCode = Environment.ExitCode;
        }

        base.OnStop();
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _powerEvents.RaiseSuspending();
                break;

            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeCritical:
                _powerEvents.RaiseResumed();
                break;

            default:
                break;
        }

        // Never veto a suspend. Blocking the machine from sleeping to protect a
        // measurement would be the tool interfering with the thing it is measuring, and
        // an unexpected refusal to sleep is a far worse surprise than a recorded gap.
        return true;
    }
}
