using System.Net.Http;
using System.Net.Sockets;

namespace IEM.Core.Speed;

/// <summary>
/// The HTTP client a speed measurement runs on, built in one place.
/// <para>
/// It was built by hand at three call sites - the console, the service and the window - which
/// is how the measurement came to observe nothing about its own path: whatever was added in
/// one place had to be remembered in the other two. Here it is one method, and the connect
/// callback that records the sockets comes with it or not at all.
/// </para>
/// <para>
/// No proxy, for the same reason as before: an intercepting proxy silently caps the rate, and
/// a measurement that quietly went through one is worse than none.
/// </para>
/// </summary>
public static class MeasurementHttpClient
{
    /// <summary>
    /// A client whose connections are recorded as they open.
    /// </summary>
    /// <param name="observer">
    /// Told about every socket the client connects. Null for the latency probes, which travel
    /// on their own client and describe nothing about the transfer's path.
    /// </param>
    public static HttpClient Create(ConnectionObserver? observer = null)
    {
        var handler = new SocketsHttpHandler { UseProxy = false };

        if (observer is not null)
        {
            handler.ConnectCallback = (context, cancellationToken) => ConnectAsync(observer, context, cancellationToken);
        }

        return new HttpClient(handler);
    }

    /// <summary>
    /// Opens the connection the way the handler would, and records where it went.
    /// <para>
    /// Nothing is bound and nothing is forced: the question this answers is which way the
    /// system chooses, so the socket is left to the ordinary route lookup and only observed
    /// afterwards. Binding it to a chosen adapter is a different measurement with a different
    /// meaning, and it comes separately.
    /// </para>
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(
        ConnectionObserver observer,
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        // Delay is disabled for the same reason the default handler disables it: a transfer
        // measured through Nagle's algorithm is measuring the algorithm.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);

            // After the connect, because before it there is no local endpoint to read.
            observer.Record(socket.LocalEndPoint, socket.RemoteEndPoint);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
