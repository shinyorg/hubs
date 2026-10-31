using System.Collections.Concurrent;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Switchboard;

namespace Shiny.UniversalHubs;

/// <summary>
/// A client connected to a hub, over either transport
/// </summary>
/// <param name="Id">The connection id - the peripheral UUID over BLE, <c>sb:{lineId}</c> over Wi-Fi, <c>mc:{id}</c> over Multipeer</param>
/// <param name="Transport">How the client is connected - <see cref="HubTransports.Ble"/>, <see cref="HubTransports.Http"/> or <see cref="HubTransports.Multipeer"/></param>
/// <param name="Client">The client's handshake details and per connection state</param>
public sealed record HubConnection(string Id, HubTransports Transport, BleHubConnectedClient Client)
{
    /// <summary>
    /// Your own state for this connection, kept for as long as it is connected - the same bag on every transport
    /// (<see cref="BleHubConnectedClient.Items"/>). Not to be confused with <see cref="BleHubConnectedClient.Properties"/>,
    /// the read-only values the client sent in its handshake.
    /// </summary>
    public IDictionary<string, object> Properties => this.Client.Items;

    /// <summary>
    /// The BLE central the client is connected through - null over HTTP
    /// </summary>
    public Shiny.BluetoothLE.Hosting.IPeripheral? Ble => this.Client.Peripheral;

    /// <summary>
    /// The Switchboard line the client is connected through, with the request being handled right now
    /// (<see cref="CallerContext.HttpContext"/>: the connect request in OnConnectedAsync, the call's request in a hub
    /// method), its User, line id and the line's own Items. Null over BLE, and in OnDisconnectedAsync - the line's
    /// requests are over by then.
    /// </summary>
    public CallerContext? Http { get; init; }

    /// <summary>
    /// The MultipeerConnectivity peer the client is connected through - null on the other transports
    /// </summary>
    public MultipeerPeer? Multipeer { get; init; }

    /// <summary>
    /// Prefixed to the Switchboard line id to make a Wi-Fi connection id
    /// </summary>
    internal const string HttpPrefix = "sb:";

    /// <summary>
    /// Prefixed to the session id to make a Multipeer connection id
    /// </summary>
    internal const string MultipeerPrefix = "mc:";

    /// <summary>
    /// Which transport a connection id belongs to. Wi-Fi ids are <c>sb:{lineId}</c>, Multipeer ids <c>mc:{id}</c>; anything
    /// else is a BLE peripheral.
    /// </summary>
    public static HubTransports GetTransport(string connectionId)
    {
        if (connectionId.StartsWith(HttpPrefix, StringComparison.Ordinal))
            return HubTransports.Http;

        return connectionId.StartsWith(MultipeerPrefix, StringComparison.Ordinal) ? HubTransports.Multipeer : HubTransports.Ble;
    }

    public static HubConnection From(BleHubConnectedClient client)
    {
        var transport = GetTransport(client.Id);
        return new(client.Id, transport, client)
        {
            Http = transport == HubTransports.Http ? HttpCall.Current : null,
            Multipeer = transport == HubTransports.Multipeer ? MultipeerPeers.Find(client.Id) : null
        };
    }

    public override string ToString() => $"{this.Client.Name ?? "(unnamed)"} [{this.Id}] over {this.Transport}";
}


/// <summary>
/// Base class for a hub served over BLE and Wi-Fi. <typeparamref name="TContract"/> is the [BleHubClient] interface.
/// Register it with AddUniversalServer (or AddBleHubServer and <see cref="UniversalHubHostBuilder.AddHub{THub}"/>) exactly like a BleHub - this only adds
/// <see cref="Connection"/> and lifecycle hooks that say which transport the client is on.
/// </summary>
public abstract class UniversalHub<TContract> : BleHub<TContract> where TContract : class
{
    HubConnection? connection;

    /// <summary>
    /// The client that made the call, and the transport it is connected over
    /// </summary>
    public HubConnection Connection => this.connection ??= HubConnection.From(this.Context.Client);

    /// <summary>
    /// Runs once a client completes its handshake, over either transport, and before it is told it is connected
    /// </summary>
    public virtual Task OnConnectedAsync(HubConnection connection) => Task.CompletedTask;

    /// <summary>
    /// Runs when a client leaves, over either transport, with why (<see cref="HubDisconnect.Reason"/>) and any message.
    /// Group membership is still intact.
    /// </summary>
    public virtual Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect) => Task.CompletedTask;


    public sealed override Task OnConnectedAsync() => this.OnConnectedAsync(this.Connection);
    public sealed override Task OnDisconnectedAsync(HubDisconnect disconnect)
    {
        // the line's requests are over - nothing current to show, whatever the async flow still holds
        HttpCall.Current = null;
        return this.OnDisconnectedAsync(this.Connection, disconnect);
    }
    public sealed override Task OnDisconnectedAsync(string? reason) => Task.CompletedTask;
}


/// <summary>
/// The Switchboard call the hub is running inside of, carried to <see cref="HubConnection.From"/> through the async flow
/// </summary>
internal static class HttpCall
{
    static readonly AsyncLocal<CallerContext?> current = new();

    public static CallerContext? Current
    {
        get => current.Value;
        set => current.Value = value;
    }
}


/// <summary>
/// A client's MultipeerConnectivity peer
/// </summary>
/// <param name="DisplayName">The name the client's device gave its peer - the hub's own client name is <see cref="BleHubConnectedClient.Name"/></param>
public sealed record MultipeerPeer(string DisplayName);


/// <summary>
/// The Multipeer peer behind each Multipeer connection id, kept by Shiny.UniversalHubs.Multipeer while it is connected
/// </summary>
internal static class MultipeerPeers
{
    static readonly ConcurrentDictionary<string, MultipeerPeer> peers = new(StringComparer.Ordinal);

    public static void Add(string connectionId, MultipeerPeer peer) => peers[connectionId] = peer;
    public static void Remove(string connectionId) => peers.TryRemove(connectionId, out _);
    public static MultipeerPeer? Find(string connectionId) => peers.TryGetValue(connectionId, out var peer) ? peer : null;
}
