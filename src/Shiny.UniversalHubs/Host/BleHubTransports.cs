using System.ComponentModel;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// How a hub reaches one client connected through another transport (Wi-Fi, ...). Implemented by that transport.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IBleHubPeerChannel
{
    /// <summary>
    /// Delivers a push. <paramref name="arguments"/> is the encoded argument list (see <see cref="BleHubArgumentWriter"/>).
    /// Calls for one client are made one at a time, in push order.
    /// </summary>
    Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken);

    /// <summary>
    /// The hub is ending this client's session - tell the client why, then drop it
    /// </summary>
    Task Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken);

    /// <summary>
    /// The host has a new name - tell the client. Called in order with <see cref="Push"/>.
    /// </summary>
    Task HostRenamed(string? hostName, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The transport this channel belongs to - what <c>HubConnection.Transport</c> reports. Null for a channel outside the
    /// universal host.
    /// </summary>
    Shiny.UniversalHubs.IHubTransport? Transport => null;
}


/// <summary>
/// The outcome of a hub method called through another transport
/// </summary>
/// <param name="Result">The serialized result, null for a method returning Task</param>
/// <param name="AbortRequested">The method called Context.Abort - send the reply, then call <see cref="IBleHubTransportEndpoint.Disconnect"/></param>
/// <param name="AbortReason">The reason passed to Context.Abort</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record BleHubInvocationResult(byte[]? Result, bool AbortRequested, string? AbortReason);


/// <summary>
/// Serves a hub over a transport other than BLE. Clients connected this way share Clients, Groups, MaxClients,
/// ValidateClient and IHubContext with BLE clients - the hub can't tell them apart.
/// Get one from <see cref="IHubContext{THub}.TransportEndpoint"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IBleHubTransportEndpoint
{
    BleHubRegistration Registration { get; }
    Type ContractType { get; }
    IBleHubSerializer Serializer { get; }

    /// <summary>
    /// Admits a client: applies MaxClients and ValidateClient, registers it and runs OnConnectedAsync.
    /// Returns null when accepted, otherwise why it was refused. The transport checks its own protocol version and
    /// whether it is running - this does not.
    /// </summary>
    Task<string?> Connect(string connectionId, HandshakeInfo info, IBleHubPeerChannel channel, CancellationToken cancellationToken);

    BleHubMethodKind GetMethodKind(string method);

    /// <summary>
    /// Runs a hub method. Hub exceptions are thrown to the caller, which relays them.
    /// </summary>
    Task<BleHubInvocationResult> Invoke(string connectionId, string method, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a streaming hub method. A Context.Abort inside it disconnects the client once the stream completes.
    /// </summary>
    IAsyncEnumerable<byte[]> Stream(string connectionId, string method, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken);

    /// <summary>
    /// The client is gone (it left, or its connection was lost) - runs OnDisconnectedAsync and removes it from its groups
    /// </summary>
    void Disconnected(string connectionId, HubDisconnect disconnect);

    /// <summary>
    /// The link to a client was lost or closed - like <see cref="Disconnected(string, HubDisconnect)"/>, but only while
    /// <paramref name="channel"/> is still the client's channel (a client that resumed on a new channel is left alone), and
    /// a dropped link (<see cref="HubDisconnectReason.ClientTimeout"/>, <see cref="HubDisconnectReason.ConnectionFailed"/>)
    /// holds a client that can resume (<see cref="EnableResume"/>) for the host's resume window instead of ending it.
    /// </summary>
    void Disconnected(string connectionId, IBleHubPeerChannel channel, HubDisconnect disconnect) => this.Disconnected(connectionId, disconnect);

    /// <summary>
    /// Lets a connected client resume after a dropped link. Returns the secret token to hand the client, or null when the
    /// host doesn't hold dropped clients. A transport with its own resume (Wi-Fi) doesn't call it.
    /// </summary>
    string? EnableResume(string connectionId) => null;

    /// <summary>
    /// How long the host holds a dropped client for it to resume - zero when it doesn't
    /// </summary>
    TimeSpan ResumeWindow => TimeSpan.Zero;

    /// <summary>
    /// A client back with its resume token on a new <paramref name="channel"/>. Only connections whose id starts with
    /// <paramref name="connectionIdPrefix"/> (the transport's own) are resumed. When it can be, the connection moves onto
    /// the channel - same connection id, groups and Items - <paramref name="accepted"/> runs with the connection id (send
    /// the client its reply there), then the pushes it missed go out through the channel, in order. Returns null when
    /// resumed, otherwise why not - the client connects again from scratch.
    /// </summary>
    Task<string?> Resume(string resumeToken, string connectionIdPrefix, IBleHubPeerChannel channel, Func<string, Task> accepted, CancellationToken cancellationToken)
        => Task.FromResult<string?>("This host can't resume connections");

    /// <summary>
    /// The client renamed itself: runs ValidateClient with the new name, then OnRenamedAsync. Returns null when renamed,
    /// otherwise why it was refused - relay it to the client as a <see cref="BleHubRemoteException"/> of type
    /// <see cref="BleHubRemoteException.RenameRefused"/>.
    /// </summary>
    Task<string?> Rename(string connectionId, string? name, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a client's session through its channel, then forgets it
    /// </summary>
    Task Disconnect(string connectionId, HubDisconnect disconnect);

    BleHubConnectedClient? FindClient(string connectionId);

    /// <summary>
    /// The transport opened a media channel for a connected client (it checked its own token first). Raises
    /// <see cref="IHubContext{THub}.MediaChannelOpened"/>, and the channel is closed when that client goes. A channel the
    /// client already had is closed and replaced. Returns false when the client is unknown - the caller closes the channel.
    /// </summary>
    bool MediaChannelOpened(string connectionId, IBleHubMediaChannel channel);
}
