using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Per hub settings
/// </summary>
public class BleHubOptions
{
    /// <summary>
    /// Maximum number of connected clients for this hub, null for no limit
    /// </summary>
    public int? MaxClients { get; set; } = 8;

    /// <summary>
    /// Optional gate for incoming clients - return a rejection reason to refuse, or null to accept
    /// </summary>
    public Func<HandshakeInfo, string?>? ValidateClient { get; set; }
}


/// <summary>
/// Settings shared by every hub on this device
/// </summary>
public class BleHubHostOptions
{
    /// <summary>
    /// The one GATT service every hub lives in - each hub is a characteristic inside it. It is the only UUID the host
    /// advertises, and the one its clients scan for (their own ServiceUuid must match).
    /// </summary>
    public string ServiceUuid { get; set; } = BleHubProtocolOptions.DefaultServiceUuid;

    /// <summary>
    /// Advertised local name, also sent to clients in the handshake. Keep it short - the 128-bit service UUID leaves little
    /// room in a 31 byte advertisement. Null to advertise without a name.
    /// </summary>
    public string? LocalName { get; set; }

    /// <summary>
    /// How often to sweep for clients that dropped without unsubscribing
    /// </summary>
    public TimeSpan ClientSweepInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// L2CAP file transfer settings, null when disabled
    /// </summary>
    public BleHubFileTransferOptions? FileTransfers { get; private set; }

    /// <summary>
    /// Serves file uploads and downloads over L2CAP. Uses a directory server unless an <see cref="IBleHubFileHandler"/> is registered.
    /// </summary>
    public BleHubHostOptions EnableFileTransfers(string rootDirectory, Action<BleHubFileTransferOptions>? configure = null)
    {
        var options = new BleHubFileTransferOptions(rootDirectory);
        configure?.Invoke(options);
        this.FileTransfers = options;
        return this;
    }

    /// <summary>
    /// Media channel settings, null when disabled
    /// </summary>
    public BleHubMediaChannelOptions? MediaChannels { get; private set; }

    /// <summary>
    /// Serves media channels (<see cref="IBleHubMediaChannel"/>) on a second L2CAP PSM, opened when hosting starts. Each
    /// connected client may open one, beside its hub connection, for live audio and video. Hubs see them through
    /// <see cref="IHubContext{THub}.MediaChannelOpened"/>.
    /// </summary>
    /// <param name="maxPacketSize">The largest packet either side may send</param>
    /// <param name="secure">Listen for secure (encrypted) L2CAP channels. Android listens on secure and insecure channels separately; Apple ignores it.</param>
    public BleHubHostOptions EnableMediaChannels(int maxPacketSize = 128 * 1024, bool secure = false)
    {
        if (maxPacketSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPacketSize), "Max packet size must be at least 1");

        this.MediaChannels = new BleHubMediaChannelOptions(maxPacketSize, secure);
        return this;
    }
}


/// <summary>
/// Media channel settings - see <see cref="BleHubHostOptions.EnableMediaChannels"/>
/// </summary>
/// <param name="MaxPacketSize">The largest packet either side may send</param>
/// <param name="Secure">Whether the L2CAP listener is secure</param>
public sealed record BleHubMediaChannelOptions(int MaxPacketSize, bool Secure);


/// <summary>
/// A client opened its media channel
/// </summary>
/// <param name="Client">Whose channel it is</param>
/// <param name="Channel">The channel - its ConnectionId is the client's</param>
public sealed record BleHubMediaChannelEventArgs(BleHubConnectedClient Client, IBleHubMediaChannel Channel);


public class BleHubFileTransferOptions(string rootDirectory)
{
    /// <summary>
    /// Uploads land here and downloads are served from here. Peer supplied names can never escape it.
    /// </summary>
    public string RootDirectory { get; } = rootDirectory;

    public bool Secure { get; set; }
    public bool AllowUploads { get; set; } = true;
    public bool AllowDownloads { get; set; } = true;
    public bool OverwriteExistingUploads { get; set; } = true;
    public long? MaxUploadSize { get; set; }
    public L2CapTransferOptions? Transfer { get; set; }

    /// <summary>
    /// Return false to refuse a transfer (the peer gets NotPermitted)
    /// </summary>
    public Func<BleHubFileRequest, bool>? Authorize { get; set; }
}


/// <summary>
/// A file transfer request. <see cref="Client"/> is the hub client on the same device, when the L2CAP peer could be matched.
/// </summary>
public sealed record BleHubFileRequest(
    L2CapFileRequest Request,
    BleHubConnectedClient? Client
);


/// <summary>
/// Register to take over file transfers entirely (serve from a database, generate content, route per client...).
/// Every request must be answered with an Accept* or Reject call.
/// </summary>
public interface IBleHubFileHandler
{
    Task Handle(BleHubFileRequest request, CancellationToken cancellationToken);
}


public sealed record BleHubFileTransferredEventArgs(
    BleHubConnectedClient? Client,
    string PeerIdentifier,
    L2CapTransferType Type,
    string FileName,
    string LocalFilePath,
    long BytesTransferred,
    TimeSpan Elapsed
);

public sealed record BleHubFileProgressEventArgs(
    BleHubConnectedClient? Client,
    string PeerIdentifier,
    L2CapTransferType Type,
    string FileName,
    TransferProgress Progress
);


public sealed record BleHubRegistration(Type HubType, string CharacteristicUuid, BleHubOptions Options);
