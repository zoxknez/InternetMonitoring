namespace IEM.Linux.Network.Preflight;

/// <summary>
/// Status of an individual Linux kernel/network capability.
/// Invariants 271-275: Fine-grained, non-global capability states.
/// </summary>
public enum LinuxCapabilityState
{
    Available,
    Unavailable,
    Unsupported,
    Unknown
}

/// <summary>
/// Granular observation of a specific network capability with native error details.
/// </summary>
public sealed record LinuxCapabilityObservation(
    LinuxCapabilityState State,
    int? NativeError = null,
    string? SocketError = null,
    string? Diagnostic = null)
{
    public bool IsAvailable => State == LinuxCapabilityState.Available;
}
