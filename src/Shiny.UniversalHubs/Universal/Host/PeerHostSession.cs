using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// What a peer-link session needs from the add-on transport that accepted it (Shiny.UniversalHubs.Multipeer,
/// Shiny.UniversalHubs.Nearby). Everything past advertising is the same for every peer-to-peer radio.
/// </summary>
internal interface IPeerHostTransport
{
    /// <summary>
    /// The transport hubs see their clients on (HubConnection.Transport)
    /// </summary>
    IHubTransport HubTransport { get; }

    /// <summary>
    /// Prefixed to the session id to make the connection id: <c>mc:</c>, <c>nb:</c>
    /// </summary>
    string ConnectionPrefix { get; }

    UniversalHubHost Host { get; }
    UniversalServerOptions ServerOptions { get; }
    ILogger? Logger { get; }

    /// <summary>
    /// The running hub a handshake asks for, by characteristic UUID
    /// </summary>
    IHubBinding? FindRunning(string? key);

    /// <summary>
    /// What the transport tells hubs about a session's peer - HubConnection.Get&lt;T&gt;() finds it by type (MultipeerPeer,
    /// NearbyPeer, the HTTP request)
    /// </summary>
    object CreatePeer(IPeerConnection connection);

    void Remove(PeerHostSession session);

    /// <summary>
    /// Every session the transport has open
    /// </summary>
    IEnumerable<PeerHostSession> Sessions { get; }
}


/// <summary>
/// One client's peer-link session on the host (Multipeer, Nearby): runs its calls through the hub and carries the hub's pushes back
/// </summary>
internal sealed class PeerHostSession(IPeerHostTransport transport, IPeerConnection connection) : IPeerConnectionHandler, IHubPeerChannel
{
    readonly object sendLock = new();
    readonly ConcurrentDictionary<uint, CancellationTokenSource> calls = new();
    readonly ConcurrentDictionary<uint, PendingUpload> uploads = new();
    readonly CancellationTokenSource lifetime = new();
    PeerMediaChannel? media;
    HubDisconnect? ending;
    int handshaking;
    int closed;


    /// <summary>
    /// The hub connection this session carries - a fresh id, or the held connection's when the session resumed one
    /// </summary>
    public string ConnectionId { get; private set; } = transport.ConnectionPrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The hub this session hand-shook with, null before then
    /// </summary>
    public IHubBinding? Binding { get; private set; }

    ILogger? Logger => transport.Logger;


    // ---- from the client ----

    public void Received(ReadOnlyMemory<byte> data)
    {
        PeerMessage message;
        try
        {
            message = PeerProtocol.Decode(data);
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "Dropping {Peer}: unreadable message", connection.RemoteName);
            this.Close();
            return;
        }

        switch (message.Kind)
        {
            case PeerMessageKind.Handshake:
                _ = this.Handshake(message);
                break;

            case PeerMessageKind.Resume:
                _ = this.Resume(message);
                break;

            case PeerMessageKind.Invoke:
                _ = this.Invoke(message);
                break;

            case PeerMessageKind.Stream:
                _ = this.Stream(message);
                break;

            case PeerMessageKind.Cancel:
                if (this.calls.TryRemove(message.Id, out var cts))
                    cts.Cancel();
                break;

            case PeerMessageKind.Rename:
                _ = this.Rename(message);
                break;

            case PeerMessageKind.Disconnect:
                var leaving = message.Read(PeerJsonContext.Default.HubHangUp);
                this.ending ??= new HubDisconnect(HubDisconnectReason.ClientDisconnect, leaving.Message);
                this.Close();
                break;

            case PeerMessageKind.FileRequest:
                _ = this.FileRequest(message);
                break;

            case PeerMessageKind.MediaOpen:
                this.OpenMedia(message);
                break;

            case PeerMessageKind.MediaPacket:
                Volatile.Read(ref this.media)?.Deliver(message.Payload);
                break;

            case PeerMessageKind.MediaClose:
                Interlocked.Exchange(ref this.media, null)?.Closed();
                break;

            default:
                this.Logger?.LogDebug("Ignoring {Kind} from {Peer}", message.Kind, connection.RemoteName);
                break;
        }
    }


    public void FileProgress(string name, double fraction)
    {
        // the client reports its own upload progress
    }


    public void FileReceived(string name, string? path, Exception? error)
    {
        var id = PeerProtocol.ReadFileResourceId(name);
        if (id == null || !this.uploads.TryRemove(id.Value, out var upload))
        {
            DeleteQuietly(path);
            return;
        }
        _ = Task.Run(() => this.Store(id.Value, upload, path, error));
    }


    public void Closed()
    {
        if (Interlocked.Exchange(ref this.closed, 1) == 1)
            return;

        this.lifetime.Cancel();
        foreach (var call in this.calls.Values)
            call.Cancel();
        Interlocked.Exchange(ref this.media, null)?.Closed();
        this.uploads.Clear();

        // the hub forgets clients it disconnected itself - this only matters when the client left or the link dropped.
        // A connected client's peer is forgotten once the hub has run OnDisconnectedAsync (HubBinding).
        // a session that drops without a goodbye leaves a resumable client held; one that was resumed elsewhere is ignored
        if (this.Binding is { } hub)
            hub.Endpoint.Disconnected(this.ConnectionId, this, this.ending ?? new HubDisconnect(HubDisconnectReason.ClientTimeout));
        transport.Remove(this);
        this.Logger?.LogDebug("{Transport} session {Id} closed", transport.HubTransport.DisplayName, this.ConnectionId);
    }


    public void Close() => connection.Dispose();


    // ---- to the client (IHubPeerChannel) ----

    public IHubTransport Transport => transport.HubTransport;

    internal IPeerHostTransport PeerTransport => transport;

    public object? GetFeature(Type type) => this.peer is { } p && type.IsInstanceOfType(p) ? p : null;
    object? peer;

    public Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken)
    {
        this.Send(PeerMessageKind.Push, 0, eventName, arguments);
        return Task.CompletedTask;
    }


    public Task Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken)
    {
        this.ending ??= disconnect;
        try
        {
            this.Send(PeerProtocol.Encode(
                PeerMessageKind.Disconnect, 0, null,
                new HubHangUp((int)disconnect.Reason, disconnect.Message),
                PeerJsonContext.Default.HubHangUp
            ));
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Could not tell {Peer} why it was disconnected", connection.RemoteName);
        }

        // the client hangs up when it reads that - this is for one that doesn't
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => this.Close(), TaskScheduler.Default);
        return Task.CompletedTask;
    }


    public Task HostRenamed(string? hostName, CancellationToken cancellationToken)
    {
        this.Send(PeerProtocol.Encode(PeerMessageKind.HostRenamed, 0, null, new PeerRename(hostName), PeerJsonContext.Default.PeerRename));
        return Task.CompletedTask;
    }


    // ---- calls ----

    async Task Handshake(PeerMessage message)
    {
        if (Interlocked.Exchange(ref this.handshaking, 1) == 1)
        {
            this.SendError(message.Id, nameof(HubProtocolException), "Already connected");
            return;
        }

        string? rejection;
        HubHandshake? handshake = null;
        try
        {
            handshake = message.Read(PeerJsonContext.Default.HubHandshake);
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Malformed handshake from {Peer}", connection.RemoteName);
        }

        var hub = transport.FindRunning(message.Name);
        if (handshake == null)
            rejection = "Malformed handshake";
        else if (hub == null)
            rejection = "Hub is not running";
        else if (handshake.Protocol != PeerProtocol.Version)
            rejection = $"Unsupported protocol version {handshake.Protocol}";
        else
        {
            this.peer = transport.CreatePeer(connection);
            this.Binding = hub;
            try
            {
                rejection = await hub.Endpoint.Connect(
                    this.ConnectionId,
                    new HandshakeInfo(FrameCodec.ProtocolVersion, handshake.Name, handshake.AppVersion, handshake.Properties, Serializer: handshake.Serializer),
                    this,
                    this.lifetime.Token
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Logger?.LogWarning(ex, "Handshake from {Peer} failed", connection.RemoteName);
                rejection = ex.Message;
            }
            if (rejection != null)
            {
                this.Binding = null;
            }
        }

        // only a client that can come back is held
        var token = rejection == null && handshake!.Resumable ? hub!.Endpoint.EnableResume(this.ConnectionId) : null;
        this.Reply(message.Id, rejection, token, hub);
        if (rejection != null)
        {
            this.Logger?.LogInformation("Refused {Peer}: {Reason}", connection.RemoteName, rejection);
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => this.Close(), TaskScheduler.Default);
        }
    }


    /// <summary>
    /// A client whose session dropped, back on this new one with its resume token. Accepted, this session takes over the
    /// held connection - its id, groups and Items - the reply goes out, then the pushes it missed.
    /// </summary>
    async Task Resume(PeerMessage message)
    {
        if (Interlocked.Exchange(ref this.handshaking, 1) == 1)
        {
            this.SendError(message.Id, nameof(HubProtocolException), "Already connected");
            return;
        }

        PeerResume? resume = null;
        try
        {
            resume = message.Read(PeerJsonContext.Default.PeerResume);
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Malformed resume from {Peer}", connection.RemoteName);
        }

        var hub = transport.FindRunning(message.Name);
        string? rejection;
        if (resume == null)
            rejection = "Malformed resume";
        else if (hub == null)
            rejection = "Hub is not running";
        else
        {
            try
            {
                rejection = await hub.Endpoint.Resume(resume.Token, transport.ConnectionPrefix, this, connectionId =>
                {
                    // under the hub's send lock: this session is the connection now, and its reply goes ahead of the replay
                    this.ConnectionId = connectionId;
                    this.Binding = hub;
                    this.peer = transport.CreatePeer(connection);
                    this.Reply(message.Id, null, resume.Token, hub);
                    return Task.CompletedTask;
                }, this.lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Logger?.LogWarning(ex, "Resume from {Peer} failed", connection.RemoteName);
                rejection = ex.Message;
            }
        }

        if (rejection == null)
        {
            // the session the client dropped from, if the host hasn't seen it end yet - it no longer speaks for anyone
            foreach (var old in transport.Sessions.Where(x => !ReferenceEquals(x, this) && x.ConnectionId == this.ConnectionId).ToList())
                old.Close();

            this.Logger?.LogInformation("{Peer} resumed {Connection}", connection.RemoteName, this.ConnectionId);
            return;
        }

        this.Logger?.LogInformation("Could not resume {Peer}: {Reason}", connection.RemoteName, rejection);
        this.Reply(message.Id, rejection, null, hub);
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => this.Close(), TaskScheduler.Default);
    }


    void Reply(uint id, string? rejection, string? resumeToken, IHubBinding? hub)
    {
        var host = transport.Host;
        var window = resumeToken != null && hub != null ? (int)Math.Ceiling(hub.Endpoint.ResumeWindow.TotalSeconds) : 0;
        try
        {
            this.Send(PeerProtocol.Encode(
                PeerMessageKind.HandshakeReply,
                id,
                null,
                new PeerHandshakeReply(
                    rejection == null,
                    rejection,
                    host.HostName,
                    host.HostId,
                    rejection == null && host.FilesEnabled,
                    rejection == null ? host.MediaMaxPacketSize : null,
                    window > 0 ? resumeToken : null,
                    window > 0 ? window : null
                ),
                PeerJsonContext.Default.PeerHandshakeReply
            ));
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Could not answer {Peer}'s handshake", connection.RemoteName);
        }
    }


    async Task Invoke(PeerMessage message)
    {
        if (this.Check(message, HubMethodKind.Invoke) is not { } endpoint)
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        this.calls[message.Id] = cts;
        HubInvocationResult? result = null;
        try
        {
            result = await endpoint.Invoke(this.ConnectionId, message.Name!, message.Payload, cts.Token).ConfigureAwait(false);
            this.Send(PeerMessageKind.Completion, message.Id, null, result.Result ?? []);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // the caller gave up or is gone - nobody is waiting for a reply
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "{Hub} call failed", this.Binding?.Name);
            this.SendError(message.Id, HubRemoteException.TypeOf(ex), ex.Message);
        }
        finally
        {
            this.calls.TryRemove(message.Id, out _);
        }

        // Context.Abort() takes effect once the reply is out, as it does over BLE and HTTP
        if (result?.AbortRequested == true)
            await endpoint.Disconnect(this.ConnectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, result.AbortReason)).ConfigureAwait(false);
    }


    async Task Stream(PeerMessage message)
    {
        if (this.Check(message, HubMethodKind.Stream) is not { } endpoint)
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        this.calls[message.Id] = cts;
        try
        {
            await foreach (var item in endpoint.Stream(this.ConnectionId, message.Name!, message.Payload, cts.Token).ConfigureAwait(false))
                this.Send(PeerMessageKind.StreamItem, message.Id, null, item);

            this.Send(PeerMessageKind.StreamEnd, message.Id, null, []);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "{Hub} stream failed", this.Binding?.Name);
            this.SendError(message.Id, HubRemoteException.TypeOf(ex), ex.Message);
        }
        finally
        {
            this.calls.TryRemove(message.Id, out _);
        }
    }


    async Task Rename(PeerMessage message)
    {
        if (this.Binding is not { } hub)
        {
            this.SendError(message.Id, nameof(HubProtocolException), "Handshake required");
            return;
        }
        try
        {
            var rename = message.Read(PeerJsonContext.Default.PeerRename);
            var refusal = await hub.Endpoint.Rename(this.ConnectionId, rename.Name, this.lifetime.Token).ConfigureAwait(false);
            if (refusal == null)
                this.Send(PeerMessageKind.Completion, message.Id, null, []);
            else
                this.SendError(message.Id, HubRemoteException.RenameRefused, refusal);
        }
        catch (Exception ex) when (!this.lifetime.IsCancellationRequested)
        {
            this.SendError(message.Id, HubRemoteException.TypeOf(ex), ex.Message);
        }
    }


    /// <summary>
    /// The hub's endpoint when the call may go ahead; otherwise answers with the error and returns null
    /// </summary>
    IHubEndpoint? Check(PeerMessage message, HubMethodKind expected)
    {
        if (this.Binding is not { } hub)
        {
            this.SendError(message.Id, nameof(HubProtocolException), "Handshake required");
            return null;
        }

        var method = message.Name ?? String.Empty;
        var kind = hub.Endpoint.GetMethodKind(method);
        if (kind == expected)
            return hub.Endpoint;

        // the same wording and error type as BLE and HTTP
        this.SendError(message.Id, "HubMethodNotFound", kind == HubMethodKind.NotFound
            ? $"{hub.Name} has no method '{method}'"
            : $"'{method}' is {(kind == HubMethodKind.Stream ? "a stream" : "not a stream")}");
        return null;
    }


    // ---- files ----

    async Task FileRequest(PeerMessage message)
    {
        PeerFileRequest request;
        try
        {
            request = message.Read(PeerJsonContext.Default.PeerFileRequest);
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Malformed file request from {Peer}", connection.RemoteName);
            this.ReplyFile(message.Id, 400, "Malformed file request", null);
            return;
        }

        var ft = transport.ServerOptions.FileTransfers;
        var client = this.Binding is { } hub ? hub.Endpoint.FindClient(this.ConnectionId) : null;
        if (client == null)
        {
            this.ReplyFile(message.Id, 401, "Handshake required", null);
            return;
        }
        if (ft == null || !transport.Host.FilesEnabled)
        {
            this.ReplyFile(message.Id, 404, "This host does not serve files", null);
            return;
        }

        var path = HubFilePaths.Resolve(ft, request.Name);
        var refusal = path == null ? (400, "Invalid file name") : request.Upload ? this.CheckUpload(ft, request, path, client) : this.CheckDownload(ft, request, path, client);
        if (refusal is { } r)
        {
            this.ReplyFile(message.Id, r.Status, r.Message, null);
            return;
        }

        if (request.Upload)
        {
            // the resource follows the reply
            this.uploads[message.Id] = new PendingUpload(request.Name, path!, request.Size!.Value, client, ft, Stopwatch.StartNew());
            this.ReplyFile(message.Id, 200, null, request.Size);
            return;
        }

        var sw = Stopwatch.StartNew();
        var length = new FileInfo(path!).Length;
        this.ReplyFile(message.Id, 200, null, length);
        try
        {
            await connection.SendFile(path!, PeerProtocol.FileResourceName(message.Id), null, this.lifetime.Token).ConfigureAwait(false);
            this.Raise(client, HubTransferDirection.Download, request.Name, path!, length, sw.Elapsed);
        }
        catch (Exception ex)
        {
            this.Logger?.LogInformation(ex, "Sending {File} to {Peer} failed", request.Name, connection.RemoteName);
        }
    }


    (int Status, string Message)? CheckUpload(HubFileTransferOptions ft, PeerFileRequest request, string path, HubConnectedClient client)
    {
        if (!ft.AllowUploads)
            return (403, "Uploads are not allowed");
        if (request.Size == null)
            return (411, "A size is required");
        if (ft.MaxUploadSize is { } max && request.Size > max)
            return (413, $"Uploads are limited to {max} bytes");
        if (!ft.OverwriteExistingUploads && File.Exists(path))
            return (409, "The file already exists");
        if (transport.ServerOptions.AuthorizeFile?.Invoke(new UniversalHubFileRequest(HubTransferDirection.Upload, request.Name, request.Size, client)) == false)
            return (403, "The host refused the transfer");

        return null;
    }


    (int Status, string Message)? CheckDownload(HubFileTransferOptions ft, PeerFileRequest request, string path, HubConnectedClient client)
    {
        if (!ft.AllowDownloads)
            return (403, "Downloads are not allowed");
        if (!File.Exists(path))
            return (404, "No such file");
        if (transport.ServerOptions.AuthorizeFile?.Invoke(new UniversalHubFileRequest(HubTransferDirection.Download, request.Name, null, client)) == false)
            return (403, "The host refused the transfer");

        return null;
    }


    void Store(uint id, PendingUpload upload, string? received, Exception? error)
    {
        PeerFileDone done;
        try
        {
            if (error != null || received == null)
            {
                done = new PeerFileDone(false, error?.Message ?? "The file did not arrive", 0);
            }
            else
            {
                var bytes = new FileInfo(received).Length;
                if (bytes != upload.Size)
                {
                    done = new PeerFileDone(false, $"Expected {upload.Size} bytes but received {bytes}", bytes);
                }
                else if (!upload.Options.OverwriteExistingUploads && File.Exists(upload.Path))
                {
                    done = new PeerFileDone(false, "The file already exists", bytes);
                }
                else
                {
                    Directory.CreateDirectory(upload.Options.RootDirectory);
                    File.Move(received, upload.Path, true);
                    received = null;
                    done = new PeerFileDone(true, null, bytes);
                    this.Raise(upload.Client, HubTransferDirection.Upload, upload.Name, upload.Path, bytes, upload.Elapsed.Elapsed);
                }
            }
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "Storing {File} from {Peer} failed", upload.Name, connection.RemoteName);
            done = new PeerFileDone(false, ex.Message, 0);
        }
        finally
        {
            DeleteQuietly(received);
        }

        try
        {
            this.Send(PeerProtocol.Encode(PeerMessageKind.FileDone, id, null, done, PeerJsonContext.Default.PeerFileDone));
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Could not tell {Peer} how its upload went", connection.RemoteName);
        }
    }


    void Raise(HubConnectedClient client, HubTransferDirection type, string name, string path, long bytes, TimeSpan elapsed)
    {
        try
        {
            transport.Host.OnFileTransferred(new HubFileTransferredEventArgs(client, client.Id, type, name, path, bytes, elapsed));
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "FileTransferred handler failed");
        }
    }


    void ReplyFile(uint id, int status, string? message, long? length)
    {
        try
        {
            this.Send(PeerProtocol.Encode(PeerMessageKind.FileReply, id, null, new PeerFileReply(status, message, length), PeerJsonContext.Default.PeerFileReply));
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Could not answer {Peer}'s file request", connection.RemoteName);
        }
    }


    // ---- media ----

    void OpenMedia(PeerMessage message)
    {
        if (this.Binding is not { } hub)
        {
            this.SendError(message.Id, nameof(HubProtocolException), "Handshake required");
            return;
        }
        if (transport.Host.MediaMaxPacketSize is not { } max)
        {
            this.SendError(message.Id, nameof(HubException), "This host has no media channels");
            return;
        }

        var channel = new PeerMediaChannel((kind, packet) => this.Send(kind, 0, null, packet.Span), this.ConnectionId, max);
        Interlocked.Exchange(ref this.media, channel)?.Closed();
        if (!hub.Endpoint.MediaChannelOpened(this.ConnectionId, channel))
        {
            Interlocked.CompareExchange(ref this.media, null, channel);
            channel.Closed();
            this.SendError(message.Id, nameof(HubException), "Not connected");
            return;
        }
        this.Send(PeerMessageKind.MediaOpened, message.Id, null, []);
    }


    // ---- sending ----

    void Send(PeerMessageKind kind, uint id, string? name, ReadOnlySpan<byte> payload)
        => this.Send(PeerProtocol.Encode(kind, id, name, payload));


    void Send(byte[] message)
    {
        // one message at a time, so a session's messages leave in the order they were made
        lock (this.sendLock)
            connection.Send(message);
    }


    void SendError(uint id, string type, string message)
    {
        try
        {
            this.Send(PeerProtocol.Encode(PeerMessageKind.Error, id, null, new PeerError(type, message), PeerJsonContext.Default.PeerError));
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Could not send an error to {Peer}", connection.RemoteName);
        }
    }


    static void DeleteQuietly(string? path)
    {
        if (path == null)
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
            // a temp file - the OS cleans up
        }
    }


    sealed record PendingUpload(string Name, string Path, long Size, HubConnectedClient Client, HubFileTransferOptions Options, Stopwatch Elapsed);
}
