using IEM.Storage;

namespace IEM.Linux.Installation;

/// <summary>
/// Authoritative source for Linux service IPC transport reachability and protocol verification truth.
/// Invariant 8E-J: Reachability is derived independently from control socket connectivity and protocol handshake.
/// </summary>
public interface ILinuxServiceReachabilitySource
{
    ServiceReachability ProbeReachability();
    Task<ServiceReachability> ProbeReachabilityAsync(CancellationToken ct = default);
}
