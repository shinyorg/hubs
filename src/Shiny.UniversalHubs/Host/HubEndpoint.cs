using System.ComponentModel;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// How a hub reaches one client, through the transport it is connected over. Implemented by that transport.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IHubPeerChannel
{
    /// <summary>
    /// Delivers a push. <paramref name="arguments"/> is the encoded argument list (see <see cref="HubArgumentWriter"/>).
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

    /// <summary>
    /// What the transport knows about the client's link, by type - the BLE central, the HTTP request, the Multipeer or
    /// Nearby peer. <c>HubConnection.Get&lt;T&gt;()</c> asks the client's current channel.
    /// </summary>
    object? GetFeature(Type type) => null;
}


/// <summary>
/// The outcome of a hub method called through another transport
/// </summary>
/// <param name="Result">The serialized result, null for a method returning Task</param>
/// <param name="AbortRequested">The method called Context.Abort - send the reply, then call <see cref="IHubEndpoint.Disconnect"/></param>
/// <param name="AbortReason">The reason passed to Context.Abort</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record HubInvocationResult(byte[]? Result, bool AbortRequested, string? AbortReason);


/// <summary>
/// A streaming hub method called through another transport (<see cref="IHubEndpoint.StartStream"/>)
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class HubStreamInvocation
{
    int abortRequested;
    string? abortReason;


    /// <summary>
    /// A stream that handles Context.Abort itself - <see cref="AbortRequested"/> stays false
    /// </summary>
    public HubStreamInvocation(IAsyncEnumerable<byte[]> items) => this.Items = items;

    internal HubStreamInvocation(Func<HubStreamInvocation, IAsyncEnumerable<byte[]>> items) => this.Items = items(this);


    /// <summary>
    /// The serialized items - the method runs as they are enumerated
    /// </summary>
    public IAsyncEnumerable<byte[]> Items { get; }

    /// <summary>
    /// The method called Context.Abort - once the stream is over (ended, failed or cancelled), send the end of stream or
    /// the error, then call <see cref="IHubEndpoint.Disconnect"/>
    /// </summary>
    public bool AbortRequested => Volatile.Read(ref this.abortRequested) == 1;

    /// <summary>
    /// The reason passed to Context.Abort
    /// </summary>
    public string? AbortReason => Volatile.Read(ref this.abortReason);


    internal void RequestAbort(string? reason)
    {
        Volatile.Write(ref this.abortReason, reason);
        Volatile.Write(ref this.abortRequested, 1);
    }
}


/// <summary>
/// Serves a hub over a transport other than BLE. Clients connected this way share Clients, Groups, MaxClients,
/// ValidateClient and IHubContext with BLE clients - the hub can't tell them apart.
/// Get one from <see cref="IHubContext{THub}.TransportEndpoint"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IHubEndpoint
{
    HubDefinition Registration { get; }
    Type ContractType { get; }
    IHubSerializer Serializer { get; }

    /// <summary>
    /// Admits a client: applies MaxClients and ValidateClient, registers it and runs OnConnectedAsync.
    /// Returns null when accepted, otherwise why it was refused. The transport checks its own protocol version and
    /// whether it is running - this does not.
    /// </summary>
    Task<string?> Connect(string connectionId, HandshakeInfo info, IHubPeerChannel channel, CancellationToken cancellationToken);

    HubMethodKind GetMethodKind(string method);

    /// <summary>
    /// Runs a hub method. Hub exceptions are thrown to the caller, which relays them.
    /// </summary>
    Task<HubInvocationResult> Invoke(string connectionId, string method, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a streaming hub method. A Context.Abort inside it disconnects the client once the stream is over - before the
    /// transport sends its end of stream. Prefer <see cref="StartStream"/>, which lets the transport send that first.
    /// </summary>
    IAsyncEnumerable<byte[]> Stream(string connectionId, string method, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a streaming hub method. Send each of <see cref="HubStreamInvocation.Items"/>, then the end of stream (or the
    /// error); then, when <see cref="HubStreamInvocation.AbortRequested"/> - the method called Context.Abort, whether the
    /// stream completed, failed or was cancelled - call <see cref="Disconnect"/>, as after an <see cref="Invoke"/> reply.
    /// </summary>
    HubStreamInvocation StartStream(string connectionId, string method, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken)
        // an endpoint without it disconnects inside the stream itself
        => new(this.Stream(connectionId, method, arguments, cancellationToken));

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
    void Disconnected(string connectionId, IHubPeerChannel channel, HubDisconnect disconnect) => this.Disconnected(connectionId, disconnect);

    /// <summary>
    /// Lets a connected client resume after a dropped link. Returns the secret token to hand the client, or null when the
    /// host doesn't hold dropped clients. A transport with its own resume doesn't call it.
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
    Task<string?> Resume(string resumeToken, string connectionIdPrefix, IHubPeerChannel channel, Func<string, Task> accepted, CancellationToken cancellationToken)
        => Task.FromResult<string?>("This host can't resume connections");

    /// <summary>
    /// The client renamed itself: runs ValidateClient with the new name, then OnRenamedAsync. Returns null when renamed,
    /// otherwise why it was refused - relay it to the client as a <see cref="HubRemoteException"/> of type
    /// <see cref="HubRemoteException.RenameRefused"/>.
    /// </summary>
    Task<string?> Rename(string connectionId, string? name, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a client's session through its channel, then forgets it
    /// </summary>
    Task Disconnect(string connectionId, HubDisconnect disconnect);

    HubConnectedClient? FindClient(string connectionId);

    /// <summary>
    /// The transport opened a media channel for a connected client (it checked its own token first). Raises
    /// <see cref="IHubContext{THub}.MediaChannelOpened"/>, and the channel is closed when that client goes. A channel the
    /// client already had is closed and replaced. Returns false when the client is unknown - the caller closes the channel.
    /// </summary>
    bool MediaChannelOpened(string connectionId, IHubMediaChannel channel);
}
