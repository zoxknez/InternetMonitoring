using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace IEM.Core.Probes;

/// <param name="ConnectionStatus">Router's own word for the WAN link: Connected, Disconnected, Connecting.</param>
/// <param name="LastError">Why the WAN connection last failed, in the router's vocabulary.</param>
/// <param name="Uptime">How long the WAN connection has been up. A reset means the router reconnected.</param>
public sealed record WanStatus(
    string? ConnectionStatus,
    string? LastError,
    TimeSpan? Uptime,
    string? ExternalAddress)
{
    public bool IsConnected =>
        string.Equals(ConnectionStatus, "Connected", StringComparison.OrdinalIgnoreCase);

    public bool IsDisconnected =>
        string.Equals(ConnectionStatus, "Disconnected", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Asks the router about its own WAN connection, over UPnP.
/// <para>
/// This is the only evidence in the whole tool that comes from the other side of the
/// link. Everything else describes what the computer can see; this describes what the
/// router says about itself - whether its WAN connection is up, why it last dropped, and
/// how long it has been established. A WAN uptime that resets during an outage means the
/// router reconnected, which separates a line fault from a router that rebooted itself.
/// </para>
/// <para>
/// The external address matters for the same reason and comes for free here: asked of the
/// router rather than of some website, so it works during an outage and nothing about the
/// customer's connection leaves the house to obtain it.
/// </para>
/// <para>
/// Plenty of routers do not expose this, and some expose it wrongly. Every failure returns
/// <see langword="null"/> so the confidence score can record "could not check" rather than
/// treat silence as an answer.
/// </para>
/// </summary>
public sealed class UpnpGateway
{
    private const string SsdpAddress = "239.255.255.250";
    private const int SsdpPort = 1900;

    private static readonly string[] ConnectionServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
        "urn:schemas-upnp-org:service:WANIPConnection:2",
    ];

    private readonly HttpClient _http;

    private Uri? _controlUrl;
    private string? _serviceType;
    private bool _discoveryFailed;
    private int _discoveryFailures;

    /// <summary>Consecutive failed discoveries before the channel is given up for the session.</summary>
    private const int DiscoveryAttemptLimit = 3;

    /// <summary>At most this many device descriptions are fetched per discovery.</summary>
    private const int MaxLocationFetches = 5;

    /// <summary>The whole location-fetch phase is bounded, whatever the LAN replies with.</summary>
    private static readonly TimeSpan DiscoveryFetchBudget = TimeSpan.FromSeconds(8);

    public UpnpGateway(HttpClient? httpClient = null) =>
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>True once a gateway has been found and its control endpoint resolved.</summary>
    public bool IsAvailable => _controlUrl is not null;

    /// <summary>
    /// Reads the router's WAN status, discovering the gateway first if needed.
    /// Returns <see langword="null"/> when the router does not expose UPnP.
    /// </summary>
    public async Task<WanStatus?> ReadAsync(CancellationToken cancellationToken)
    {
        if (_controlUrl is null)
        {
            if (_discoveryFailed)
            {
                return null;
            }

            if (!await DiscoverAsync(cancellationToken).ConfigureAwait(false))
            {
                // Shutting down is not a failed discovery: the next start gets a fresh
                // chance, and latching here would disable the router-status channel on a
                // service that was merely stopping.
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                // A few consecutive failures before giving up for the session. One was
                // enough before, and one transient timeout - a busy router, a flap, most
                // likely exactly when the connection is degrading - permanently silenced
                // the one channel that distinguishes a line fault from a router reboot.
                if (++_discoveryFailures >= DiscoveryAttemptLimit)
                {
                    _discoveryFailed = true;
                }

                return null;
            }

            _discoveryFailures = 0;
        }

        var status = await InvokeAsync("GetStatusInfo", cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

        var external = await InvokeAsync("GetExternalIPAddress", cancellationToken).ConfigureAwait(false);

        return new WanStatus(
            Read(status, "NewConnectionStatus"),
            Read(status, "NewLastConnectionError"),
            ReadSeconds(status, "NewUptime"),
            external is null ? null : Read(external, "NewExternalIPAddress"));
    }

    /// <summary>Sends an SSDP search and resolves the first gateway that answers.</summary>
    private async Task<bool> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            var locations = await SearchAsync(cancellationToken).ConfigureAwait(false);

            // Deduplicated and capped. Anything on the LAN can answer an SSDP search, and
            // walking every reply sequentially - each with its own HTTP timeout - let a
            // misbehaving peer stall this loop for minutes and point the monitor's HTTP at
            // attacker-chosen URLs.
            var budget = DateTime.UtcNow + DiscoveryFetchBudget;

            foreach (var location in locations.Distinct().Take(MaxLocationFetches))
            {
                if (DateTime.UtcNow >= budget)
                {
                    return false;
                }

                if (await ResolveControlUrlAsync(location, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or TaskCanceledException)
        {
            return false;
        }

        return false;
    }

    private static async Task<IReadOnlyList<Uri>> SearchAsync(CancellationToken cancellationToken)
    {
        const string request =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var target = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);
        await socket.SendToAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None, target, cancellationToken)
            .ConfigureAwait(false);

        var locations = new List<Uri>();
        var buffer = new byte[4096];

        // Bounded by MX plus a margin. Routers answer within a second or two; waiting
        // longer would stall a discovery that already has its answer.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, target, deadline.Token)
                    .ConfigureAwait(false);

                var response = Encoding.ASCII.GetString(buffer, 0, received.ReceivedBytes);

                if (TryReadHeader(response, "LOCATION", out var location) &&
                    Uri.TryCreate(location, UriKind.Absolute, out var uri))
                {
                    locations.Add(uri);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the search window closed.
        }

        return locations;
    }

    /// <summary>Fetches the device description and finds the WAN connection service inside it.</summary>
    private async Task<bool> ResolveControlUrlAsync(Uri location, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await _http.GetStringAsync(location, cancellationToken).ConfigureAwait(false);
            var document = XDocument.Parse(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";

            foreach (var serviceType in ConnectionServiceTypes)
            {
                var service = document.Descendants(ns + "service")
                    .FirstOrDefault(s => string.Equals(
                        s.Element(ns + "serviceType")?.Value, serviceType, StringComparison.Ordinal));

                var controlPath = service?.Element(ns + "controlURL")?.Value;
                if (string.IsNullOrWhiteSpace(controlPath))
                {
                    continue;
                }

                // Control URLs are usually relative to the description's own location.
                _controlUrl = new Uri(location, controlPath);
                _serviceType = serviceType;
                return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return false;
        }

        return false;
    }

    private async Task<XElement?> InvokeAsync(string action, CancellationToken cancellationToken)
    {
        if (_controlUrl is null || _serviceType is null)
        {
            return null;
        }

        var envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:{action} xmlns:u=\"{_serviceType}\"></u:{action}>" +
            "</s:Body></s:Envelope>";

        try
        {
            using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", $"\"{_serviceType}#{action}\"");

            using var response = await _http.PostAsync(_controlUrl, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return XDocument.Parse(body).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == $"{action}Response");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string? Read(XElement response, string name) =>
        response.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static TimeSpan? ReadSeconds(XElement response, string name) =>
        long.TryParse(Read(response, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, (long)TimeSpan.MaxValue.TotalSeconds))
            : null;

    private static bool TryReadHeader(string response, string header, out string value)
    {
        foreach (var line in response.Split('\n'))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            if (line.AsSpan(0, separator).Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
            {
                value = line[(separator + 1)..].Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
