namespace IEM.Service.Linux.Lifecycle.Logind;

/// <summary>
/// Abstraction for observing systemd-logind sleep signals over D-Bus.
/// Decouples LinuxLogindPowerSource from concrete D-Bus wire protocol for testability.
/// </summary>
internal interface ILogindSignalTransport : IAsyncDisposable
{
    Task ObservePrepareForSleepAsync(
        Func<bool, ValueTask> handler,
        CancellationToken cancellationToken);
}
