using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.SwitchboardR;

/// <summary>
/// A host found over BLE, Wi-Fi or both. <see cref="SwitchboardRClient{TContract}.DiscoverAll"/> raises a fresh snapshot
/// whenever either side changes; when <see cref="Transports"/> is None the host has gone.
/// </summary>
/// <param name="Key">Stable for one host across both transports - its advertised name</param>
/// <param name="Name">The host's name</param>
/// <param name="HostId">From mDNS, when the host has been seen on Wi-Fi</param>
/// <param name="Ble">Set while the host is seen over BLE</param>
/// <param name="Wifi">Set while the host is seen over mDNS</param>
public sealed record HubHostInfo(
    string Key,
    string Name,
    string? HostId,
    BleHubHostInfo? Ble,
    DiscoveredHttpServer? Wifi
)
{
    public HubTransports Transports =>
        (this.Ble != null ? HubTransports.Ble : HubTransports.None)
        | (this.Wifi != null ? HubTransports.Wifi : HubTransports.None);

    /// <summary>
    /// Signal strength, when seen over BLE
    /// </summary>
    public int? Rssi => this.Ble?.Rssi;
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
    public HubTransports Prefer { get; init; } = HubTransports.Wifi;

    /// <summary>
    /// Transports that may be used at all - the other one is the fallback when the preferred one fails
    /// </summary>
    public HubTransports Allowed { get; init; } = HubTransports.All;

    internal BleHubConnectOptions ToBle() => new(this.Name, this.AppVersion, this.Properties);
}


public class SwitchboardRClientOptions
{
    /// <summary>
    /// The mDNS service type to browse - must match the host's
    /// </summary>
    public string ServiceType { get; set; } = SwitchboardRProtocol.ServiceType;

    /// <summary>
    /// How long one Wi-Fi address gets to answer before the next address (or BLE) is tried
    /// </summary>
    public TimeSpan WifiConnectTimeout { get; set; } = TimeSpan.FromSeconds(6);

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
