using Shiny.BluetoothLE;

namespace Shiny.UniversalHubs;

/// <summary>
/// HTTP hosting settings - the Switchboard routes, mDNS and the server's lifetime
/// </summary>
public class HttpTransportOptions
{
    /// <summary>
    /// Each hub is mapped at <c>{BasePath}/{hub name}</c> and files at <c>{BasePath}/files/{name}</c>. Advertised over mDNS,
    /// so clients that discover the host need no setting for it.
    /// </summary>
    public string BasePath { get; set; } = UniversalHubProtocol.DefaultBasePath;

    /// <summary>
    /// Advertise the hubs over mDNS while HTTP hosting runs, so clients on the same network find them
    /// </summary>
    public bool Advertise { get; set; } = true;

    /// <summary>
    /// The mDNS service type for hubs set up with UseHttp() and no service type of their own. Clients browse for the same
    /// one. iOS lists it in NSBonjourServices.
    /// </summary>
    public string ServiceType { get; set; } = UniversalHubProtocol.ServiceType;

    /// <summary>
    /// Start and stop the HttpServer along with HTTP hosting. Turn off when the app runs the server for other things too.
    /// </summary>
    public bool ManageHttpServer { get; set; } = true;
}


/// <summary>
/// What a hub was told by UseHttp
/// </summary>
/// <param name="ServiceType">The mDNS service type it is advertised under, null for the transport's default</param>
/// <param name="AllowTunnel">Whether clients may reach it through a tunnel (the public internet)</param>
internal sealed record HttpHubSettings(string? ServiceType, bool AllowTunnel);


/// <summary>
/// A file transfer over HTTP or a peer link (Multipeer, Nearby)
/// </summary>
/// <param name="Type">Upload or Download, from the client's side</param>
/// <param name="FileName">The requested file name</param>
/// <param name="Size">The upload's length; null for downloads</param>
/// <param name="Client">The hub client asking</param>
public sealed record UniversalHubFileRequest(
    L2CapTransferType Type,
    string FileName,
    long? Size,
    HubConnectedClient Client
);
