using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.UniversalHubs;

/// <summary>
/// A host found over BLE, Wi-Fi or both. <see cref="UniversalHubClient{TContract}.DiscoverAll"/> raises a fresh snapshot
/// whenever either side changes; when <see cref="Transports"/> is None the host has gone.
/// </summary>
/// <param name="Key">Stable for one host across both transports - its advertised name</param>
/// <param name="Name">The host's name</param>
/// <param name="HostId">From mDNS, when the host has been seen on Wi-Fi</param>
/// <param name="Ble">Set while the host is seen over BLE</param>
/// <param name="Http">Set while the host is seen over mDNS</param>
public sealed record HubHostInfo(
    string Key,
    string Name,
    string? HostId,
    BleHubHostInfo? Ble,
    DiscoveredHttpServer? Http
)
{
    public HubTransports Transports =>
        (this.Ble != null ? HubTransports.Ble : HubTransports.None)
        | (this.Http != null ? HubTransports.Http : HubTransports.None);

    /// <summary>
    /// Signal strength, when seen over BLE
    /// </summary>
    public int? Rssi => this.Ble?.Rssi;


    /// <summary>
    /// A host you have the address of rather than discovered - a tunnel's public URL, a typed address, a QR code.
    /// Pass it to <see cref="IUniversalHubClient{TContract}.Connect"/>; it is reached over HTTP only.
    /// </summary>
    /// <param name="serverUrl">The server's address, eg. <c>https://abc.a.pinggy.link</c> or <c>http://192.168.1.20:5000</c></param>
    /// <param name="endpoint">The server's base path - AddUniversalServer's Endpoint, <c>/hubs</c> unless it was changed</param>
    public static HubHostInfo FromUrl(Uri serverUrl, string endpoint = UniversalHubProtocol.DefaultBasePath)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        if (!serverUrl.IsAbsoluteUri || (serverUrl.Scheme != Uri.UriSchemeHttp && serverUrl.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("An absolute http or https address is required", nameof(serverUrl));

        var root = serverUrl.GetLeftPart(UriPartial.Authority);
        var baseAddress = new Uri(root + UniversalHubProtocol.NormalizeBasePath(endpoint));
        return new HubHostInfo(
            root,
            serverUrl.Host,
            null,
            null,
            new DiscoveredHttpServer(serverUrl.Host, baseAddress, [], serverUrl.Port, new Dictionary<string, string>())
        );
    }
}


/// <summary>
/// What the client tells the host about itself, and which transports it may use
/// </summary>
public sealed record HubConnectOptions(
    string? Name = null,
    string? AppVersion = null,
    Dictionary<string, string>? Properties = null
)
{
    /// <summary>
    /// Tried first when the host is reachable both ways. Wi-Fi by default: more range, more bandwidth, no MTU.
    /// </summary>
    public HubTransports Prefer { get; init; } = HubTransports.Http;

    /// <summary>
    /// Transports that may be used at all - the other one is the fallback when the preferred one fails
    /// </summary>
    public HubTransports Allowed { get; init; } = HubTransports.All;

    internal BleHubConnectOptions ToBle() => new(this.Name, this.AppVersion, this.Properties);
}


public class UniversalHubClientOptions
{
    /// <summary>
    /// The BLE service the host serves its hubs under - must match the host's (AddUniversalServer's ServiceUuid)
    /// </summary>
    public string ServiceUuid { get; set; } = BleHubProtocolOptions.DefaultServiceUuid;

    /// <summary>
    /// The mDNS service type to browse - must match the host's
    /// </summary>
    public string ServiceType { get; set; } = UniversalHubProtocol.ServiceType;

    /// <summary>
    /// How long one Wi-Fi address gets to answer before the next address (or BLE) is tried
    /// </summary>
    public TimeSpan HttpConnectTimeout { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// When none of a discovered host's addresses answer, how long to spend looking it up again over mDNS - a host that
    /// restarted (or was killed without a goodbye) is on a new port. Zero turns this off.
    /// </summary>
    public TimeSpan HttpRediscoverTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Resume the Wi-Fi line after a short drop instead of disconnecting - the host keeps the client, its groups and
    /// replays missed pushes
    /// </summary>
    public bool AutomaticReconnect { get; set; } = true;

    /// <summary>
    /// The HTTP stack for Wi-Fi connections - tests swap in an in-memory handler
    /// </summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }
}
