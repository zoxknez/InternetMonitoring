using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using IEM.Core.Probes;

namespace IEM.Windows;

/// <param name="Status">The IP_STATUS value the stack returned. 0 is success.</param>
public readonly record struct BoundPingReply(bool Succeeded, TimeSpan RoundTrip, uint Status)
{
    /// <summary>The reply never arrived, as opposed to arriving with an error.</summary>
    public bool TimedOut => Status is IpReqTimedOut;

    internal const uint IpReqTimedOut = 11010;
}

/// <summary>
/// An ICMP echo sent from a chosen source address.
/// <para>
/// The framework's <c>Ping</c> has no way to say which adapter to leave through, so on a
/// machine with Wi-Fi, Ethernet and a VPN it silently measures whichever one the stack
/// prefers. For a tool whose output is meant to prove a fault on one specific link, that is
/// not a limitation but a way of producing confident nonsense - and <c>IcmpSendEcho2Ex</c>
/// takes the source address that fixes it.
/// </para>
/// <para>
/// The underlying call blocks, so each probe runs on a pool thread. That is affordable here
/// only because the probe loops are independent: a blocked ping no longer holds up the
/// sampling tick behind it.
/// </para>
/// </summary>
public sealed class BoundPing : IBoundIcmp
{
    public static readonly BoundPing Instance = new();

    private const int PayloadSize = 32;
    private const int ReplyBufferSize = 512;
    private const uint IpSuccess = 0;

    async Task<IcmpEcho?> IBoundIcmp.SendAsync(
        IPAddress destination,
        IPAddress source,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var reply = await SendAsync(destination, source, timeout, cancellationToken).ConfigureAwait(false);

        return reply is { } value
            ? new IcmpEcho(value.Succeeded, value.TimedOut, value.RoundTrip, value.Status)
            : null;
    }

    /// <summary>
    /// Sends one echo request. Returns null when the platform call cannot be used at all,
    /// so the caller can fall back rather than record a failure that never happened.
    /// </summary>
    public static async Task<BoundPingReply?> SendAsync(
        IPAddress destination,
        IPAddress source,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        if (destination.AddressFamily != AddressFamily.InterNetwork ||
            source.AddressFamily != AddressFamily.InterNetwork)
        {
            // IPv6 goes through Icmp6SendEcho2, which takes a different reply layout. Until
            // that exists, an IPv6 ping is left to the framework and reported as unbound
            // rather than claimed to be bound.
            return null;
        }

        return await Task.Run(() => Send(destination, source, timeout), cancellationToken).ConfigureAwait(false);
    }

    private static BoundPingReply? Send(IPAddress destination, IPAddress source, TimeSpan timeout)
    {
        var handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return null;
        }

        var payload = Marshal.AllocHGlobal(PayloadSize);
        var reply = Marshal.AllocHGlobal(ReplyBufferSize);

        try
        {
            for (var i = 0; i < PayloadSize; i++)
            {
                Marshal.WriteByte(payload, i, (byte)'a');
            }

            var replied = IcmpSendEcho2Ex(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                ToUInt32(source),
                ToUInt32(destination),
                payload,
                PayloadSize,
                IntPtr.Zero,
                reply,
                ReplyBufferSize,
                (uint)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue));

            if (replied == 0)
            {
                // No echo came back. The reason is in GetLastError, and it is carried
                // through rather than flattened: a timeout means the path went silent,
                // while other codes mean the stack would not send at all - which is a local
                // problem and must never be charged to the operator.
                return new BoundPingReply(false, TimeSpan.Zero, (uint)Marshal.GetLastWin32Error());
            }

            // ICMP_ECHO_REPLY: Address (4), Status (4), RoundTripTime (4), ...
            var status = (uint)Marshal.ReadInt32(reply, 4);
            var roundTrip = (uint)Marshal.ReadInt32(reply, 8);

            return new BoundPingReply(status == IpSuccess, TimeSpan.FromMilliseconds(roundTrip), status);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(payload);
            Marshal.FreeHGlobal(reply);
            IcmpCloseHandle(handle);
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);

        // IPAddr is in network byte order, which is exactly the order the bytes come out in.
        return BitConverter.ToUInt32(bytes);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(
        IntPtr icmpHandle,
        IntPtr shellEvent,
        IntPtr apcRoutine,
        IntPtr apcContext,
        uint sourceAddress,
        uint destinationAddress,
        IntPtr requestData,
        short requestSize,
        IntPtr requestOptions,
        IntPtr replyBuffer,
        uint replySize,
        uint timeout);
}
