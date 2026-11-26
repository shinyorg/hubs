using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hosting;
using Shiny.UniversalHubs.Protocol;
using Shiny.UniversalHubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// The host side of the frame protocol (<see cref="FrameCodec"/>) for one hub: reassembles each link's frames, and runs
/// handshakes, calls, renames, resumes and disconnects through the hub's transport endpoint - BLE is a transport like any
/// other. The BLE host feeds it GATT writes; tests feed it from an in-memory radio.
/// </summary>
internal sealed class FrameHostSession
{
    // by link: the BLE central frames come from - a client that resumed is on a new link with the same connection id
    readonly ConcurrentDictionary<string, FrameHostLink> links = new(StringComparer.OrdinalIgnoreCase);
    readonly HubRuntime endpoint;
    readonly HubProtocolOptions options;
    readonly ILogger? logger;
    CancellationTokenSource lifetime = new();


    public FrameHostSession(HubRuntime endpoint, HubProtocolOptions options, ILogger? logger)
    {
        this.endpoint = endpoint;
        this.options = options;
        this.logger = logger;
    }


    public HubRuntime Endpoint => this.endpoint;

    /// <summary>
    /// Sends one frame to a link - set by whoever owns the radio
    /// </summary>
    public Func<FrameHostLink, byte[], CancellationToken, Task>? Notify { get; set; }

    /// <summary>
    /// Whether the hub takes handshakes over these frames - the hub's own state unless the transport says otherwise
    /// </summary>
    public Func<bool>? IsRunning { get; set; }

    /// <summary>
    /// The transport the session's clients are connected through
    /// </summary>
    public IHubTransport? Transport { get; init; }

    bool Running => this.IsRunning?.Invoke() ?? this.endpoint.IsRunning;

    public ushort FileTransferPsm { get; set; }
    public bool FileTransferSecure { get; set; }

    /// <summary>
    /// The BLE media channel listener - 0 when media channels are off or unavailable
    /// </summary>
    public ushort MediaChannelPsm { get; set; }
    public bool MediaChannelSecure { get; set; }
    public int MediaMaxPacketSize { get; set; }

    /// <summary>
    /// Links with a live connection or one still hand-shaking
    /// </summary>
    public IEnumerable<FrameHostLink> Links => this.links.Values;


    /// <summary>
    /// The client on a link right now - its connection id differs from the link once it has resumed on a new one
    /// </summary>
    public HubConnectedClient? FindClientOnLink(string linkId)
        => this.links.TryGetValue(linkId, out var link) && link.ConnectionId is { } id ? this.endpoint.FindClient(id) : null;


    /// <summary>
    /// Feeds a frame written by a link. Returns false when the frame is malformed (the GATT write should be failed).
    /// Complete messages are dispatched in the background.
    /// </summary>
    public bool OnFrame(string linkId, int mtu, object? native, ReadOnlySpan<byte> frame)
    {
        var link = this.links.GetOrAdd(linkId, id => new FrameHostLink(this, id, this.CreateReassembler()));
        link.Mtu = mtu;
        if (native != null)
            link.Native = native;

        HubMessage? message;
        try
        {
            lock (link.Reassembler)
                message = link.Reassembler.Add(frame);
        }
        catch (HubProtocolException ex)
        {
            this.logger?.LogWarning(ex, "Bad frame from {Peer}", linkId);
            if (ex.MessageId is { } id && link.ConnectionId != null)
                _ = this.SendError(link, id, nameof(HubProtocolException), ex.Message);

            return false;
        }

        if (message?.Kind == FrameKind.Disconnect)
        {
            // handled inline, in the GATT write - the client unsubscribes right after, and that must find it already gone
            // rather than be taken for a dropped link
            this.OnPeerGone(link.LinkId, ProtocolSerializer.ReadDisconnect(message.Payload).ToDisconnect(HubDisconnectReason.ClientDisconnect));
        }
        else if (message != null)
        {
            _ = Task.Run(() => this.Dispatch(link, message));
        }

        return true;
    }


    /// <summary>
    /// A link is gone - the client unsubscribed, disconnected or was removed. The hub holds a client whose link dropped
    /// (rather than one that left) when it can resume.
    /// </summary>
    public void OnPeerGone(string linkId, HubDisconnect disconnect)
    {
        if (!this.links.TryRemove(linkId, out var link))
            return;

        link.CancelCalls();
        if (link.ConnectionId is { } id)
            this.endpoint.Disconnected(id, link.Channel, disconnect);
    }


    /// <summary>
    /// Tells links that never finished their handshake to go, and stops answering the ones still under way. The hub's own
    /// clients are disconnected through the hub.
    /// </summary>
    public async Task DisconnectPending(HubDisconnect disconnect)
    {
        var pending = this.links.Values.Where(x => x.ConnectionId == null).ToList();
        await Task.WhenAll(pending.Select(async link =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await this.Send(link, FrameKind.Disconnect, link.NextHostMessageId(), null, ProtocolSerializer.Serialize(DisconnectInfo.From(disconnect)), cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogInformation(ex, "Could not deliver disconnect to {Peer}", link.LinkId);
            }
            this.links.TryRemove(new KeyValuePair<string, FrameHostLink>(link.LinkId, link));
        })).ConfigureAwait(false);

        this.lifetime.Cancel();
        this.lifetime = new CancellationTokenSource();
    }


    /// <summary>
    /// Finds the connected client a media token was issued to
    /// </summary>
    public string? FindMediaToken(ReadOnlySpan<byte> token)
    {
        foreach (var link in this.links.Values)
        {
            if (link.ConnectionId is { } id && link.MediaToken is { } issued && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(issued), token))
                return id;
        }
        return null;
    }


    internal void Forget(FrameHostLink link)
    {
        this.links.TryRemove(new KeyValuePair<string, FrameHostLink>(link.LinkId, link));
        link.CancelCalls();
    }


    public bool AttachMediaChannel(string connectionId, IHubMediaChannel channel) => this.endpoint.AttachMediaChannel(connectionId, channel);


    async Task Dispatch(FrameHostLink link, HubMessage message)
    {
        try
        {
            switch (message.Kind)
            {
                case FrameKind.Handshake:
                    await this.OnHandshake(link, message).ConfigureAwait(false);
                    break;

                case FrameKind.Resume:
                    await this.OnResume(link, message).ConfigureAwait(false);
                    break;

                case FrameKind.Cancel:
                    link.Cancel(message.MessageId);
                    break;

                case FrameKind.Invoke:
                case FrameKind.StreamInvoke:
                    await this.OnInvoke(link, message).ConfigureAwait(false);
                    break;

                case FrameKind.Rename:
                    await this.OnRename(link, message).ConfigureAwait(false);
                    break;

                default:
                    await this.SendError(link, message.MessageId, nameof(HubProtocolException), $"Unexpected message kind {message.Kind}").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            this.logger?.LogError(ex, "Unhandled error dispatching {Kind} {Id} from {Peer}", message.Kind, message.MessageId, link.LinkId);
        }
    }


    async Task OnHandshake(FrameHostLink link, HubMessage message)
    {
        var info = ProtocolSerializer.ReadHandshake(message.Payload);
        string? rejection = null;

        if (!this.Running)
            rejection = "Hub is not running";
        else if (info.ProtocolVersion != FrameCodec.ProtocolVersion)
            rejection = $"Unsupported protocol version {info.ProtocolVersion}";
        else if (link.ConnectionId == null)
        {
            // the link is the connection id - a client that resumes later keeps it on its new link
            rejection = await this.endpoint.Connect(link.LinkId, info, link.Channel, client =>
            {
                client.Mtu = link.Mtu;
                client.Peripheral = link.Native as Shiny.BluetoothLE.Hosting.IPeripheral;
            }).ConfigureAwait(false);

            if (rejection == null)
            {
                link.ConnectionId = link.LinkId;

                // only a client that can come back is held
                if (info.Resumable)
                    link.ResumeToken = this.endpoint.EnableResume(link.LinkId);
            }
        }

        var ack = this.CreateAck(link, rejection);
        await this.Send(link, FrameKind.HandshakeAck, message.MessageId, null, ProtocolSerializer.Serialize(ack), this.lifetime.Token).ConfigureAwait(false);

        if (rejection != null)
        {
            this.logger?.LogInformation("Rejected client {Peer}: {Reason}", link.LinkId, rejection);
            this.links.TryRemove(new KeyValuePair<string, FrameHostLink>(link.LinkId, link));
        }
    }


    HandshakeAck CreateAck(FrameHostLink link, string? rejection)
    {
        var media = rejection == null && this.MediaChannelPsm > 0;
        if (media)
            link.MediaToken ??= NewToken();

        var window = this.endpoint.ResumeWindow;
        var resume = rejection == null && link.ResumeToken != null && window > TimeSpan.Zero;
        return new HandshakeAck(
            rejection == null,
            rejection,
            this.endpoint.HostName,
            this.FileTransferPsm,
            this.FileTransferSecure,
            media ? this.MediaChannelPsm : (ushort)0,
            media && this.MediaChannelSecure,
            media ? link.MediaToken : null,
            media ? this.MediaMaxPacketSize : 0,
            resume ? link.ResumeToken : null,
            resume ? (int)Math.Ceiling(window.TotalSeconds) : 0
        );
    }


    /// <summary>
    /// A client back on a new link with its resume token. Accepted, the held connection moves onto this link - same
    /// connection id, groups and Items - the ack goes out, then the pushes it missed.
    /// </summary>
    async Task OnResume(FrameHostLink link, HubMessage message)
    {
        ResumeInfo? info = null;
        try
        {
            info = ProtocolSerializer.ReadResume(message.Payload);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Malformed resume from {Peer}", link.LinkId);
        }

        string? rejection;
        if (info == null)
            rejection = "Malformed resume";
        else if (!this.Running)
            rejection = "Hub is not running";
        else if (info.ProtocolVersion != FrameCodec.ProtocolVersion)
            rejection = $"Unsupported protocol version {info.ProtocolVersion}";
        else if (link.ConnectionId != null)
            rejection = "Already connected";
        else
        {
            rejection = await this.endpoint.Resume(
                info.Token,
                // only a connection made over these frames resumes over them
                "",
                channel => channel is FrameHostLink.LinkChannel own && ReferenceEquals(own.Session, this),
                link.Channel,
                connectionId =>
                {
                    // the new link takes over: frames from this central now belong to the held connection, and whatever
                    // the old link still had under way is over
                    foreach (var old in this.links.Values.Where(x => x != link && x.ConnectionId == connectionId).ToList())
                    {
                        this.links.TryRemove(new KeyValuePair<string, FrameHostLink>(old.LinkId, old));
                        old.CancelCalls();
                    }
                    link.ConnectionId = connectionId;
                    link.ResumeToken = info.Token;
                    if (this.endpoint.FindClient(connectionId) is { } client)
                        client.Peripheral = link.Native as Shiny.BluetoothLE.Hosting.IPeripheral;

                    return this.SendLocked(link, FrameKind.HandshakeAck, message.MessageId, null, ProtocolSerializer.Serialize(this.CreateAck(link, null)), this.lifetime.Token);
                }
            ).ConfigureAwait(false);

            if (rejection == null)
                return;
        }

        this.logger?.LogInformation("Could not resume {Peer}: {Reason}", link.LinkId, rejection);
        await this.Send(link, FrameKind.HandshakeAck, message.MessageId, null, ProtocolSerializer.Serialize(new HandshakeAck(false, rejection, this.endpoint.HostName, 0, false)), this.lifetime.Token).ConfigureAwait(false);
        this.links.TryRemove(new KeyValuePair<string, FrameHostLink>(link.LinkId, link));
    }


    async Task OnRename(FrameHostLink link, HubMessage message)
    {
        if (link.ConnectionId is not { } id)
        {
            await this.SendError(link, message.MessageId, nameof(HubProtocolException), "Handshake required").ConfigureAwait(false);
            return;
        }

        string? rejection;
        try
        {
            rejection = await this.endpoint.Rename(id, ProtocolSerializer.ReadRename(message.Payload).Name, this.lifetime.Token).ConfigureAwait(false);
        }
        catch (HubProtocolException ex)
        {
            await this.SendError(link, message.MessageId, nameof(HubProtocolException), ex.Message).ConfigureAwait(false);
            return;
        }

        if (rejection == null)
            await this.Send(link, FrameKind.Completion, message.MessageId, null, ReadOnlyMemory<byte>.Empty, this.lifetime.Token).ConfigureAwait(false);
        else
            await this.SendError(link, message.MessageId, HubRemoteException.RenameRefused, rejection).ConfigureAwait(false);
    }


    async Task OnInvoke(FrameHostLink link, HubMessage message)
    {
        if (link.ConnectionId is not { } connectionId)
        {
            await this.SendError(link, message.MessageId, nameof(HubProtocolException), "Handshake required").ConfigureAwait(false);
            return;
        }

        var method = message.Name ?? "";
        var kind = this.endpoint.GetMethodKind(method);
        var expected = message.Kind == FrameKind.StreamInvoke ? HubMethodKind.Stream : HubMethodKind.Invoke;
        if (kind != expected)
        {
            var error = kind == HubMethodKind.NotFound
                ? $"{this.endpoint.Name} has no method '{method}'"
                : $"'{method}' is {(kind == HubMethodKind.Stream ? "a stream" : "not a stream")}";
            await this.SendError(link, message.MessageId, "HubMethodNotFound", error).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        link.Track(message.MessageId, cts);
        HubInvocationResult? result = null;
        try
        {
            if (kind == HubMethodKind.Invoke)
            {
                result = await this.endpoint.Invoke(connectionId, method, message.Payload, cts.Token).ConfigureAwait(false);
                await this.Send(link, FrameKind.Completion, message.MessageId, null, result.Result ?? ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await foreach (var item in this.endpoint.Stream(connectionId, method, message.Payload, cts.Token).WithCancellation(cts.Token).ConfigureAwait(false))
                    await this.Send(link, FrameKind.StreamItem, message.MessageId, null, item, cts.Token).ConfigureAwait(false);

                await this.Send(link, FrameKind.StreamEnd, message.MessageId, null, ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // the client cancelled, left, or the host is stopping - nobody is waiting for a reply
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "{Hub}.{Method} failed", this.endpoint.Name, method);
            await this.SendError(link, message.MessageId, HubRemoteException.TypeOf(ex), ex.Message).ConfigureAwait(false);
        }
        finally
        {
            link.Untrack(message.MessageId);
        }

        // deferred so the caller still gets this method's reply
        if (result is { AbortRequested: true })
            await this.endpoint.Disconnect(connectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, result.AbortReason)).ConfigureAwait(false);
    }


    async Task SendError(FrameHostLink link, ushort messageId, string type, string error)
    {
        try
        {
            var payload = ProtocolSerializer.Serialize(new RemoteError(type, error));
            await this.Send(link, FrameKind.Error, messageId, null, payload, this.lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Failed to send error to {Peer}", link.LinkId);
        }
    }


    internal async Task Send(FrameHostLink link, FrameKind kind, ushort messageId, string? name, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // whole messages go out under the lock so frames for one link never interleave
        await link.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.SendLocked(link, kind, messageId, name, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            link.SendLock.Release();
        }
    }


    // under the link's send lock
    async Task SendLocked(FrameHostLink link, FrameKind kind, ushort messageId, string? name, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var notify = this.Notify ?? throw new HubException("Hub host is not running");
        var frames = FrameCodec.Encode(kind, messageId, name, payload.Span, FrameCodec.GetFrameSize(link.Mtu));
        foreach (var frame in frames)
            await notify(link, frame, cancellationToken).ConfigureAwait(false);
    }


    MessageReassembler CreateReassembler() => new(this.options.MaxPayloadSize, this.options.ReassemblyTimeout, this.options.MaxPartialMessages);

    static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}


/// <summary>
/// One link (a BLE central) sending frames to a hub
/// </summary>
internal sealed class FrameHostLink
{
    readonly ConcurrentDictionary<ushort, CancellationTokenSource> inFlight = new();
    int nextHostId;


    public FrameHostLink(FrameHostSession session, string linkId, MessageReassembler reassembler)
    {
        this.LinkId = linkId;
        this.Reassembler = reassembler;
        this.Channel = new LinkChannel(session, this);
    }


    public string LinkId { get; }

    /// <summary>
    /// The hub connection on this link, once its handshake (or resume) was accepted
    /// </summary>
    public string? ConnectionId { get; set; }

    /// <summary>
    /// For a test that addresses its clients by connection id - the link until the client resumes elsewhere
    /// </summary>
    public string Id => this.ConnectionId ?? this.LinkId;

    public object? Native { get; set; }
    public int Mtu { get; set; } = 23;
    public MessageReassembler Reassembler { get; }
    public SemaphoreSlim SendLock { get; } = new(1, 1);
    public string? ResumeToken { get; set; }

    /// <summary>
    /// Issued in the handshake ack when the host serves BLE media channels - the client's first frame on that channel
    /// </summary>
    public string? MediaToken { get; set; }

    /// <summary>
    /// How the hub reaches the client on this link
    /// </summary>
    public IHubPeerChannel Channel { get; }


    /// <summary>
    /// Host allocated ids have the high bit set so they never collide with client ids
    /// </summary>
    public ushort NextHostMessageId() => (ushort)(0x8000 | (Interlocked.Increment(ref this.nextHostId) & 0x7FFF));

    public void Track(ushort id, CancellationTokenSource cts) => this.inFlight[id] = cts;
    public void Untrack(ushort id) => this.inFlight.TryRemove(id, out _);

    public void Cancel(ushort id)
    {
        if (!this.inFlight.TryRemove(id, out var cts))
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void CancelCalls()
    {
        foreach (var id in this.inFlight.Keys.ToList())
            this.Cancel(id);
    }


    internal sealed class LinkChannel(FrameHostSession session, FrameHostLink link) : IHubPeerChannel
    {
        public FrameHostSession Session => session;
        public IHubTransport? Transport => session.Transport;

        public Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken)
            => session.Send(link, FrameKind.Push, link.NextHostMessageId(), eventName, arguments, cancellationToken);

        public async Task Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken)
        {
            try
            {
                await session.Send(link, FrameKind.Disconnect, link.NextHostMessageId(), null, ProtocolSerializer.Serialize(DisconnectInfo.From(disconnect)), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // the hub has let the client go - a handshake on this link starts afresh
                session.Forget(link);
            }
        }

        public Task HostRenamed(string? hostName, CancellationToken cancellationToken)
            => session.Send(link, FrameKind.HostRenamed, link.NextHostMessageId(), null, ProtocolSerializer.Serialize(new RenameInfo(hostName)), cancellationToken);
    }
}
