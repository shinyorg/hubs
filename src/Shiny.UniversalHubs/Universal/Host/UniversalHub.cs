using System.Collections.Concurrent;

namespace Shiny.UniversalHubs;

/// <summary>
/// A client connected to a hub, over any transport
/// </summary>
/// <param name="Id">The connection id - the central's id over BLE, <c>ws:{id}</c> over HTTP, <c>mc:{id}</c> over Multipeer, <c>nb:{id}</c> over Nearby</param>
/// <param name="Transport">
/// The transport the client is connected through right now (<c>connection.Transport is BleHostTransport</c>) - null for a
/// client connected through a channel outside the universal host
/// </param>
/// <param name="Client">The client's handshake details and per connection state</param>
public sealed record HubConnection(string Id, IHubTransport? Transport, HubConnectedClient Client)
{
    /// <summary>
    /// Your own state for this connection, kept for as long as it is connected - the same bag on every transport
    /// (<see cref="HubConnectedClient.Items"/>). Not to be confused with <see cref="HubConnectedClient.Properties"/>,
    /// the read-only values the client sent in its handshake.
    /// </summary>
    public IDictionary<string, object> Properties => this.Client.Items;

    /// <summary>
    /// What the transport knows about the client's link, by type - eg. the BLE central (connection.Peripheral()), the HTTP
    /// request (connection.HttpContext()), the Multipeer or Nearby peer. Null when the transport has no such thing.
    /// </summary>
    public T? Get<T>() where T : class => this.Client.Get<T>();

    /// <summary>
    /// Prefixed to the session id to make an HTTP connection id
    /// </summary>
    internal const string HttpPrefix = "ws:";

    /// <summary>
    /// Prefixed to the session id to make a Multipeer connection id
    /// </summary>
    internal const string MultipeerPrefix = "mc:";

    /// <summary>
    /// Prefixed to the session id to make a Nearby connection id
    /// </summary>
    internal const string NearbyPrefix = "nb:";

    public static HubConnection From(HubConnectedClient client) => new(client.Id, client.Channel?.Transport, client);

    public override string ToString() => $"{this.Client.Name ?? "(unnamed)"} [{this.Id}] over {this.Transport?.DisplayName ?? "an external channel"}";
}


/// <summary>
/// Base class for a hub. <typeparamref name="TContract"/> is the [UniversalHubContract] interface - implement its methods as public
/// methods on the hub (a trailing CancellationToken parameter is optional). A new hub instance is created, in its own DI
/// scope, for every invocation - keep state in singletons or <see cref="HubCallerContext.Items"/>. The same hub serves
/// clients on every transport it is set up with.
/// </summary>
public abstract class UniversalHub<TContract> : IHubInternal where TContract : class
{
    HubConnection? connection;

    /// <summary>
    /// The client that made the call
    /// </summary>
    public HubCallerContext Context { get; private set; } = null!;

    /// <summary>
    /// Push targets - eg. Clients.All.SomethingHappened(...), Clients.Others..., Clients.Group("x")...
    /// </summary>
    public IHubCallerClients<HubPush<TContract>> Clients { get; private set; } = null!;

    public IGroupManager Groups { get; private set; } = null!;

    /// <summary>
    /// The client that made the call, and the transport it is connected over
    /// </summary>
    public HubConnection Connection => this.connection ??= HubConnection.From(this.Context.Client);

    /// <summary>
    /// Runs once a client completes its handshake, on any transport, and before it is told it is connected
    /// </summary>
    public virtual Task OnConnectedAsync(HubConnection connection) => Task.CompletedTask;

    /// <summary>
    /// Runs when a client leaves (disconnect, host disconnect or connection loss), on any transport, with why
    /// (<see cref="HubDisconnect.Reason"/>) and any message. Group membership is still intact.
    /// </summary>
    public virtual Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect) => Task.CompletedTask;

    /// <summary>
    /// Runs when a connected client renames itself, after ValidateClient accepted the new name and before the client is
    /// told. <see cref="HubCallerContext.Client"/> already has the new name. Throw to refuse the rename - the name is put
    /// back and the client gets the exception's message.
    /// </summary>
    public virtual Task OnRenamedAsync(string? previousName) => Task.CompletedTask;


    void IHubInternal.Initialize(HubCallerContext context, HubRuntime runtime)
    {
        this.Context = context;
        this.Groups = runtime.Groups;
        this.Clients = new HubCallerClients<HubPush<TContract>>(runtime, target => new HubPush<TContract>(target, runtime.Serializer), context.ConnectionId);
    }

    Task IHubInternal.OnConnectedAsync() => this.OnConnectedAsync(this.Connection);

    Task IHubInternal.OnDisconnectedAsync(HubDisconnect disconnect) => this.OnDisconnectedAsync(this.Connection, disconnect);
}
