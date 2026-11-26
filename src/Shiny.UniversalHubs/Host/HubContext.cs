namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Reach a hub's clients from outside the hub (services, view models, timers...).
/// The generated extension property <c>Clients</c> gives typed pushes - eg. <c>context.Clients.All.StateChanged(state)</c>.
/// </summary>
public interface IHubContext<THub> where THub : class
{
    /// <summary>
    /// Whether this hub is accepting clients
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts only this hub - adds its GATT service (if not already there) and advertises it. Shared pieces (BLE access,
    /// L2CAP file server) start with the first running hub. IBleHubHost.Start() starts every hub.
    /// </summary>
    Task Start(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops only this hub - its clients are told to disconnect and new handshakes are refused. Its GATT service is removed once
    /// no running hub shares it, and the advertisement is updated.
    /// </summary>
    Task Stop(string? reason = null);

    IGroupManager Groups { get; }

    /// <summary>
    /// Clients that have completed the handshake with this hub
    /// </summary>
    IReadOnlyList<BleHubConnectedClient> ConnectedClients { get; }

    event EventHandler<BleHubConnectedClient>? ClientConnected;
    event EventHandler<BleHubClientDisconnectedEventArgs>? ClientDisconnected;

    /// <summary>
    /// A connected client changed its name (<see cref="IBleHubConnection.Rename"/>)
    /// </summary>
    event EventHandler<BleHubClientRenamedEventArgs>? ClientRenamed;

    /// <summary>
    /// Ends a client's session (cooperative - the client library disconnects itself)
    /// </summary>
    Task Disconnect(string connectionId, string? reason = null);

    /// <summary>
    /// A connected client opened its media channel (over BLE when <see cref="BleHubHostOptions.EnableMediaChannels"/> is on,
    /// or over another transport). The channel closes when the client disconnects or the hub stops.
    /// </summary>
    event EventHandler<BleHubMediaChannelEventArgs>? MediaChannelOpened;

    /// <summary>
    /// A client's open media channel, null when it has none
    /// </summary>
    IBleHubMediaChannel? GetMediaChannel(string connectionId);

    /// <summary>
    /// Serves this hub over another transport (Wi-Fi, ...) alongside BLE
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    IBleHubTransportEndpoint TransportEndpoint { get; }

    /// <summary>
    /// Used by the generated <c>Clients</c> extension property
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    IHubClients<BleHubPush<TContract>> GetClients<TContract>() where TContract : class;
}


internal sealed class HubContext<THub>(Func<HubRuntime> getRuntime, BleHubHost? host = null) : IHubContext<THub> where THub : class
{
    HubRuntime Runtime => getRuntime();

    public bool IsRunning => this.Runtime.IsRunning;

    public Task Start(CancellationToken cancellationToken = default)
        => (host ?? throw new InvalidOperationException("No BLE host is available")).StartHub(this.Runtime, cancellationToken);

    public Task Stop(string? reason = null)
        => (host ?? throw new InvalidOperationException("No BLE host is available")).StopHub(this.Runtime, reason);

    public IGroupManager Groups => this.Runtime;
    public IBleHubTransportEndpoint TransportEndpoint => this.Runtime;
    public IReadOnlyList<BleHubConnectedClient> ConnectedClients => this.Runtime.Clients;

    public event EventHandler<BleHubConnectedClient>? ClientConnected
    {
        add => this.Runtime.ClientConnected += value;
        remove => this.Runtime.ClientConnected -= value;
    }

    public event EventHandler<BleHubClientDisconnectedEventArgs>? ClientDisconnected
    {
        add => this.Runtime.ClientDisconnected += value;
        remove => this.Runtime.ClientDisconnected -= value;
    }

    public event EventHandler<BleHubClientRenamedEventArgs>? ClientRenamed
    {
        add => this.Runtime.ClientRenamed += value;
        remove => this.Runtime.ClientRenamed -= value;
    }

    public Task Disconnect(string connectionId, string? reason = null)
        => this.Runtime.Disconnect(connectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, reason));

    public event EventHandler<BleHubMediaChannelEventArgs>? MediaChannelOpened
    {
        add => this.Runtime.MediaChannelOpened += value;
        remove => this.Runtime.MediaChannelOpened -= value;
    }

    public IBleHubMediaChannel? GetMediaChannel(string connectionId) => this.Runtime.GetMediaChannel(connectionId);

    public IHubClients<BleHubPush<TContract>> GetClients<TContract>() where TContract : class
    {
        var runtime = this.Runtime;
        if (runtime.ContractType != typeof(TContract))
            throw new InvalidOperationException($"{typeof(THub).Name} serves {runtime.ContractType.Name}, not {typeof(TContract).Name}");

        return new HubClients<BleHubPush<TContract>>(runtime, target => new BleHubPush<TContract>(target, runtime.Serializer));
    }
}
