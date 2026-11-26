using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// Per hub settings
/// </summary>
public class HubOptions
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
/// Settings shared by every hub and every transport on this host
/// </summary>
public class UniversalServerOptions
{
    /// <summary>
    /// The host's name: sent to clients in every handshake, and advertised by the transports that advertise (keep it short
    /// over BLE - a 31 byte advertisement has little room). Null for none.
    /// </summary>
    public string? LocalName { get; set; }

    /// <summary>
    /// Identifies this host across transports, so a client that sees it over several shows it once. Defaults to a new id
    /// each time the app starts - persist one for a stable id.
    /// </summary>
    public string HostId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// How long a client whose link dropped (out of range, its app went to the background) is held for it to come back
    /// as the same connection - same id, groups and Items, with the pushes it missed replayed. While it is held the hub
    /// still counts it in ConnectedClients and MaxClients, and OnDisconnectedAsync only runs if the window runs out.
    /// Covers every first-party transport (BLE, HTTP, Multipeer, Nearby). Zero turns it off.
    /// </summary>
    public TimeSpan ResumeWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pushes kept for a held client. One that misses more than this can't be given everything, so it is let go
    /// (OnDisconnectedAsync runs) rather than resumed with a gap.
    /// </summary>
    public int ResumeBufferSize { get; set; } = 128;

    /// <summary>
    /// Gate for file transfers, on every transport - return false to refuse. The rest of the rules are the
    /// <see cref="EnableFileTransfers"/> settings: directory, uploads/downloads allowed, overwrite and MaxUploadSize.
    /// </summary>
    public Func<Shiny.UniversalHubs.UniversalHubFileRequest, bool>? AuthorizeFile { get; set; }

    /// <summary>
    /// File transfer settings, null when disabled
    /// </summary>
    public HubFileTransferOptions? FileTransfers { get; private set; }

    /// <summary>
    /// Serves file uploads and downloads on every transport that can carry them (L2CAP over BLE, in-band over HTTP,
    /// Multipeer and Nearby).
    /// </summary>
    public UniversalServerOptions EnableFileTransfers(string rootDirectory, Action<HubFileTransferOptions>? configure = null)
    {
        var options = new HubFileTransferOptions(rootDirectory);
        configure?.Invoke(options);
        this.FileTransfers = options;
        return this;
    }

    /// <summary>
    /// Media channel settings, null when disabled
    /// </summary>
    public HubMediaChannelOptions? MediaChannels { get; private set; }

    /// <summary>
    /// Serves media channels (<see cref="IHubMediaChannel"/>) on every transport that can carry them - a second L2CAP
    /// PSM over BLE, a WebSocket over HTTP, the peer link itself over Multipeer and Nearby. Each connected client may open
    /// one, beside its hub connection, for live audio and video. Hubs see them through
    /// <see cref="IHubContext{THub}.MediaChannelOpened"/>.
    /// </summary>
    /// <param name="maxPacketSize">The largest packet either side may send</param>
    public UniversalServerOptions EnableMediaChannels(int maxPacketSize = 128 * 1024)
    {
        if (maxPacketSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPacketSize), "Max packet size must be at least 1");

        this.MediaChannels = new HubMediaChannelOptions(maxPacketSize);
        return this;
    }
}


/// <summary>
/// Media channel settings - see <see cref="UniversalServerOptions.EnableMediaChannels"/>
/// </summary>
/// <param name="MaxPacketSize">The largest packet either side may send</param>
public sealed record HubMediaChannelOptions(int MaxPacketSize);


/// <summary>
/// A client opened its media channel
/// </summary>
/// <param name="Client">Whose channel it is</param>
/// <param name="Channel">The channel - its ConnectionId is the client's</param>
public sealed record HubMediaChannelEventArgs(HubConnectedClient Client, IHubMediaChannel Channel);


/// <summary>
/// File transfer settings, the same on every transport - see <see cref="UniversalServerOptions.EnableFileTransfers"/>
/// </summary>
public class HubFileTransferOptions(string rootDirectory)
{
    /// <summary>
    /// Uploads land here and downloads are served from here. Peer supplied names can never escape it.
    /// </summary>
    public string RootDirectory { get; } = rootDirectory;

    public bool AllowUploads { get; set; } = true;
    public bool AllowDownloads { get; set; } = true;
    public bool OverwriteExistingUploads { get; set; } = true;
    public long? MaxUploadSize { get; set; }
}


public sealed record HubFileTransferredEventArgs(
    HubConnectedClient? Client,
    string PeerIdentifier,
    HubTransferDirection Direction,
    string FileName,
    string LocalFilePath,
    long BytesTransferred,
    TimeSpan Elapsed
);

public sealed record HubFileProgressEventArgs(
    HubConnectedClient? Client,
    string PeerIdentifier,
    HubTransferDirection Direction,
    string FileName,
    HubTransferProgress Progress
);


/// <summary>
/// A hub as its runtime knows it
/// </summary>
/// <param name="HubType">The hub's class - a stand-in for a hub added while the app runs</param>
/// <param name="Name">The hub's name - how clients ask for it on every transport</param>
/// <param name="Options">Its rules</param>
public sealed record HubDefinition(Type HubType, string Name, HubOptions Options);
