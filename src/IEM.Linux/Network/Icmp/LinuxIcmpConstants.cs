namespace IEM.Linux.Network.Icmp;

/// <summary>
/// ICMPv4 and ICMPv6 protocol constants for Linux unprivileged datagram sockets.
/// Invariants 271-275.
/// </summary>
public static class LinuxIcmpConstants
{
    // ICMPv4 Message Types (RFC 792)
    public const byte IcmpV4EchoRequest = 8;
    public const byte IcmpV4EchoReply = 0;

    // ICMPv6 Message Types (RFC 4443)
    public const byte IcmpV6EchoRequest = 128;
    public const byte IcmpV6EchoReply = 129;

    public const byte IcmpCodeEcho = 0;

    public const int IcmpHeaderSize = 8;
    public const int NonceSize = 8;
    public const int TimestampSize = 8;
    public const int DefaultPayloadSize = NonceSize + TimestampSize; // 16 bytes
    public const int TotalEchoPacketSize = IcmpHeaderSize + DefaultPayloadSize; // 24 bytes

    public const int ReceiveBufferSize = 512;
}
