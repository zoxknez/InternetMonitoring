namespace IEM.Linux.Network.Icmp;

/// <summary>
/// Status codes reported in IcmpEcho.Status for raw logging, diagnostics, and probe outcome derivation.
/// Invariants 271-275: Local capability denial is strictly differentiated from network timeout.
/// </summary>
public static class LinuxIcmpStatus
{
    public const uint Success = 0;
    public const uint TimedOut = 11010;

    public const uint SocketCreateFailed = 20001;
    public const uint BindFailed = 20002;
    public const uint SendFailed = 20003;
    public const uint LocalCapabilityDenied = 20004; // EPERM / EACCES
    public const uint AddressNotAvailable = 20005;   // EADDRNOTAVAIL
    public const uint MalformedReply = 20006;
    public const uint UnspecifiedLocalError = 20099;
}
