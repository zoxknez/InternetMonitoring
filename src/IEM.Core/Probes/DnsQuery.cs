using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IEM.Core.Probes;

/// <param name="AnswerCount">Number of records returned. Zero with <see cref="ResponseCode"/> 0 means the name has no A record.</param>
/// <param name="ResponseCode">DNS RCODE. 0 is success, 2 is server failure, 3 is name not found.</param>
public readonly record struct DnsQueryResult(
    bool Succeeded,
    TimeSpan Elapsed,
    int AnswerCount,
    int ResponseCode,
    string? Error,
    bool BindFailed = false);

/// <summary>
/// A minimal DNS/UDP client that asks one specific resolver.
/// <para>
/// The framework resolver only ever uses whatever Windows is configured with, which
/// cannot answer the question that actually matters: is the operator's resolver broken,
/// or is DNS broken everywhere? Those are different faults with different remedies, and
/// telling them apart needs a query aimed at a resolver we choose. Hence roughly sixty
/// lines of wire format rather than a dependency.
/// </para>
/// </summary>
public static class DnsQuery
{
    private const int DnsPort = 53;
    private const int MaxResponseSize = 512;
    private const ushort RecursionDesired = 0x0100;
    private const ushort TypeA = 1;
    private const ushort ClassIn = 1;

    /// <param name="source">
    /// Source address to send from, so the query provably left through the monitored link.
    /// Null lets the stack choose, which costs the ability to attribute the result.
    /// </param>
    public static async Task<DnsQueryResult> ResolveAsync(
        string resolver,
        string name,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IPAddress? source = null)
    {
        if (!IPAddress.TryParse(resolver, out var resolverAddress))
        {
            return new DnsQueryResult(false, TimeSpan.Zero, 0, -1, "Unparseable resolver address");
        }

        // A fresh transaction id per query, so a late reply to an earlier question can
        // never be mistaken for an answer to this one.
        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);

        byte[] request;
        try
        {
            request = BuildQuery(transactionId, name);
        }
        catch (ArgumentException ex)
        {
            return new DnsQueryResult(false, TimeSpan.Zero, 0, -1, ex.Message);
        }

        using var socket = new Socket(resolverAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var endpoint = new IPEndPoint(resolverAddress, DnsPort);
        var started = Stopwatch.GetTimestamp();

        try
        {
            if (source is not null && source.AddressFamily == resolverAddress.AddressFamily)
            {
                // Forces the query out of the monitored adapter. Without it the claim
                // "the operator's resolver is down while a public one works" rests on two
                // queries that may have left the machine by different routes entirely.
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                }
                catch (SocketException ex)
                {
                    return new DnsQueryResult(
                        false,
                        TimeSpan.Zero,
                        0,
                        -1,
                        $"LocalBindFailed:{ex.SocketErrorCode}",
                        BindFailed: true);
                }
            }

            await socket.SendToAsync(request, SocketFlags.None, endpoint, timeoutSource.Token).ConfigureAwait(false);

            var buffer = new byte[MaxResponseSize];
            var received = await socket
                .ReceiveFromAsync(buffer, SocketFlags.None, endpoint, timeoutSource.Token)
                .ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(started);
            return Interpret(buffer.AsSpan(0, received.ReceivedBytes), transactionId, elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DnsQueryResult(false, Stopwatch.GetElapsedTime(started), 0, -1, "Timed out");
        }
        catch (SocketException ex)
        {
            return new DnsQueryResult(false, Stopwatch.GetElapsedTime(started), 0, -1, ex.SocketErrorCode.ToString());
        }
    }

    private static byte[] BuildQuery(ushort transactionId, string name)
    {
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0)
        {
            throw new ArgumentException("Empty DNS name.", nameof(name));
        }

        // 12-byte header, then each label as one length byte plus its bytes, then the
        // root terminator, then QTYPE and QCLASS.
        var size = 12 + labels.Sum(l => l.Length + 1) + 1 + 4;
        var buffer = new byte[size];

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), RecursionDesired);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), 1); // one question
        // Answer, authority and additional counts stay zero.

        var offset = 12;
        foreach (var label in labels)
        {
            if (label.Length > 63)
            {
                throw new ArgumentException($"DNS label '{label}' exceeds 63 bytes.", nameof(name));
            }

            buffer[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, buffer.AsSpan(offset));
        }

        buffer[offset++] = 0; // root label
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 2), ClassIn);

        return buffer;
    }

    private static DnsQueryResult Interpret(ReadOnlySpan<byte> response, ushort expectedId, TimeSpan elapsed)
    {
        if (response.Length < 12)
        {
            return new DnsQueryResult(false, elapsed, 0, -1, "Truncated response");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(response) != expectedId)
        {
            return new DnsQueryResult(false, elapsed, 0, -1, "Transaction id mismatch");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        var responseCode = flags & 0x000F;
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);

        // The answer has to be an answer. A query echoed back - QR clear - is not one,
        // whatever the rest of the header claims.
        if ((flags & 0x8000) == 0)
        {
            return new DnsQueryResult(false, elapsed, 0, responseCode, "Not a response (QR clear)");
        }

        // And it has to contain a real address record. The header's ANCOUNT used to be
        // trusted on its own, so a resolver replying "no error, five hundred answers" with
        // an empty body was recorded as success - a fabricated healthy signal in the middle
        // of an outage, the same failure class as the stale-cache bug this file's history
        // already fixed once.
        if (!TryCountAddressRecords(response, answerCount, out var realAnswers))
        {
            return new DnsQueryResult(false, elapsed, 0, responseCode, "Malformed answer section");
        }

        // A resolver that answers at all is reachable. Whether it answers correctly is a
        // separate matter, so both facts are reported rather than collapsed into one.
        var succeeded = responseCode == 0 && realAnswers > 0;
        var error = succeeded ? null : $"RCODE {responseCode}, {realAnswers} address records";

        return new DnsQueryResult(succeeded, elapsed, realAnswers, responseCode, error);
    }

    /// <summary>
    /// Walks the answer section and counts records that actually hold an address, rather
    /// than believing the count in the header.
    /// </summary>
    private static bool TryCountAddressRecords(ReadOnlySpan<byte> response, int claimedAnswers, out int addressRecords)
    {
        addressRecords = 0;

        var offset = 12;

        // The question section first: its name, then QTYPE and QCLASS.
        if (!TrySkipName(response, ref offset))
        {
            return false;
        }

        offset += 4;

        for (var i = 0; i < claimedAnswers; i++)
        {
            if (!TrySkipName(response, ref offset) || offset + 10 > response.Length)
            {
                return false;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);

            // TYPE, CLASS, TTL, RDLENGTH.
            offset += 8;
            var length = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            offset += 2;

            if (offset + length > response.Length)
            {
                return false;
            }

            if (type is 1 or 28)
            {
                addressRecords++;
            }

            offset += length;
        }

        return true;
    }

    /// <summary>Skips a DNS name, following the compression pointer that ends it when present.</summary>
    private static bool TrySkipName(ReadOnlySpan<byte> response, ref int offset)
    {
        while (true)
        {
            if (offset >= response.Length)
            {
                return false;
            }

            var label = response[offset];

            if (label == 0)
            {
                offset++;
                return true;
            }

            // A compression pointer is two bytes and always ends the name.
            if ((label & 0xC0) == 0xC0)
            {
                offset += 2;
                return offset <= response.Length;
            }

            offset += 1 + label;
        }
    }
}
