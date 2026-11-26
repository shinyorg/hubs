using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;

namespace Shiny.UniversalHubs;

public enum HubClientStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,

    /// <summary>
    /// The link dropped and the client is getting back onto it - the host holds the connection (same id, groups) and
    /// replays the pushes it missed. Calls fail until it is Connected again; if the host's resume window runs out it goes
    /// to Disconnected (and Disconnected is raised) instead.
    /// </summary>
    Reconnecting
}


/// <summary>
/// What the client tells the host about itself during the handshake
/// </summary>
public sealed record HubHandshakeOptions(
    string? Name = null,
    string? AppVersion = null,
    Dictionary<string, string>? Properties = null
)
{
    /// <summary>
    /// When the link drops, get back onto it as the same connection (<see cref="HubClientStatus.Reconnecting"/>)
    /// instead of disconnecting - over BLE and the peer-to-peer transports, when the host holds dropped clients. Pushes
    /// missed in between are replayed; calls in flight when the link dropped fail.
    /// </summary>
    public bool AutomaticReconnect { get; init; } = true;
}


/// <summary>
/// The client's status changed
/// </summary>
/// <param name="Status">The new status</param>
/// <param name="Disconnect">Why the connection is ending - set for Disconnecting and Disconnected - or why the link dropped, for Reconnecting</param>
public sealed record HubStatusChangedEventArgs(HubClientStatus Status, HubDisconnect? Disconnect = null)
{
    /// <summary>
    /// <see cref="HubDisconnect.Description"/>, null when nothing is ending
    /// </summary>
    public string? Reason => this.Disconnect?.Description;
}


/// <summary>
/// Connection management shared by every generated hub proxy
/// </summary>
public interface IHubConnection
{
    HubClientStatus Status { get; }

    /// <summary>
    /// The host's advertised name, from the handshake - kept up to date when the host renames itself
    /// </summary>
    string? HostName { get; }

    /// <summary>
    /// The name the host knows this client by: <see cref="HubHandshakeOptions.Name"/>, or the latest <see cref="Rename"/>.
    /// Null while disconnected.
    /// </summary>
    string? ClientName { get; }

    /// <summary>
    /// Whether this connection can move files: the host serves them, and the transport can carry them (over BLE: this
    /// device supports L2CAP)
    /// </summary>
    bool CanTransferFiles { get; }

    event EventHandler<HubStatusChangedEventArgs>? StatusChanged;
    event EventHandler? Connected;

    /// <summary>
    /// Raised once a connection ends, with why: <see cref="HubDisconnect.Reason"/> (you disconnected, the link was lost,
    /// the host disconnected you or shut down...) and the host's message, if it gave one
    /// </summary>
    event EventHandler<HubDisconnect>? Disconnected;

    /// <summary>
    /// The host changed its name (IUniversalHubHost.Rename) - <see cref="HostName"/> already has it. Raised in order with the
    /// hub's events.
    /// </summary>
    event EventHandler<string?>? HostRenamed;

    Task Disconnect();

    /// <summary>
    /// Changes the name the host knows this client by, without reconnecting. The host runs ValidateClient and the hub's
    /// OnRenamedAsync; a refusal throws <see cref="HubRemoteException"/> of type
    /// <see cref="HubRemoteException.RenameRefused"/> and the name is unchanged. Hosts older than this feature refuse it too.
    /// A later Connect uses the name in its own options.
    /// </summary>
    Task Rename(string? name, CancellationToken cancellationToken = default);

    Task<HubFileTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<HubFileTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<HubFileTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <see cref="OpenMediaChannel"/> can work: connected, and the host serves media channels (over BLE: it
    /// enabled them and this device supports L2CAP)
    /// </summary>
    bool CanOpenMediaChannel { get; }

    /// <summary>
    /// Opens this connection's media channel - an ordered packet pipe beside the hub, for live audio and video. Returns
    /// the channel already open when there is one. It closes when the connection ends. Throws
    /// <see cref="NotSupportedException"/> when the host or the device can't, and <see cref="HubException"/> when the
    /// host refuses it.
    /// </summary>
    Task<IHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken = default);
}


/// <summary>
/// A connection to one hub plus its typed proxy
/// </summary>
public interface IHubProxy<out TContract> : IHubConnection where TContract : class
{
    /// <summary>
    /// Call hub methods and subscribe to hub events here
    /// </summary>
    TContract Hub { get; }
}


/// <summary>
/// Services the generated proxies need - registered by AddUniversalHubClient
/// </summary>
public sealed class HubClientServices(
    HubProtocolOptions options,
    IHubSerializer serializer,
    ILoggerFactory? loggerFactory = null
)
{
    public HubProtocolOptions Options { get; } = options;
    public IHubSerializer Serializer { get; } = serializer;
    public ILoggerFactory? LoggerFactory { get; } = loggerFactory;
}
