using System.Net;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network.Icmp;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Linux unprivileged datagram ICMP framing, correlation, and failure semantics.
/// Invariants 271-275 (Phase 3.1-4C).
/// </summary>
public sealed class LinuxBoundIcmpTests
{
    [Fact]
    public void BuildEchoRequest_creates_valid_IPv4_packet()
    {
        var seq = (ushort)42;
        var nonce = 0x1122334455667788UL;
        var timestamp = 123456789L;

        var packet = LinuxIcmpPacket.BuildEchoRequest(isV6: false, seq, nonce, timestamp);

        Assert.Equal(LinuxIcmpConstants.TotalEchoPacketSize, packet.Length);
        Assert.Equal(LinuxIcmpConstants.IcmpV4EchoRequest, packet[0]); // Type 8
        Assert.Equal(0, packet[1]); // Code 0

        // Validate that our validator accepts its own formatted reply
        packet[0] = LinuxIcmpConstants.IcmpV4EchoReply; // Convert to Type 0 Reply
        var isValid = LinuxIcmpPacket.TryValidateEchoReply(packet, isV6: false, seq, nonce);
        Assert.True(isValid);
    }

    [Fact]
    public void BuildEchoRequest_creates_valid_IPv6_packet()
    {
        var seq = (ushort)999;
        var nonce = 0xAABBCCDDEEFF0011UL;
        var timestamp = 987654321L;

        var packet = LinuxIcmpPacket.BuildEchoRequest(isV6: true, seq, nonce, timestamp);

        Assert.Equal(LinuxIcmpConstants.TotalEchoPacketSize, packet.Length);
        Assert.Equal(LinuxIcmpConstants.IcmpV6EchoRequest, packet[0]); // Type 128
        Assert.Equal(0, packet[1]); // Code 0

        // Validate that our validator accepts its own formatted reply
        packet[0] = LinuxIcmpConstants.IcmpV6EchoReply; // Convert to Type 129 Reply
        var isValid = LinuxIcmpPacket.TryValidateEchoReply(packet, isV6: true, seq, nonce);
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateEchoReply_rejects_mismatched_sequence_or_nonce()
    {
        var seq = (ushort)10;
        var nonce = 0x12345678UL;
        var packet = LinuxIcmpPacket.BuildEchoRequest(isV6: false, seq, nonce, 0);
        packet[0] = LinuxIcmpConstants.IcmpV4EchoReply;

        // Sequence mismatch
        Assert.False(LinuxIcmpPacket.TryValidateEchoReply(packet, isV6: false, expectedSequence: 11, expectedNonce: nonce));

        // Nonce mismatch
        Assert.False(LinuxIcmpPacket.TryValidateEchoReply(packet, isV6: false, expectedSequence: seq, expectedNonce: 0x99999999UL));

        // Wrong IP version reply type (e.g. IPv6 reply received on IPv4 socket)
        Assert.False(LinuxIcmpPacket.TryValidateEchoReply(packet, isV6: true, expectedSequence: seq, expectedNonce: nonce));
    }

    [Fact]
    public void ValidateEchoReply_rejects_truncated_buffer()
    {
        var tinyBuffer = new byte[12];
        Assert.False(LinuxIcmpPacket.TryValidateEchoReply(tinyBuffer, isV6: false, 1, 1));
    }

    [Fact]
    public async Task SendAsync_returns_non_null_echo_with_skipped_semantics_on_local_unavailability()
    {
        var icmp = LinuxBoundIcmp.Instance;
        var dest = IPAddress.Parse("8.8.8.8");
        var src = IPAddress.Parse("192.168.1.50");

        var result = await icmp.SendAsync(dest, src, TimeSpan.FromMilliseconds(100));

        // CRITICAL INVARIANT: Result MUST NOT be null (null would trigger fallback Ping in Core)
        Assert.NotNull(result);
        var echo = result.Value;

        // On non-Linux or without network connection, it returns Succeeded=false, TimedOut=false
        Assert.False(echo.Succeeded);
        Assert.False(echo.TimedOut);
        Assert.Equal(TimeSpan.Zero, echo.RoundTrip);
    }
}
