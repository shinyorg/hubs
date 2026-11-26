using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

public class UniversalHubHostOptions
{
    /// <summary>
    /// Each hub is mapped at <c>{BasePath}/{characteristicUuid}</c> and files at <c>{BasePath}/files/{name}</c>
    /// </summary>
    public string BasePath { get; set; } = UniversalHubProtocol.DefaultBasePath;

    /// <summary>
    /// Advertise the hubs over mDNS while Wi-Fi hosting runs, so clients on the same network find them
    /// </summary>
    public bool Advertise { get; set; } = true;

    /// <summary>
    /// The mDNS service type. Clients browse for the same one - change both or neither.
    /// iOS lists it in NSBonjourServices.
    /// </summary>
    public string ServiceType { get; set; } = UniversalHubProtocol.ServiceType;

    /// <summary>
    /// Identifies this host across transports, so a client that sees it over BLE and Wi-Fi shows it once.
    /// Defaults to a new id each time the app starts - persist one for a stable id.
    /// </summary>
    public string HostId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Start and stop the HttpServer along with Wi-Fi hosting. Turn off when the app runs the server for other things too.
    /// </summary>
    public bool ManageHttpServer { get; set; } = true;

    /// <summary>
    /// Gate for Wi-Fi file transfers - return false to refuse (403). Wi-Fi files otherwise follow the same
    /// <see cref="BleHubHostOptions.EnableFileTransfers"/> settings as L2CAP: directory, uploads/downloads allowed,
    /// overwrite and MaxUploadSize. (The L2CAP Authorize hook takes an L2CAP request, so it can't be reused here.)
    /// </summary>
    public Func<UniversalHubFileRequest, bool>? AuthorizeFile { get; set; }
}


/// <summary>
/// A file transfer over Wi-Fi
/// </summary>
/// <param name="Type">Upload or Download, from the client's side</param>
/// <param name="FileName">The requested file name</param>
/// <param name="Size">The upload's length; null for downloads</param>
/// <param name="Client">The hub client asking</param>
public sealed record UniversalHubFileRequest(
    L2CapTransferType Type,
    string FileName,
    long? Size,
    BleHubConnectedClient Client
);
