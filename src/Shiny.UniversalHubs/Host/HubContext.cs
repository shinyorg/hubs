using Shiny.UniversalHubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// Reach a hub's clients from outside the hub (services, view models, timers...).
/// The generated extension property <c>Clients</c> gives typed pushes - eg. <c>context.Clients.All.StateChanged(state)</c>.
/// </summary>
public interface IHubContext<THub> where THub : class
{
    /// <summary>
    /// Whether this hub is accepting clients on any transport
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts only this hub, on every transport it was set up with - like IUniversalHubHost.StartHub. A transport that
    /// can't start is skipped as long as another one does.
    /// </summary>
    Task Start(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops only this hub on every transport - its clients are told to disconnect and new handshakes are refused
    /// </summary>
    Task Stop(string? reason = null);

    IGroupManager Groups { get; }

    /// <summary>
    /// Clients that have completed the handshake with this hub
    /// </summary>
    IReadOnlyList<HubConnectedClient> ConnectedClients { get; }

    event EventHandler<HubConnectedClient>? ClientConnected;
    event EventHandler<HubClientDisconnectedEventArgs>? ClientDisconnected;

    /// <summary>
    /// A connected client changed its name (<see cref="IHubConnection.Rename"/>)
    /// </summary>
    event EventHandler<HubClientRenamedEventArgs>? ClientRenamed;

    /// <summary>
    /// Ends a client's session (cooperative - the client library disconnects itself)
    /// </summary>
    Task Disconnect(string connectionId, string? reason = null);

    /// <summary>
    /// A connected client opened its media channel (<see cref="UniversalServerOptions.EnableMediaChannels"/>), over any
    /// transport. The channel closes when the client disconnects or the hub stops.
    /// </summary>
    event EventHandler<HubMediaChannelEventArgs>? MediaChannelOpened;

    /// <summary>
    /// A client's open media channel, null when it has none
    /// </summary>
    IHubMediaChannel? GetMediaChannel(string connectionId);

    /// <summary>
    /// The hub as transports reach it
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    IHubEndpoint TransportEndpoint { get; }

    /// <summary>
    /// Used by the generated <c>Clients</c> extension property
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    IHubClients<HubPush<TContract>> GetClients<TContract>() where TContract : class;
}


internal sealed class HubContext<THub>(Func<HubRuntime> getRuntime, UniversalHubHost? host = null) : IHubContext<THub> where THub : class
{
    HubRuntime Runtime => getRuntime();
    UniversalHubHost Host => host ?? throw new InvalidOperationException("No hub host is available");

    public bool IsRunning => this.Runtime.IsRunning;

    public Task Start(CancellationToken cancellationToken = default)
        => this.Host.StartHubs([this.Host.Get(typeof(THub))], null, cancellationToken);

    public Task Stop(string? reason = null)
        => this.Host.StopHubs([this.Host.Get(typeof(THub))], null, new HubDisconnect(HubDisconnectReason.ServerShutdown, reason ?? "Hub stopped"));

    public IGroupManager Groups => this.Runtime;
    public IHubEndpoint TransportEndpoint => this.Runtime;
    public IReadOnlyList<HubConnectedClient> ConnectedClients => this.Runtime.Clients;

    public event EventHandler<HubConnectedClient>? ClientConnected
    {
        add => this.Runtime.ClientConnected += value;
        remove => this.Runtime.ClientConnected -= value;
    }

    public event EventHandler<HubClientDisconnectedEventArgs>? ClientDisconnected
    {
        add => this.Runtime.ClientDisconnected += value;
        remove => this.Runtime.ClientDisconnected -= value;
    }

    public event EventHandler<HubClientRenamedEventArgs>? ClientRenamed
    {
        add => this.Runtime.ClientRenamed += value;
        remove => this.Runtime.ClientRenamed -= value;
    }

    public Task Disconnect(string connectionId, string? reason = null)
        => this.Runtime.Disconnect(connectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, reason));

    public event EventHandler<HubMediaChannelEventArgs>? MediaChannelOpened
    {
        add => this.Runtime.MediaChannelOpened += value;
        remove => this.Runtime.MediaChannelOpened -= value;
    }

    public IHubMediaChannel? GetMediaChannel(string connectionId) => this.Runtime.GetMediaChannel(connectionId);

    public IHubClients<HubPush<TContract>> GetClients<TContract>() where TContract : class
    {
        var runtime = this.Runtime;
        if (runtime.ContractType != typeof(TContract))
            throw new InvalidOperationException($"{typeof(THub).Name} serves {runtime.ContractType.Name}, not {typeof(TContract).Name}");

        return new HubClients<HubPush<TContract>>(runtime, target => new HubPush<TContract>(target, runtime.Serializer));
    }
}
