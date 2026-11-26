namespace Shiny.UniversalHubs;

/// <summary>
/// HTTP hosting settings - the hubs' WebSocket routes, mDNS and the server's lifetime
/// </summary>
public class HttpTransportOptions
{
    /// <summary>
    /// Each hub is a WebSocket at <c>{BasePath}/{hub name}</c>. Advertised over mDNS, so clients that discover the host need
    /// no setting for it.
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

    /// <summary>
    /// The mDNS service types a dynamic hub may be advertised under. Empty allows only <see cref="ServiceType"/>. The
    /// platform fixes the list when the app is built - iOS lists every type in NSBonjourServices.
    /// </summary>
    public IList<string> DynamicServiceTypes { get; } = new List<string>();
}


/// <summary>
/// What a hub was told by UseHttp
/// </summary>
/// <param name="ServiceType">The mDNS service type it is advertised under, null for the transport's default</param>
/// <param name="AllowTunnel">Whether clients may reach it through a tunnel (the public internet)</param>
public sealed record HttpHubSettings(string? ServiceType, bool AllowTunnel);
