using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Transport independent runtime for one hub - reassembles client frames, runs hub methods, frames replies and pushes,
/// and tracks groups. The BLE host feeds it frames; tests feed it from an in-memory radio.
/// </summary>
internal sealed class HubRuntime : IGroupManager, IBleHubTransportEndpoint
{
    readonly ConcurrentDictionary<string, HostPeer> peers = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> groups = new(StringComparer.Ordinal);
    readonly BleHubRegistration registration;
    readonly IBleHubDispatcher dispatcher;
    readonly IServiceProvider services;
    readonly BleHubProtocolOptions options;
    readonly ILogger? logger;
    CancellationTokenSource lifetime = new();


    public HubRuntime(
        BleHubRegistration registration,
        IBleHubDispatcher dispatcher,
        IServiceProvider services,
        BleHubProtocolOptions options,
        IBleHubSerializer serializer,
        ILogger? logger
    )
    {
        this.registration = registration;
        this.dispatcher = dispatcher;
        this.services = services;
        this.options = options;
        this.Serializer = serializer;
        this.logger = logger;
    }


    public BleHubRegistration Registration => this.registration;
    public Type ContractType => this.dispatcher.ContractType;
    public IBleHubSerializer Serializer { get; }
    public IGroupManager Groups => this;

    /// <summary>
    /// Sends one frame to a peer - set by whoever owns the transport
    /// </summary>
    public Func<HostPeer, byte[], CancellationToken, Task>? Notify { get; set; }

    /// <summary>
    /// A stopped hub refuses handshakes - its characteristic may still exist when it shares a service with a running hub
    /// </summary>
    public bool IsRunning { get; set; }

    public ushort FileTransferPsm { get; set; }
    public bool FileTransferSecure { get; set; }

    /// <summary>
    /// The BLE media channel listener - 0 when media channels are off or unavailable
    /// </summary>
    public ushort MediaChannelPsm { get; set; }
    public bool MediaChannelSecure { get; set; }
    public int MediaMaxPacketSize { get; set; }
    public string? HostName { get; set; }

    public IEnumerable<HostPeer> Peers => this.peers.Values;
    public IEnumerable<HostPeer> ReadyPeers => this.peers.Values.Where(x => x.Client != null);
    public IReadOnlyList<BleHubConnectedClient> Clients => this.ReadyPeers.Select(x => x.Client!).ToList();

    public event EventHandler<BleHubConnectedClient>? ClientConnected;
    public event EventHandler<BleHubClientDisconnectedEventArgs>? ClientDisconnected;
    public event EventHandler<BleHubClientRenamedEventArgs>? ClientRenamed;
    public event EventHandler<BleHubMediaChannelEventArgs>? MediaChannelOpened;


    public BleHubConnectedClient? FindClient(string connectionId)
        => this.peers.TryGetValue(connectionId, out var peer) ? peer.Client : null;


    /// <summary>
    /// Feeds a frame written by a peer. Returns false when the frame is malformed (the GATT write should be failed).
    /// Complete messages are dispatched in the background.
    /// </summary>
    public bool OnFrame(string peerId, int mtu, object? native, ReadOnlySpan<byte> frame)
    {
        var peer = this.peers.GetOrAdd(peerId, id => new HostPeer(id, native, new MessageReassembler(
            this.options.MaxPayloadSize,
            this.options.ReassemblyTimeout,
            this.options.MaxPartialMessages
        )));
        peer.Mtu = mtu;
        if (native != null)
            peer.Native = native;

        BleHubMessage? message;
        try
        {
            lock (peer.Reassembler)
                message = peer.Reassembler.Add(frame);
        }
        catch (BleHubProtocolException ex)
        {
            this.logger?.LogWarning(ex, "Bad frame from {Peer}", peerId);
            if (ex.MessageId is { } id && peer.Client != null)
                _ = this.SendError(peer, id, nameof(BleHubProtocolException), ex.Message);

            return false;
        }

        if (message?.Kind == FrameKind.Disconnect)
        {
            // handled inline, in the GATT write - the client unsubscribes right after, and that must find it already gone
            // rather than be taken for a dropped link
            this.OnPeerGone(peer.Id, ProtocolSerializer.ReadDisconnect(message.Payload).ToDisconnect(HubDisconnectReason.ClientDisconnect));
        }
        else if (message != null)
        {
            _ = Task.Run(() => this.Dispatch(peer, message));
        }

        return true;
    }


    /// <summary>
    /// The peer unsubscribed, disconnected or was removed
    /// </summary>
    public void OnPeerGone(string peerId, HubDisconnect disconnect)
    {
        if (!this.peers.TryRemove(peerId, out var peer))
            return;

        peer.Abort();
        peer.CloseMedia();
        if (peer.Client == null)
            return;

        var client = peer.Client;
        this.logger?.LogInformation("Client {Client} left {Hub}: {Reason}", client, this.registration.HubType.Name, disconnect);
        _ = Task.Run(async () =>
        {
            try
            {
                await this.RunLifecycle(peer, client, hub => hub.OnDisconnectedAsync(disconnect)).ConfigureAwait(false);
            }
            finally
            {
                foreach (var group in this.groups.Values)
                    group.TryRemove(client.Id, out _);

                this.ClientDisconnected?.Invoke(this, new BleHubClientDisconnectedEventArgs(client, disconnect));
            }
        });
    }


    public async Task Push(string eventName, byte[] payload, IEnumerable<HostPeer> recipients, CancellationToken cancellationToken)
    {
        var targets = recipients.Where(x => x.Client != null).Distinct().ToList();
        await Task.WhenAll(targets.Select(async peer =>
        {
            try
            {
                if (peer.Channel == null)
                {
                    await this.Send(peer, FrameKind.Push, peer.NextHostMessageId(), eventName, payload, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // same per-peer lock as BLE, so concurrent pushes reach a client in the order they were made
                    await peer.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await peer.Channel.Push(eventName, payload, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        peer.SendLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Failed to push {Event} to {Client}", eventName, peer.Client);
            }
        })).ConfigureAwait(false);
    }


    public async Task Disconnect(string connectionId, HubDisconnect disconnect)
    {
        if (!this.peers.TryGetValue(connectionId, out var peer))
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (peer.Channel != null)
            {
                await peer.Channel.Disconnect(disconnect, cts.Token).ConfigureAwait(false);
            }
            else
            {
                var payload = ProtocolSerializer.Serialize(DisconnectInfo.From(disconnect));
                await this.Send(peer, FrameKind.Disconnect, peer.NextHostMessageId(), null, payload, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.logger?.LogInformation(ex, "Could not deliver disconnect to {Peer}", connectionId);
        }
        this.OnPeerGone(connectionId, disconnect);
    }


    public async Task DisconnectAll(HubDisconnect disconnect)
    {
        await Task.WhenAll(this.peers.Keys.ToList().Select(x => this.Disconnect(x, disconnect))).ConfigureAwait(false);
        this.lifetime.Cancel();
        this.lifetime = new CancellationTokenSource();
    }


    public IEnumerable<HostPeer> PeersInGroups(IEnumerable<string> groupNames)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in groupNames)
        {
            if (this.groups.TryGetValue(name, out var members))
                ids.UnionWith(members.Keys);
        }
        return this.ReadyPeers.Where(x => ids.Contains(x.Id));
    }


    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        this.groups.GetOrAdd(groupName, _ => new(StringComparer.OrdinalIgnoreCase))[connectionId] = 0;
        return Task.CompletedTask;
    }


    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        if (this.groups.TryGetValue(groupName, out var members))
            members.TryRemove(connectionId, out _);

        return Task.CompletedTask;
    }


    public IReadOnlyList<string> GetMembers(string groupName)
        => this.groups.TryGetValue(groupName, out var members) ? members.Keys.ToList() : [];


    async Task Dispatch(HostPeer peer, BleHubMessage message)
    {
        try
        {
            switch (message.Kind)
            {
                case FrameKind.Handshake:
                    await this.OnHandshake(peer, message).ConfigureAwait(false);
                    break;

                case FrameKind.Cancel:
                    peer.Cancel(message.MessageId);
                    break;

                case FrameKind.Invoke:
                case FrameKind.StreamInvoke:
                    await this.OnInvoke(peer, message).ConfigureAwait(false);
                    break;

                case FrameKind.Rename:
                    await this.OnRename(peer, message).ConfigureAwait(false);
                    break;

                default:
                    await this.SendError(peer, message.MessageId, nameof(BleHubProtocolException), $"Unexpected message kind {message.Kind}").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            this.logger?.LogError(ex, "Unhandled error dispatching {Kind} {Id} from {Peer}", message.Kind, message.MessageId, peer.Id);
        }
    }


    async Task OnHandshake(HostPeer peer, BleHubMessage message)
    {
        var info = ProtocolSerializer.ReadHandshake(message.Payload);
        string? rejection;

        if (!this.IsRunning)
            rejection = "Hub is not running";
        else if (info.ProtocolVersion != FrameCodec.ProtocolVersion)
            rejection = $"Unsupported protocol version {info.ProtocolVersion}";
        else
            rejection = await this.Admit(peer, info).ConfigureAwait(false);

        var media = rejection == null && this.MediaChannelPsm > 0;
        if (media)
            peer.MediaToken ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        var ack = new HandshakeAck(
            rejection == null,
            rejection,
            this.HostName,
            this.FileTransferPsm,
            this.FileTransferSecure,
            media ? this.MediaChannelPsm : (ushort)0,
            media && this.MediaChannelSecure,
            media ? peer.MediaToken : null,
            media ? this.MediaMaxPacketSize : 0
        );
        await this.Send(peer, FrameKind.HandshakeAck, message.MessageId, null, ProtocolSerializer.Serialize(ack), this.lifetime.Token).ConfigureAwait(false);

        if (rejection != null)
        {
            this.logger?.LogInformation("Rejected client {Peer}: {Reason}", peer.Id, rejection);
            this.peers.TryRemove(peer.Id, out _);
        }
    }


    /// <summary>
    /// The handshake rules every transport shares. Returns null when the client is in.
    /// </summary>
    async Task<string?> Admit(HostPeer peer, HandshakeInfo info)
    {
        var hubOptions = this.registration.Options;
        string? rejection;

        if (peer.Client == null && hubOptions.MaxClients is { } max && this.Clients.Count >= max)
            rejection = "Host is full";
        else
            rejection = hubOptions.ValidateClient?.Invoke(info);

        if (rejection == null && peer.Client == null)
        {
            // registered and OnConnectedAsync completed before the ack goes out, so the client is never told it is in
            // before the hub knows about it (and has had a chance to put it in groups)
            var client = new BleHubConnectedClient(peer.Id, info.Name, info.AppVersion, info.Properties ?? new(), peer.Mtu)
            {
                Peripheral = peer.Native as IPeripheral
            };
            peer.Client = client;
            try
            {
                await this.RunLifecycle(peer, client, hub => hub.OnConnectedAsync()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                peer.Client = null;
                rejection = ex.Message;
            }

            if (rejection == null)
            {
                this.logger?.LogInformation("Client {Client} connected to {Hub}", client, this.registration.HubType.Name);
                this.ClientConnected?.Invoke(this, client);
            }
        }
        return rejection;
    }


    async Task OnRename(HostPeer peer, BleHubMessage message)
    {
        if (peer.Client is not { } client)
        {
            await this.SendError(peer, message.MessageId, nameof(BleHubProtocolException), "Handshake required").ConfigureAwait(false);
            return;
        }

        var rejection = await this.Rename(peer, client, ProtocolSerializer.ReadRename(message.Payload).Name).ConfigureAwait(false);
        if (rejection == null)
            await this.Send(peer, FrameKind.Completion, message.MessageId, null, ReadOnlyMemory<byte>.Empty, this.lifetime.Token).ConfigureAwait(false);
        else
            await this.SendError(peer, message.MessageId, BleHubRemoteException.RenameRefused, rejection).ConfigureAwait(false);
    }


    /// <summary>
    /// The rename rules every transport shares: ValidateClient sees the new name, then OnRenamedAsync runs. Returns null
    /// when the client has its new name.
    /// </summary>
    async Task<string?> Rename(HostPeer peer, BleHubConnectedClient client, string? name)
    {
        // one rename at a time per client, so the previous name each hook sees is the one it replaced
        await peer.RenameLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = client.Name;
            if (String.Equals(previous, name, StringComparison.Ordinal))
                return null;

            var info = new HandshakeInfo(FrameCodec.ProtocolVersion, name, client.AppVersion, new Dictionary<string, string>(client.Properties));
            if (this.registration.Options.ValidateClient?.Invoke(info) is { } rejection)
                return rejection;

            client.Name = name;
            try
            {
                await this.RunLifecycle(peer, client, hub => hub.OnRenamedAsync(previous)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                client.Name = previous;
                return ex.Message;
            }

            this.logger?.LogInformation("Client {Client} on {Hub} renamed from '{Previous}'", client, this.registration.HubType.Name, previous);
            this.ClientRenamed?.Invoke(this, new BleHubClientRenamedEventArgs(client, previous));
            return null;
        }
        finally
        {
            peer.RenameLock.Release();
        }
    }


    /// <summary>
    /// The host has a new name - tell every connected client, over whichever transport it is on
    /// </summary>
    public async Task SetHostName(string? hostName, CancellationToken cancellationToken)
    {
        this.HostName = hostName;
        var payload = ProtocolSerializer.Serialize(new RenameInfo(hostName));

        await Task.WhenAll(this.ReadyPeers.ToList().Select(async peer =>
        {
            try
            {
                if (peer.Channel == null)
                {
                    await this.Send(peer, FrameKind.HostRenamed, peer.NextHostMessageId(), null, payload, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // in order with pushes
                    await peer.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await peer.Channel.HostRenamed(hostName, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        peer.SendLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Failed to tell {Client} the host's new name", peer.Client);
            }
        })).ConfigureAwait(false);
    }


    async Task OnInvoke(HostPeer peer, BleHubMessage message)
    {
        var client = peer.Client;
        if (client == null)
        {
            await this.SendError(peer, message.MessageId, nameof(BleHubProtocolException), "Handshake required").ConfigureAwait(false);
            return;
        }

        var method = message.Name ?? "";
        var kind = this.dispatcher.GetMethodKind(method);
        var expected = message.Kind == FrameKind.StreamInvoke ? BleHubMethodKind.Stream : BleHubMethodKind.Invoke;
        if (kind != expected)
        {
            var error = kind == BleHubMethodKind.NotFound
                ? $"{this.registration.HubType.Name} has no method '{method}'"
                : $"'{method}' is {(kind == BleHubMethodKind.Stream ? "a stream" : "not a stream")}";
            await this.SendError(peer, message.MessageId, "HubMethodNotFound", error).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token, peer.Aborted);
        peer.Track(message.MessageId, cts);
        string? abortReason = null;
        var abortRequested = false;
        try
        {
            await using var scope = this.services.CreateAsyncScope();
            var hub = this.CreateHub(scope.ServiceProvider, peer, client, reason =>
            {
                // deferred so the caller still gets this method's reply
                abortRequested = true;
                abortReason = reason;
            });
            var args = new BleHubArgumentReader(this.Serializer, message.Payload);

            if (kind == BleHubMethodKind.Invoke)
            {
                var result = await this.dispatcher.Invoke(hub, method, args, this.Serializer, cts.Token).ConfigureAwait(false);
                await this.Send(peer, FrameKind.Completion, message.MessageId, null, result ?? ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await foreach (var item in this.dispatcher.Stream(hub, method, args, this.Serializer, cts.Token).WithCancellation(cts.Token).ConfigureAwait(false))
                    await this.Send(peer, FrameKind.StreamItem, message.MessageId, null, item, cts.Token).ConfigureAwait(false);

                await this.Send(peer, FrameKind.StreamEnd, message.MessageId, null, ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // the client cancelled, left, or the host is stopping - nobody is waiting for a reply
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "{Hub}.{Method} failed", this.registration.HubType.Name, method);
            await this.SendError(peer, message.MessageId, ex.GetType().Name, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            peer.Untrack(message.MessageId);
        }

        if (abortRequested)
            await this.Disconnect(peer.Id, new HubDisconnect(HubDisconnectReason.ServerDisconnect, abortReason)).ConfigureAwait(false);
    }


    // ---- media channels ----

    public IBleHubMediaChannel? GetMediaChannel(string connectionId)
        => this.peers.TryGetValue(connectionId, out var peer) && peer.Media is { IsOpen: true } media ? media : null;


    /// <summary>
    /// Finds the BLE client a media token was issued to
    /// </summary>
    public string? FindMediaToken(ReadOnlySpan<byte> token)
    {
        foreach (var peer in this.peers.Values)
        {
            if (peer.Client != null && peer.Channel == null && peer.MediaToken is { } issued)
            {
                var bytes = Encoding.UTF8.GetBytes(issued);
                if (CryptographicOperations.FixedTimeEquals(bytes, token))
                    return peer.Id;
            }
        }
        return null;
    }


    /// <summary>
    /// Gives a connected client its media channel - replacing (and closing) any it had - and raises MediaChannelOpened.
    /// Returns false when the client isn't connected.
    /// </summary>
    public bool AttachMediaChannel(string connectionId, IBleHubMediaChannel channel)
    {
        if (!this.peers.TryGetValue(connectionId, out var peer) || peer.Client is not { } client)
            return false;

        if (channel is FramedMediaChannel framed)
            framed.ConnectionId = peer.Id;

        var previous = peer.SetMedia(channel);
        if (previous != null && !ReferenceEquals(previous, channel))
            _ = previous.DisposeAsync().AsTask();

        // the client may have left between the lookup and SetMedia - OnPeerGone either saw the channel or we see it gone
        if (!this.peers.TryGetValue(connectionId, out var current) || !ReferenceEquals(current, peer))
        {
            peer.CloseMedia();
            return false;
        }

        _ = channel.Completion.ContinueWith(_ => peer.ClearMedia(channel), TaskScheduler.Default);
        this.logger?.LogInformation("Client {Client} opened a media channel on {Hub}", client, this.registration.HubType.Name);
        try
        {
            this.MediaChannelOpened?.Invoke(this, new BleHubMediaChannelEventArgs(client, channel));
        }
        catch (Exception ex)
        {
            this.logger?.LogError(ex, "MediaChannelOpened handler failed for {Client}", client);
        }
        return true;
    }


    bool IBleHubTransportEndpoint.MediaChannelOpened(string connectionId, IBleHubMediaChannel channel)
        => this.AttachMediaChannel(connectionId, channel);


    // ---- IBleHubTransportEndpoint: clients connected through another transport ----

    public BleHubMethodKind GetMethodKind(string method) => this.dispatcher.GetMethodKind(method);


    public async Task<string?> Connect(string connectionId, HandshakeInfo info, IBleHubPeerChannel channel, CancellationToken cancellationToken)
    {
        var peer = this.peers.GetOrAdd(connectionId, id => new HostPeer(id, null, new MessageReassembler(
            this.options.MaxPayloadSize,
            this.options.ReassemblyTimeout,
            this.options.MaxPartialMessages
        )));
        if (peer.Channel != null && !ReferenceEquals(peer.Channel, channel))
            return "Connection id is already in use";

        peer.Channel = channel;
        peer.Mtu = 0;

        var rejection = await this.Admit(peer, info).ConfigureAwait(false);
        if (rejection != null)
        {
            this.logger?.LogInformation("Rejected client {Peer}: {Reason}", peer.Id, rejection);
            this.peers.TryRemove(peer.Id, out _);
        }
        return rejection;
    }


    public async Task<BleHubInvocationResult> Invoke(string connectionId, string method, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken)
    {
        var (peer, client) = this.GetExternal(connectionId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token, peer.Aborted, cancellationToken);
        string? abortReason = null;
        var abortRequested = false;

        await using var scope = this.services.CreateAsyncScope();
        var hub = this.CreateHub(scope.ServiceProvider, peer, client, reason =>
        {
            // the transport sends the reply first, then disconnects
            abortRequested = true;
            abortReason = reason;
        });
        var result = await this.dispatcher
            .Invoke(hub, method, new BleHubArgumentReader(this.Serializer, arguments), this.Serializer, cts.Token)
            .ConfigureAwait(false);

        return new BleHubInvocationResult(result, abortRequested, abortReason);
    }


    public async IAsyncEnumerable<byte[]> Stream(string connectionId, string method, ReadOnlyMemory<byte> arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (peer, client) = this.GetExternal(connectionId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token, peer.Aborted, cancellationToken);
        string? abortReason = null;
        var abortRequested = false;

        await using (var scope = this.services.CreateAsyncScope())
        {
            var hub = this.CreateHub(scope.ServiceProvider, peer, client, reason =>
            {
                abortRequested = true;
                abortReason = reason;
            });
            var items = this.dispatcher.Stream(hub, method, new BleHubArgumentReader(this.Serializer, arguments), this.Serializer, cts.Token);
            await foreach (var item in items.WithCancellation(cts.Token).ConfigureAwait(false))
                yield return item;
        }

        if (abortRequested)
            await this.Disconnect(connectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, abortReason)).ConfigureAwait(false);
    }


    public void Disconnected(string connectionId, HubDisconnect disconnect) => this.OnPeerGone(connectionId, disconnect);


    public Task<string?> Rename(string connectionId, string? name, CancellationToken cancellationToken)
    {
        var (peer, client) = this.GetExternal(connectionId);
        return this.Rename(peer, client, name);
    }


    (HostPeer Peer, BleHubConnectedClient Client) GetExternal(string connectionId)
    {
        if (!this.peers.TryGetValue(connectionId, out var peer) || peer.Channel == null || peer.Client is not { } client)
            throw new BleHubProtocolException("Handshake required");

        return (peer, client);
    }


    object CreateHub(IServiceProvider scope, HostPeer peer, BleHubConnectedClient client, Action<string?>? abort = null)
    {
        var hub = scope.GetRequiredService(this.registration.HubType);
        abort ??= reason => _ = this.Disconnect(peer.Id, new HubDisconnect(HubDisconnectReason.ServerDisconnect, reason));
        ((IBleHubInternal)hub).Initialize(new BleHubCallerContext(client, abort, peer.Aborted), this);
        return hub;
    }


    async Task RunLifecycle(HostPeer peer, BleHubConnectedClient client, Func<IBleHubInternal, Task> action)
    {
        await using var scope = this.services.CreateAsyncScope();
        var hub = (IBleHubInternal)this.CreateHub(scope.ServiceProvider, peer, client);
        try
        {
            await action(hub).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogError(ex, "{Hub} lifecycle hook failed for {Client}", this.registration.HubType.Name, client);
            throw;
        }
    }


    async Task SendError(HostPeer peer, ushort messageId, string type, string error)
    {
        try
        {
            var payload = ProtocolSerializer.Serialize(new RemoteError(type, error));
            await this.Send(peer, FrameKind.Error, messageId, null, payload, this.lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Failed to send error to {Peer}", peer.Id);
        }
    }


    async Task Send(HostPeer peer, FrameKind kind, ushort messageId, string? name, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var notify = this.Notify ?? throw new BleHubException("Hub host is not running");
        var frames = FrameCodec.Encode(kind, messageId, name, payload.Span, FrameCodec.GetFrameSize(peer.Mtu));

        // whole messages go out under the lock so frames for one peer never interleave
        await peer.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var frame in frames)
                await notify(peer, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            peer.SendLock.Release();
        }
    }
}


internal sealed class HostPeer(string id, object? native, MessageReassembler reassembler)
{
    readonly ConcurrentDictionary<ushort, CancellationTokenSource> inFlight = new();
    readonly CancellationTokenSource aborted = new();
    int nextHostId;

    public string Id { get; } = id;
    public object? Native { get; set; } = native;
    public int Mtu { get; set; } = 23;
    public MessageReassembler Reassembler { get; } = reassembler;
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public SemaphoreSlim RenameLock { get; } = new(1, 1);
    public BleHubConnectedClient? Client { get; set; }

    /// <summary>
    /// Issued in the handshake ack when the host serves BLE media channels - the client's first frame on that channel
    /// </summary>
    public string? MediaToken { get; set; }

    IBleHubMediaChannel? media;
    public IBleHubMediaChannel? Media => Volatile.Read(ref this.media);
    public IBleHubMediaChannel? SetMedia(IBleHubMediaChannel channel) => Interlocked.Exchange(ref this.media, channel);
    public void ClearMedia(IBleHubMediaChannel channel) => Interlocked.CompareExchange(ref this.media, null, channel);

    public void CloseMedia()
    {
        if (Interlocked.Exchange(ref this.media, null) is { } channel)
            _ = channel.DisposeAsync().AsTask();
    }

    /// <summary>
    /// Set when the client is connected through another transport rather than BLE
    /// </summary>
    public IBleHubPeerChannel? Channel { get; set; }
    public CancellationToken Aborted => this.aborted.Token;


    /// <summary>
    /// Host allocated ids have the high bit set so they never collide with client ids
    /// </summary>
    public ushort NextHostMessageId() => (ushort)(0x8000 | (Interlocked.Increment(ref this.nextHostId) & 0x7FFF));

    public void Track(ushort id, CancellationTokenSource cts) => this.inFlight[id] = cts;
    public void Untrack(ushort id) => this.inFlight.TryRemove(id, out _);

    public void Cancel(ushort id)
    {
        if (this.inFlight.TryRemove(id, out var cts))
            TryCancel(cts);
    }

    public void Abort() => TryCancel(this.aborted);

    static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}


/// <summary>
/// A client left a hub
/// </summary>
/// <param name="Client">Who left</param>
/// <param name="Disconnect">Why</param>
public sealed record BleHubClientDisconnectedEventArgs(BleHubConnectedClient Client, HubDisconnect Disconnect)
{
    /// <summary>
    /// <see cref="HubDisconnect.Description"/>
    /// </summary>
    public string Reason => this.Disconnect.Description;
}


/// <summary>
/// A connected client changed its name
/// </summary>
/// <param name="Client">Who renamed - <see cref="BleHubConnectedClient.Name"/> is the new name</param>
/// <param name="PreviousName">The name it had before</param>
public sealed record BleHubClientRenamedEventArgs(BleHubConnectedClient Client, string? PreviousName);
