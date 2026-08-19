using System.Buffers.Binary;

namespace IEM.Linux.Network.Icmp;

/// <summary>
/// Packet serializer and validator for unprivileged datagram ICMP echo requests and replies.
/// Invariants 271-275.
/// </summary>
public static class LinuxIcmpPacket
{
    public static byte[] BuildEchoRequest(bool isV6, ushort sequence, ulong nonce, long timestampTicks)
    {
        var packet = new byte[LinuxIcmpConstants.TotalEchoPacketSize];

        // 1. Header (8 bytes)
        packet[0] = isV6 ? LinuxIcmpConstants.IcmpV6EchoRequest : LinuxIcmpConstants.IcmpV4EchoRequest;
        packet[1] = LinuxIcmpConstants.IcmpCodeEcho;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0); // Checksum (kernel calculates for DGRAM)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 0); // Identifier (kernel assigns for DGRAM)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), sequence);

        // 2. Payload Nonce (8 bytes) + Timestamp (8 bytes)
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8, 8), nonce);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(16, 8), timestampTicks);

        return packet;
    }

    public static bool TryValidateEchoReply(
        ReadOnlySpan<byte> buffer,
        bool isV6,
        ushort expectedSequence,
        ulong expectedNonce)
    {
        if (buffer.Length < LinuxIcmpConstants.TotalEchoPacketSize)
        {
            return false;
        }

        var type = buffer[0];
        var code = buffer[1];

        var expectedType = isV6
            ? LinuxIcmpConstants.IcmpV6EchoReply
            : LinuxIcmpConstants.IcmpV4EchoReply;

        if (type != expectedType || code != LinuxIcmpConstants.IcmpCodeEcho)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(6, 2));
        if (sequence != expectedSequence)
        {
            return false;
        }

        var nonce = BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(8, 8));
        if (nonce != expectedNonce)
        {
            return false;
        }

        return true;
    }
}
