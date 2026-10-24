namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Base class for a hub. <typeparamref name="TContract"/> is the [BleHubClient] interface - implement its methods as public
/// methods on the hub (a trailing CancellationToken parameter is optional). A new hub instance is created, in its own DI scope,
/// for every invocation - keep state in singletons or <see cref="BleHubCallerContext.Items"/>.
/// </summary>
public abstract class BleHub<TContract> : IBleHubInternal where TContract : class
{
    /// <summary>
    /// The client that made the call
    /// </summary>
    public BleHubCallerContext Context { get; private set; } = null!;

    /// <summary>
    /// Push targets - eg. Clients.All.SomethingHappened(...), Clients.Others..., Clients.Group("x")...
    /// </summary>
    public IHubCallerClients<BleHubPush<TContract>> Clients { get; private set; } = null!;

    public IGroupManager Groups { get; private set; } = null!;

    /// <summary>
    /// Runs once a client completes its handshake and before it is told it is connected
    /// </summary>
    public virtual Task OnConnectedAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs when a client leaves (disconnect, unsubscribe, host disconnect or connection loss). Group membership is still intact.
    /// Override <see cref="OnDisconnectedAsync(HubDisconnect)"/> instead to know why as a <see cref="HubDisconnectReason"/>.
    /// </summary>
    public virtual Task OnDisconnectedAsync(string? reason) => Task.CompletedTask;

    /// <summary>
    /// Runs when a client leaves, saying why. Group membership is still intact. By default it calls
    /// <see cref="OnDisconnectedAsync(string)"/> with <see cref="HubDisconnect.Description"/>.
    /// </summary>
    public virtual Task OnDisconnectedAsync(HubDisconnect disconnect) => this.OnDisconnectedAsync(disconnect.Description);

    /// <summary>
    /// Runs when a connected client renames itself, after ValidateClient accepted the new name and before the client is
    /// told. <see cref="BleHubCallerContext.Client"/> already has the new name. Throw to refuse the rename - the name is put
    /// back and the client gets the exception's message.
    /// </summary>
    public virtual Task OnRenamedAsync(string? previousName) => Task.CompletedTask;


    void IBleHubInternal.Initialize(BleHubCallerContext context, HubRuntime runtime)
    {
        this.Context = context;
        this.Groups = runtime.Groups;
        this.Clients = new HubCallerClients<BleHubPush<TContract>>(runtime, target => new BleHubPush<TContract>(target, runtime.Serializer), context.ConnectionId);
    }
}


internal interface IBleHubInternal
{
    void Initialize(BleHubCallerContext context, HubRuntime runtime);
    Task OnConnectedAsync();
    Task OnDisconnectedAsync(HubDisconnect disconnect);
    Task OnRenamedAsync(string? previousName);
}


public sealed class BleHubCallerContext
{
    readonly Action<string?> abort;

    internal BleHubCallerContext(BleHubConnectedClient client, Action<string?> abort, CancellationToken connectionAborted)
    {
        this.Client = client;
        this.abort = abort;
        this.ConnectionAborted = connectionAborted;
    }

    public string ConnectionId => this.Client.Id;
    public BleHubConnectedClient Client { get; }

    /// <summary>
    /// Per connection state, kept for as long as the client is connected
    /// </summary>
    public IDictionary<string, object> Items => this.Client.Items;

    /// <summary>
    /// Cancelled when the client goes away
    /// </summary>
    public CancellationToken ConnectionAborted { get; }

    /// <summary>
    /// Ends this client's session (cooperative - the client library disconnects itself).
    /// Called from a hub method, it takes effect once that method's reply has been sent.
    /// </summary>
    public void Abort(string? reason = null) => this.abort(reason);
}
