using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.UniversalHubs;

/// <summary>
/// A host found over BLE, Wi-Fi, Multipeer or several of them. <see cref="UniversalHubClient{TContract}.DiscoverAll"/>
/// raises a fresh snapshot whenever any side changes; when <see cref="Transports"/> is None the host has gone.
/// </summary>
/// <param name="Key">Stable for one host across every transport - its advertised name</param>
/// <param name="Name">The host's name</param>
/// <param name="HostId">From mDNS or Multipeer, when the host has been seen there</param>
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
    /// <summary>
    /// Set while the host is seen over MultipeerConnectivity (Shiny.UniversalHubs.Multipeer)
    /// </summary>
    public MultipeerHost? Multipeer { get; init; }

    public HubTransports Transports =>
        (this.Ble != null ? HubTransports.Ble : HubTransports.None)
        | (this.Http != null ? HubTransports.Http : HubTransports.None)
        | (this.Multipeer != null ? HubTransports.Multipeer : HubTransports.None);

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
    /// Tried first when the host is reachable more than one way. Wi-Fi by default: more range, more bandwidth, no MTU.
    /// The rest follow in the order Wi-Fi, Multipeer, BLE.
    /// </summary>
    public HubTransports Prefer { get; init; } = HubTransports.Http;

    /// <summary>
    /// Transports that may be used at all - the others are the fallback when the preferred one fails
    /// </summary>
    public HubTransports Allowed { get; init; } = HubTransports.All;

    /// <summary>
    /// The order transports are tried in: <see cref="Prefer"/>, then Wi-Fi, Multipeer and BLE. Wi-Fi comes first among the
    /// rest because Android peers can use it too; Multipeer beats BLE because it is Wi-Fi fast with no network at all.
    /// </summary>
    internal IReadOnlyList<HubTransports> Order
    {
        get
        {
            HubTransports[] order = [HubTransports.Http, HubTransports.Multipeer, HubTransports.Ble];
            return order.Contains(this.Prefer)
                ? [this.Prefer, .. order.Where(x => x != this.Prefer)]
                : order;
        }
    }

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

    /// <summary>
    /// Transports beyond BLE and HTTP, from add-on packages (Shiny.UniversalHubs.Multipeer's <c>o.Multipeer(...)</c>)
    /// </summary>
    internal List<Func<IServiceProvider, IUniversalHubClientTransport>> Transports { get; } = new();

    /// <summary>
    /// Registrations those add-on transports need
    /// </summary>
    internal List<Action<IServiceCollection>> ServiceActions { get; } = new();
}


/// <summary>
/// A host seen over MultipeerConnectivity. The peer itself stays inside Shiny.UniversalHubs.Multipeer.
/// </summary>
/// <param name="DisplayName">The host's Multipeer display name</param>
/// <param name="Info">What it advertises - the same keys as its mDNS TXT record (<see cref="UniversalHubProtocol.TxtHubs"/>, ...)</param>
public sealed record MultipeerHost(string DisplayName, IReadOnlyDictionary<string, string> Info)
{
    /// <summary>
    /// The native peer (an MCPeerID) - only the Multipeer package knows what to do with it
    /// </summary>
    internal object? Peer { get; init; }
}


/// <summary>
/// A transport beyond BLE and HTTP that a universal hub client can discover hosts over and connect through
/// </summary>
internal interface IUniversalHubClientTransport
{
    HubTransports Transport { get; }

    /// <summary>
    /// Hosts of <paramref name="client"/>'s hub as they come and go. Errors end only this transport's discovery.
    /// </summary>
    IObservable<HubHostSighting> Discover(BleHubClient client);

    /// <summary>
    /// Connects <paramref name="client"/> to <paramref name="host"/> - through <see cref="BleHubClient.ConnectExternal"/>
    /// </summary>
    Task Connect(BleHubClient client, HubHostInfo host, BleHubConnectOptions options, CancellationToken cancellationToken);
}


/// <summary>
/// A host appearing on, changing on, or leaving one transport
/// </summary>
/// <param name="Name">The host's name - how it is merged with what the other transports see</param>
/// <param name="HostId">The host's id, when the transport knows it</param>
/// <param name="Apply">Puts what this transport sees into the merged host: sets its endpoint when found, clears it when lost</param>
internal sealed record HubHostSighting(string Name, string? HostId, Func<HubHostInfo, HubHostInfo> Apply);


/// <summary>
/// Implemented by each external client transport, so the client can say which one a connection is on
/// </summary>
internal interface IHubTransportKind
{
    HubTransports Transport { get; }
}
