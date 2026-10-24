using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;

namespace Shiny.BluetoothLE.Hubs;

public enum BleHubClientStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting
}


/// <summary>
/// A host found while scanning
/// </summary>
public sealed record BleHubHostInfo(IPeripheral Peripheral, string? Name, int Rssi)
{
    public string Id => this.Peripheral.Uuid;
}


/// <summary>
/// What the client tells the host about itself during the handshake
/// </summary>
public sealed record BleHubConnectOptions(
    string? Name = null,
    string? AppVersion = null,
    Dictionary<string, string>? Properties = null
);


/// <summary>
/// The client's status changed
/// </summary>
/// <param name="Status">The new status</param>
/// <param name="Disconnect">Why the connection is ending - set for Disconnecting and Disconnected</param>
public sealed record BleHubStatusChangedEventArgs(BleHubClientStatus Status, HubDisconnect? Disconnect = null)
{
    /// <summary>
    /// <see cref="HubDisconnect.Description"/>, null when nothing is ending
    /// </summary>
    public string? Reason => this.Disconnect?.Description;
}


/// <summary>
/// Connection management shared by every generated hub proxy
/// </summary>
public interface IBleHubConnection
{
    BleHubClientStatus Status { get; }
    BleHubHostInfo? Host { get; }

    /// <summary>
    /// The host's advertised name, from the handshake - kept up to date when the host renames itself
    /// </summary>
    string? HostName { get; }

    /// <summary>
    /// The name the host knows this client by: <see cref="BleHubConnectOptions.Name"/>, or the latest <see cref="Rename"/>.
    /// Null while disconnected.
    /// </summary>
    string? ClientName { get; }

    /// <summary>
    /// Whether the host serves L2CAP file transfers (and this device supports L2CAP)
    /// </summary>
    bool CanTransferFiles { get; }

    event EventHandler<BleHubStatusChangedEventArgs>? StatusChanged;
    event EventHandler? Connected;

    /// <summary>
    /// Raised once a connection ends, with why: <see cref="HubDisconnect.Reason"/> (you disconnected, the link was lost,
    /// the host disconnected you or shut down...) and the host's message, if it gave one
    /// </summary>
    event EventHandler<HubDisconnect>? Disconnected;

    /// <summary>
    /// The host changed its name (IBleHubHost.Rename) - <see cref="HostName"/> already has it. Raised in order with the
    /// hub's events.
    /// </summary>
    event EventHandler<string?>? HostRenamed;

    /// <summary>
    /// Scans for hosts serving this hub. Dispose the subscription to stop scanning. While this app hosts hubs too
    /// (AddBleHubServer), its own advertisement is left out - matched by service UUID and advertised local name.
    /// </summary>
    IObservable<BleHubHostInfo> Discover();

    Task Connect(BleHubHostInfo host, BleHubConnectOptions? options = null, CancellationToken cancellationToken = default);
    Task Disconnect();

    /// <summary>
    /// Changes the name the host knows this client by, without reconnecting. The host runs ValidateClient and the hub's
    /// OnRenamedAsync; a refusal throws <see cref="BleHubRemoteException"/> of type
    /// <see cref="BleHubRemoteException.RenameRefused"/> and the name is unchanged. Hosts older than this feature refuse it too.
    /// A later Connect uses the name in its own options.
    /// </summary>
    Task Rename(string? name, CancellationToken cancellationToken = default);

    Task<L2CapTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<L2CapTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<L2CapTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <see cref="OpenMediaChannel"/> can work: connected, and the host serves media channels (over BLE: it
    /// enabled them and this device supports L2CAP)
    /// </summary>
    bool CanOpenMediaChannel { get; }

    /// <summary>
    /// Opens this connection's media channel - an ordered packet pipe beside the hub, for live audio and video. Returns
    /// the channel already open when there is one. It closes when the connection ends. Throws
    /// <see cref="NotSupportedException"/> when the host or the device can't, and <see cref="BleHubException"/> when the
    /// host refuses it.
    /// </summary>
    Task<IBleHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken = default);
}


/// <summary>
/// A connection to one hub plus its typed proxy
/// </summary>
public interface IBleHubClient<out TContract> : IBleHubConnection where TContract : class
{
    /// <summary>
    /// Call hub methods and subscribe to hub events here
    /// </summary>
    TContract Hub { get; }
}


/// <summary>
/// Services the generated proxies need - registered by AddBleHubClient
/// </summary>
public sealed class BleHubClientServices(
    BleHubProtocolOptions options,
    IBleHubSerializer serializer,
    IBleManager? bleManager = null,
    ILoggerFactory? loggerFactory = null
)
{
    public BleHubProtocolOptions Options { get; } = options;
    public IBleHubSerializer Serializer { get; } = serializer;
    public IBleManager? BleManager { get; } = bleManager;
    public ILoggerFactory? LoggerFactory { get; } = loggerFactory;
    internal BleConnectionTracker Connections { get; } = new();

    /// <summary>
    /// What this app's own hub host is advertising - discovery leaves it out
    /// </summary>
    internal LocalHubAdvertisement? LocalAdvertisement { get; init; }
}


/// <summary>
/// Several hub clients can share one BLE connection to a host - only the last one out disconnects
/// </summary>
internal sealed class BleConnectionTracker
{
    readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public void Acquire(string peripheralId)
    {
        lock (this.counts)
            this.counts[peripheralId] = this.counts.TryGetValue(peripheralId, out var c) ? c + 1 : 1;
    }

    /// <summary>
    /// Returns true when nobody else is using the connection
    /// </summary>
    public bool Release(string peripheralId)
    {
        lock (this.counts)
        {
            if (!this.counts.TryGetValue(peripheralId, out var c) || c <= 1)
            {
                this.counts.Remove(peripheralId);
                return true;
            }
            this.counts[peripheralId] = c - 1;
            return false;
        }
    }
}
