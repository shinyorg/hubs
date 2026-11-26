using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Transport independent runtime for one hub - admits clients, runs hub methods, delivers pushes, holds dropped clients
/// for resume and tracks groups. Every transport reaches it as an <see cref="IBleHubTransportEndpoint"/> - BLE through
/// <see cref="FrameHostSession"/>.
/// </summary>
internal sealed class HubRuntime : IGroupManager, IBleHubTransportEndpoint
{
    // by connection id: clients with a live link - the ones a transport can reach right now
    readonly ConcurrentDictionary<string, HostPeer> peers = new(StringComparer.OrdinalIgnoreCase);
    // by connection id: every admitted client, including one held for resume (it has no link until it comes back)
    readonly ConcurrentDictionary<string, HostPeer> connections = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> groups = new(StringComparer.Ordinal);
    readonly BleHubRegistration registration;
    readonly IBleHubDispatcher dispatcher;
    readonly IServiceProvider services;
    readonly BleHubProtocolOptions options;
    readonly ILogger? logger;
    readonly Func<object>? createHub;
    CancellationTokenSource lifetime = new();


    public HubRuntime(
        BleHubRegistration registration,
        IBleHubDispatcher dispatcher,
        IServiceProvider services,
        BleHubProtocolOptions options,
        IBleHubSerializer serializer,
        ILogger? logger,
        string? name = null,
        Func<object>? createHub = null
    )
    {
        this.registration = registration;
        this.Name = name ?? registration.HubType.Name;
        this.createHub = createHub;
        this.dispatcher = dispatcher;
        this.services = services;
        this.options = options;
        this.Serializer = serializer;
        this.logger = logger;
    }


    public BleHubRegistration Registration => this.registration;

    /// <summary>
    /// The hub's name in logs and errors - its class name, or the name a handler hub was added under
    /// </summary>
    public string Name { get; }
    public Type ContractType => this.dispatcher.ContractType;
    public IBleHubSerializer Serializer { get; }
    public IGroupManager Groups => this;

    /// <summary>
    /// A stopped hub refuses handshakes - its characteristic may still exist when it shares a service with a running hub
    /// </summary>
    public bool IsRunning { get; set; }

    public string? HostName { get; set; }

    /// <summary>
    /// How long a client whose link dropped is held for it to resume, zero for never (<see cref="UniversalServerOptions.ResumeWindow"/>)
    /// </summary>
    public TimeSpan ResumeWindow { get; set; }

    /// <summary>
    /// Pushes kept for a held client (<see cref="UniversalServerOptions.ResumeBufferSize"/>)
    /// </summary>
    public int ResumeBufferSize { get; set; } = 128;

    /// <summary>
    /// Peers with a live link - the ones a transport can reach right now
    /// </summary>
    public IEnumerable<HostPeer> Peers => this.peers.Values;

    /// <summary>
    /// Admitted clients, held ones included - pushes to a held client are kept for its resume
    /// </summary>
    public IEnumerable<HostPeer> ReadyPeers => this.connections.Values.Where(x => x.Client != null);
    public IReadOnlyList<BleHubConnectedClient> Clients => this.ReadyPeers.Select(x => x.Client!).ToList();

    public event EventHandler<BleHubConnectedClient>? ClientConnected;
    public event EventHandler<BleHubClientDisconnectedEventArgs>? ClientDisconnected;
    public event EventHandler<BleHubClientRenamedEventArgs>? ClientRenamed;
    public event EventHandler<BleHubMediaChannelEventArgs>? MediaChannelOpened;


    /// <summary>
    /// The admitted clients - held ones too - whose last channel <paramref name="channel"/> recognizes: one transport's own
    /// </summary>
    public IReadOnlyList<string> ConnectionIds(Func<IBleHubPeerChannel?, bool> channel)
        => this.connections.Values.Where(x => x.Client != null && channel(x.Channel)).Select(x => x.Id).ToList();


    public BleHubConnectedClient? FindClient(string connectionId)
        => this.connections.TryGetValue(connectionId, out var peer) ? peer.Client : null;


    /// <summary>
    /// A peer by connection id - admitted, or still being admitted
    /// </summary>
    HostPeer? Find(string connectionId)
        => this.connections.TryGetValue(connectionId, out var peer) ? peer : this.peers.TryGetValue(connectionId, out peer) ? peer : null;


    /// <summary>
    /// A link is gone - the peer unsubscribed, disconnected or was removed. A client whose link dropped (rather than one
    /// that left or was disconnected) is held for <see cref="ResumeWindow"/> when it can resume.
    /// </summary>
    public void OnPeerGone(string connectionId, HubDisconnect disconnect)
    {
        if (!this.peers.TryRemove(connectionId, out var peer))
            return;

        if (this.CanHold(peer, disconnect))
            this.Hold(peer, disconnect);
        else
            this.End(peer, disconnect);
    }


    bool CanHold(HostPeer peer, HubDisconnect disconnect)
        => peer.Client != null
           && peer.ResumeToken != null
           && this.ResumeWindow > TimeSpan.Zero
           && disconnect.Reason is HubDisconnectReason.ClientTimeout or HubDisconnectReason.ConnectionFailed;


    /// <summary>
    /// Keeps a client whose link dropped - in ConnectedClients, its groups and MaxClients - and keeps what is pushed to it,
    /// until it resumes or the window runs out
    /// </summary>
    void Hold(HostPeer peer, HubDisconnect disconnect)
    {
        if (peer.BeginHold(disconnect) is not { } generation)
            return;

        // what was under way on the old link can't be answered on it - the transport cancels it, and the client has
        // failed those calls itself
        peer.CloseMedia();
        this.logger?.LogInformation("Holding {Client} on {Hub} for {Window} to resume: {Reason}", peer.Client, this.Name, this.ResumeWindow, disconnect);
        this.ExpireLater(peer, generation);
    }


    /// <summary>
    /// Lets the client go when this hold is still on once the window is up - a later hold has its own timer
    /// </summary>
    void ExpireLater(HostPeer peer, int generation)
    {
        var window = this.ResumeWindow;
        _ = Task.Run(async () =>
        {
            await Task.Delay(window).ConfigureAwait(false);
            if (peer.TryExpireHold(generation) is { } reason)
            {
                this.logger?.LogInformation("{Client} did not resume on {Hub} in time", peer.Client, this.Name);
                this.End(peer, reason);
            }
        });
    }


    /// <summary>
    /// The client is gone for good: OnDisconnectedAsync runs and it leaves its groups
    /// </summary>
    void End(HostPeer peer, HubDisconnect disconnect)
    {
        peer.MarkGone();
        this.peers.TryRemove(new KeyValuePair<string, HostPeer>(peer.Id, peer));
        peer.Abort();
        peer.CloseMedia();
        if (peer.Client == null || !this.connections.TryRemove(new KeyValuePair<string, HostPeer>(peer.Id, peer)))
            return;

        var client = peer.Client;
        this.logger?.LogInformation("Client {Client} left {Hub}: {Reason}", client, this.Name, disconnect);
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
        await Task.WhenAll(targets.Select(peer => this.Deliver(peer, new QueuedSend(FrameKind.Push, eventName, payload), cancellationToken))).ConfigureAwait(false);
    }


    /// <summary>
    /// Sends a push or host rename to one client - or keeps it for the resume when the client is held. Under the peer's
    /// send lock, so a client gets them in the order they were made, replays first.
    /// </summary>
    async Task Deliver(HostPeer peer, QueuedSend item, CancellationToken cancellationToken)
    {
        var overflowed = false;
        try
        {
            await peer.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var buffered = peer.TryBuffer(item, this.ResumeBufferSize);
                if (buffered == HoldBuffer.NotHeld)
                {
                    try
                    {
                        await this.DeliverLocked(peer, item, cancellationToken).ConfigureAwait(false);
                    }
                    catch when (peer.TryBuffer(item, this.ResumeBufferSize) is var late && late != HoldBuffer.NotHeld)
                    {
                        // the link died under this send and the client is held now - it gets this on resume
                        overflowed = late == HoldBuffer.Overflow;
                    }
                }
                else
                {
                    overflowed = buffered == HoldBuffer.Overflow;
                }
            }
            finally
            {
                peer.SendLock.Release();
            }
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Failed to send {Event} to {Client}", item.Name ?? item.Kind.ToString(), peer.Client);
        }

        if (overflowed && peer.TryExpireHold() is { } reason)
        {
            this.logger?.LogInformation("{Client} missed more than {Size} pushes while away - letting it go", peer.Client, this.ResumeBufferSize);
            this.End(peer, reason);
        }
    }


    // under the peer's send lock
    Task DeliverLocked(HostPeer peer, QueuedSend item, CancellationToken cancellationToken)
    {
        if (peer.Channel == null)
            throw new BleHubException("The client has no link");

        return item.Kind == FrameKind.HostRenamed
            ? peer.Channel.HostRenamed(ProtocolSerializer.ReadRename(item.Payload).Name, cancellationToken)
            : peer.Channel.Push(item.Name ?? String.Empty, item.Payload, cancellationToken);
    }


    public async Task Disconnect(string connectionId, HubDisconnect disconnect)
    {
        if (this.Find(connectionId) is not { } peer)
            return;

        // a held client has no link to be told on
        if (peer.IsHeld)
        {
            this.End(peer, disconnect);
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (peer.Channel != null)
                await peer.Channel.Disconnect(disconnect, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogInformation(ex, "Could not deliver disconnect to {Peer}", connectionId);
        }
        // a host-side disconnect is never held
        this.End(peer, disconnect);
    }


    public async Task DisconnectAll(HubDisconnect disconnect)
    {
        var ids = this.connections.Keys
            .Concat(this.peers.Values.Where(x => x.Client == null).Select(x => x.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        await Task.WhenAll(ids.Select(x => this.Disconnect(x, disconnect))).ConfigureAwait(false);
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


    const string NotResumable = "The connection can't be resumed - connect again";


    HostPeer? FindResumable(string? token, Func<HostPeer, bool> transport)
    {
        if (String.IsNullOrEmpty(token))
            return null;

        var bytes = Encoding.UTF8.GetBytes(token);
        foreach (var peer in this.connections.Values)
        {
            if (peer.ResumeToken is { } issued && transport(peer) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(issued), bytes))
                return peer;
        }
        return null;
    }


    /// <summary>
    /// Moves a held client - or one whose drop the host hasn't noticed yet - onto a new link. <paramref name="relink"/>
    /// swaps the link, <paramref name="acknowledge"/> tells the client it is back, then everything kept for it is sent, all
    /// under the send lock so nothing overtakes the replay. False when it can't be resumed (gone, or resuming already).
    /// </summary>
    async Task<bool> Reattach(HostPeer peer, Action relink, Func<Task> acknowledge)
    {
        var (claim, generation) = peer.ClaimForResume();
        if (claim == ResumeClaim.Refused)
            return false;

        if (claim == ResumeClaim.WasAttached)
        {
            // the client saw the drop first - its old link is dead even though nothing reported it yet
            this.peers.TryRemove(new KeyValuePair<string, HostPeer>(peer.Id, peer));
            peer.CloseMedia();
        }

        List<QueuedSend> replay = [];
        var delivered = 0;
        await peer.SendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (peer.Overflowed)
            {
                peer.MarkGone();
                _ = Task.Run(() => this.End(peer, new HubDisconnect(HubDisconnectReason.ClientTimeout, "Missed too many messages while away")));
                return false;
            }

            relink();
            await acknowledge().ConfigureAwait(false);

            replay = peer.TakeBuffered();
            foreach (var item in replay)
            {
                await this.DeliverLocked(peer, item, this.lifetime.Token).ConfigureAwait(false);
                delivered++;
            }

            peer.FinishResume();
            this.logger?.LogInformation("{Client} resumed on {Hub} ({Count} missed pushes replayed)", peer.Client, this.Name, replay.Count);
            return true;
        }
        catch (Exception ex)
        {
            // the new link failed already - hold again so it can try once more
            this.logger?.LogInformation(ex, "Resume of {Client} on {Hub} failed", peer.Client, this.Name);

            // off the link that just failed, with what it didn't get kept for the next try
            this.peers.TryRemove(new KeyValuePair<string, HostPeer>(peer.Id, peer));
            peer.FailResume(replay.Skip(delivered));

            // a hold that started with this resume has no timer yet
            if (claim == ResumeClaim.WasAttached)
                this.ExpireLater(peer, generation);
            return true;
        }
        finally
        {
            peer.SendLock.Release();
        }
    }


    static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));


    /// <summary>
    /// The handshake rules every transport shares. Returns null when the client is in.
    /// </summary>
    async Task<string?> Admit(HostPeer peer, HandshakeInfo info, Action<BleHubConnectedClient>? configure)
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
            var client = new BleHubConnectedClient(peer.Id, info.Name, info.AppVersion, info.Properties ?? new(), 0);
            configure?.Invoke(client);
            peer.Client = client;
            this.connections[peer.Id] = peer;
            try
            {
                await this.RunLifecycle(peer, client, hub => hub.OnConnectedAsync()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.connections.TryRemove(new KeyValuePair<string, HostPeer>(peer.Id, peer));
                peer.Client = null;
                rejection = ex.Message;
            }

            if (rejection == null)
            {
                this.logger?.LogInformation("Client {Client} connected to {Hub}", client, this.Name);
                this.ClientConnected?.Invoke(this, client);
            }
        }
        return rejection;
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

            this.logger?.LogInformation("Client {Client} on {Hub} renamed from '{Previous}'", client, this.Name, previous);
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

        // in order with pushes, and kept for a held client like one
        await Task.WhenAll(this.ReadyPeers.ToList().Select(peer => this.Deliver(peer, new QueuedSend(FrameKind.HostRenamed, null, payload), cancellationToken))).ConfigureAwait(false);
    }


    // ---- media channels ----

    public IBleHubMediaChannel? GetMediaChannel(string connectionId)
        => this.connections.TryGetValue(connectionId, out var peer) && peer.Media is { IsOpen: true } media ? media : null;


    /// <summary>
    /// Gives a connected client its media channel - replacing (and closing) any it had - and raises MediaChannelOpened.
    /// Returns false when the client isn't connected.
    /// </summary>
    public bool AttachMediaChannel(string connectionId, IBleHubMediaChannel channel)
    {
        if (!this.connections.TryGetValue(connectionId, out var peer) || peer.Client is not { } client || peer.IsHeld)
            return false;

        if (channel is FramedMediaChannel framed)
            framed.ConnectionId = peer.Id;

        var previous = peer.SetMedia(channel);
        if (previous != null && !ReferenceEquals(previous, channel))
            _ = previous.DisposeAsync().AsTask();

        // the client may have left between the lookup and SetMedia - OnPeerGone either saw the channel or we see it gone
        if (!this.connections.TryGetValue(connectionId, out var current) || !ReferenceEquals(current, peer) || peer.IsHeld)
        {
            peer.CloseMedia();
            return false;
        }

        _ = channel.Completion.ContinueWith(_ => peer.ClearMedia(channel), TaskScheduler.Default);
        this.logger?.LogInformation("Client {Client} opened a media channel on {Hub}", client, this.Name);
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


    public Task<string?> Connect(string connectionId, HandshakeInfo info, IBleHubPeerChannel channel, CancellationToken cancellationToken)
        => this.Connect(connectionId, info, channel, null);


    /// <summary>
    /// <paramref name="configure"/> fills in what the transport knows about the client (its link) before the hub hears of it
    /// </summary>
    public async Task<string?> Connect(string connectionId, HandshakeInfo info, IBleHubPeerChannel channel, Action<BleHubConnectedClient>? configure)
    {
        if (this.connections.ContainsKey(connectionId))
            return "Connection id is already in use";

        var peer = this.peers.GetOrAdd(connectionId, id => new HostPeer(id));
        if (peer.Channel != null && !ReferenceEquals(peer.Channel, channel))
            return "Connection id is already in use";

        peer.Channel = channel;

        var rejection = await this.Admit(peer, info, configure).ConfigureAwait(false);
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


    public void Disconnected(string connectionId, IBleHubPeerChannel channel, HubDisconnect disconnect)
    {
        // a link the client already replaced by resuming somewhere else
        if (this.peers.TryGetValue(connectionId, out var peer) && ReferenceEquals(peer.Channel, channel))
            this.OnPeerGone(connectionId, disconnect);
    }


    public string? EnableResume(string connectionId)
    {
        if (this.ResumeWindow <= TimeSpan.Zero || !this.connections.TryGetValue(connectionId, out var peer) || peer.Client == null)
            return null;

        return peer.ResumeToken ??= NewToken();
    }


    public Task<string?> Resume(string resumeToken, string connectionIdPrefix, IBleHubPeerChannel channel, Func<string, Task> accepted, CancellationToken cancellationToken)
        // like Connect, the transport checks whether it is running - IsRunning is BLE's
        => this.Resume(resumeToken, connectionIdPrefix, null, channel, accepted);


    /// <summary>
    /// Resumes a connection made through the calling transport: its id starts with <paramref name="connectionIdPrefix"/>
    /// and, when given, <paramref name="ownChannel"/> recognizes its last channel
    /// </summary>
    public async Task<string?> Resume(string resumeToken, string connectionIdPrefix, Func<IBleHubPeerChannel, bool>? ownChannel, IBleHubPeerChannel channel, Func<string, Task> accepted)
    {
        var peer = this.FindResumable(resumeToken, x =>
            x.Channel != null
            && x.Id.StartsWith(connectionIdPrefix, StringComparison.Ordinal)
            && (ownChannel == null || ownChannel(x.Channel)));
        if (peer == null)
            return NotResumable;

        var resumed = await this.Reattach(
            peer,
            () =>
            {
                peer.Channel = channel;
                this.peers[peer.Id] = peer;
            },
            () => accepted(peer.Id)
        ).ConfigureAwait(false);

        return resumed ? null : NotResumable;
    }


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
        // a handler hub has no class to resolve - it stands in for one
        var hub = this.createHub?.Invoke() ?? scope.GetRequiredService(this.registration.HubType);
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
            this.logger?.LogError(ex, "{Hub} lifecycle hook failed for {Client}", this.Name, client);
            throw;
        }
    }
}


/// <summary>
/// A push or host rename on its way to a client - kept while the client is held for resume
/// </summary>
internal sealed record QueuedSend(FrameKind Kind, string? Name, byte[] Payload);


internal enum HoldBuffer
{
    NotHeld,
    Buffered,
    Overflow
}


internal enum ResumeClaim
{
    Refused,
    WasHeld,
    WasAttached
}


internal sealed class HostPeer(string id)
{
    readonly CancellationTokenSource aborted = new();
    readonly object holdGate = new();
    List<QueuedSend>? held;
    HubDisconnect? holdReason;
    HoldState state = HoldState.Attached;
    int generation;

    /// <summary>
    /// The connection id - stays the same when the client resumes on a new link
    /// </summary>
    public string Id { get; } = id;

    /// <summary>
    /// Issued when the host holds dropped clients - the client proves who it is with it when it comes back
    /// </summary>
    public string? ResumeToken { get; set; }

    /// <summary>
    /// The link dropped and the client hasn't resumed (or been let go) yet
    /// </summary>
    public bool IsHeld
    {
        get
        {
            lock (this.holdGate)
                return this.state is HoldState.Held or HoldState.Resuming;
        }
    }

    /// <summary>
    /// More was pushed while held than the buffer keeps
    /// </summary>
    public bool Overflowed { get; private set; }


    /// <summary>
    /// Starts holding - returns this hold's generation, or null when the peer isn't attached
    /// </summary>
    public int? BeginHold(HubDisconnect reason)
    {
        lock (this.holdGate)
        {
            if (this.state != HoldState.Attached)
                return null;

            this.state = HoldState.Held;
            this.held = new();
            this.holdReason = reason;
            this.Overflowed = false;
            return ++this.generation;
        }
    }


    /// <summary>
    /// Keeps <paramref name="item"/> for the resume when held. Call under the send lock.
    /// </summary>
    public HoldBuffer TryBuffer(QueuedSend item, int max)
    {
        lock (this.holdGate)
        {
            if (this.held == null || this.state is HoldState.Attached or HoldState.Gone)
                return HoldBuffer.NotHeld;

            if (this.held.Count >= max)
            {
                this.Overflowed = true;
                return HoldBuffer.Overflow;
            }
            this.held.Add(item);
            return HoldBuffer.Buffered;
        }
    }


    /// <summary>
    /// The hold ends without a resume - returns why the link dropped, or null when it isn't held (resumed, resuming or
    /// gone). With <paramref name="generation"/>, only that hold - a timer from an earlier hold leaves a later one alone.
    /// </summary>
    public HubDisconnect? TryExpireHold(int? generation = null)
    {
        lock (this.holdGate)
        {
            if (this.state != HoldState.Held || (generation != null && generation != this.generation))
                return null;

            this.state = HoldState.Gone;
            this.held = null;
            return this.holdReason ?? new HubDisconnect(HubDisconnectReason.ClientTimeout);
        }
    }


    public (ResumeClaim Claim, int Generation) ClaimForResume()
    {
        lock (this.holdGate)
        {
            switch (this.state)
            {
                case HoldState.Held:
                    this.state = HoldState.Resuming;
                    return (ResumeClaim.WasHeld, this.generation);

                case HoldState.Attached:
                    this.state = HoldState.Resuming;
                    this.held = new();
                    this.holdReason = new HubDisconnect(HubDisconnectReason.ClientTimeout);
                    this.Overflowed = false;
                    return (ResumeClaim.WasAttached, ++this.generation);

                default:
                    return (ResumeClaim.Refused, this.generation);
            }
        }
    }


    public List<QueuedSend> TakeBuffered()
    {
        lock (this.holdGate)
        {
            var items = this.held ?? new();
            this.held = new();
            return items;
        }
    }


    public void FinishResume()
    {
        lock (this.holdGate)
        {
            if (this.state == HoldState.Resuming)
                this.state = HoldState.Attached;
            this.held = null;
        }
    }


    /// <summary>
    /// The new link failed during the resume - back to held, so the client can try again inside the window
    /// </summary>
    public void FailResume(IEnumerable<QueuedSend> undelivered)
    {
        lock (this.holdGate)
        {
            if (this.state == HoldState.Resuming)
            {
                this.state = HoldState.Held;
                var kept = undelivered.ToList();
                kept.AddRange(this.held ?? []);
                this.held = kept;
            }
        }
    }


    public void MarkGone()
    {
        lock (this.holdGate)
        {
            this.state = HoldState.Gone;
            this.held = null;
        }
    }


    enum HoldState
    {
        Attached,
        Held,
        Resuming,
        Gone
    }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public SemaphoreSlim RenameLock { get; } = new(1, 1);
    public BleHubConnectedClient? Client { get; set; }

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
    /// How the hub reaches the client - set by the transport it is connected through
    /// </summary>
    public IBleHubPeerChannel? Channel { get; set; }
    public CancellationToken Aborted => this.aborted.Token;


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
