using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// Serves universal hubs over MultipeerConnectivity. The host advertises once under the Multipeer service type, listing its
/// running hubs, and accepts every invitation into a session of its own - one session per hub connection, so the 8-peer
/// limit of an MCSession never applies and clients never mesh with each other. Each session reaches its hub through the
/// blehubs transport seam, so it shares Clients, Groups, MaxClients and ValidateClient with BLE and HTTP clients.
/// </summary>
internal sealed class MultipeerHostTransport(
    IServiceProvider services,
    IMultipeerLink link,
    MultipeerHostOptions options,
    BleHubHostOptions bleOptions,
    UniversalHubHostOptions httpOptions,
    ILogger<MultipeerHostTransport>? logger = null
) : IUniversalHubTransport
{
    readonly object gate = new();
    readonly List<IHubBinding> running = new();
    readonly ConcurrentDictionary<string, MultipeerHostSession> sessions = new(StringComparer.Ordinal);
    IDisposable? advertisement;


    public HubTransports Transport => HubTransports.Multipeer;

    internal UniversalHubHost Host => services.GetRequiredService<UniversalHubHost>();
    internal BleHubHostOptions BleOptions => bleOptions;
    internal UniversalHubHostOptions HttpOptions => httpOptions;
    internal ILogger? Logger => logger;

    /// <summary>
    /// The Multipeer display name hosts advertise as: the configured one, else the BLE local name
    /// </summary>
    internal string DisplayName => MultipeerNames.Truncate(options.DisplayName ?? bleOptions.LocalName ?? "Hub host");


    public bool IsRunning(IHubBinding hub)
    {
        lock (this.gate)
            return this.running.Contains(hub);
    }


    public Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        if (!link.IsSupported)
            throw new PlatformNotSupportedException("MultipeerConnectivity is only available on iOS, Mac Catalyst and macOS");

        lock (this.gate)
        {
            var added = hubs.Where(x => !this.running.Contains(x)).ToList();
            this.running.AddRange(added);
            try
            {
                this.Readvertise();
            }
            catch
            {
                foreach (var hub in added)
                    this.running.Remove(hub);
                throw;
            }
        }
        logger?.LogInformation("Multipeer hosting {Hubs} as {Name} ({ServiceType})", String.Join(", ", hubs.Select(x => x.HubType.Name)), this.DisplayName, options.ServiceType);
        return Task.CompletedTask;
    }


    public async Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect)
    {
        // refuse new handshakes first, then tell everyone connected why they're going
        lock (this.gate)
        {
            foreach (var hub in hubs)
                this.running.Remove(hub);

            this.Readvertise();
        }

        var stopping = this.sessions.Values.Where(x => x.Binding != null && hubs.Contains(x.Binding)).ToList();
        await Task.WhenAll(stopping.Select(x => x.Binding!.Endpoint.Disconnect(x.ConnectionId, disconnect))).ConfigureAwait(false);

        bool any;
        lock (this.gate)
            any = this.running.Count > 0;

        // nothing left to join - sessions still waiting to hand-shake have nowhere to go
        if (!any)
        {
            foreach (var session in this.sessions.Values.Where(x => x.Binding == null))
                session.Close();
        }
    }


    /// <summary>
    /// The running hub a handshake asks for, by characteristic UUID
    /// </summary>
    internal IHubBinding? FindRunning(string? characteristicUuid)
    {
        lock (this.gate)
            return this.running.FirstOrDefault(x => x.CharacteristicUuid.Equals(characteristicUuid, StringComparison.OrdinalIgnoreCase));
    }


    internal void Remove(MultipeerHostSession session) => this.sessions.TryRemove(session.ConnectionId, out _);


    // under the gate
    void Readvertise()
    {
        this.advertisement?.Dispose();
        this.advertisement = null;

        if (this.running.Count == 0)
            return;

        var hubs = String.Join(",", this.running.Select(x => x.CharacteristicUuid.ToLowerInvariant()));
        var info = new Dictionary<string, string>
        {
            [MultipeerProtocol.InfoProtocol] = MultipeerProtocol.Version.ToString(),
            [MultipeerProtocol.InfoHostId] = httpOptions.HostId,
            [MultipeerProtocol.InfoName] = bleOptions.LocalName ?? this.DisplayName,
            [MultipeerProtocol.InfoHubs] = hubs
        };
        this.advertisement = link.Advertise(options.ServiceType, this.DisplayName, info, this.Accepted);
    }


    void Accepted(IMultipeerConnection connection)
    {
        var session = new MultipeerHostSession(this, connection);
        this.sessions[session.ConnectionId] = session;
        logger?.LogDebug("Multipeer session {Id} with {Peer}", session.ConnectionId, connection.RemoteName);
        connection.Start(session);
    }
}


/// <summary>
/// One client's Multipeer session on the host: runs its calls through the hub and carries the hub's pushes back
/// </summary>
internal sealed class MultipeerHostSession(MultipeerHostTransport transport, IMultipeerConnection connection) : IMultipeerConnectionHandler, IBleHubPeerChannel
{
    readonly object sendLock = new();
    readonly ConcurrentDictionary<uint, CancellationTokenSource> calls = new();
    readonly ConcurrentDictionary<uint, PendingUpload> uploads = new();
    readonly CancellationTokenSource lifetime = new();
    MultipeerMediaChannel? media;
    HubDisconnect? ending;
    int handshaking;
    int closed;


    public string ConnectionId { get; } = HubConnection.MultipeerPrefix + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The hub this session hand-shook with, null before then
    /// </summary>
    public IHubBinding? Binding { get; private set; }

    ILogger? Logger => transport.Logger;


    // ---- from the client ----

    public void Received(ReadOnlyMemory<byte> data)
    {
        MultipeerMessage message;
        try
        {
            message = MultipeerProtocol.Decode(data);
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "Dropping {Peer}: unreadable message", connection.RemoteName);
            this.Close();
            return;
        }

        switch (message.Kind)
        {
            case MultipeerMessageKind.Handshake:
                _ = this.Handshake(message);
                break;

            case MultipeerMessageKind.Invoke:
                _ = this.Invoke(message);
                break;

            case MultipeerMessageKind.Stream:
                _ = this.Stream(message);
                break;

            case MultipeerMessageKind.Cancel:
                if (this.calls.TryRemove(message.Id, out var cts))
                    cts.Cancel();
                break;

            case MultipeerMessageKind.Rename:
                _ = this.Rename(message);
                break;

            case MultipeerMessageKind.Disconnect:
                var leaving = message.Read(MultipeerJsonContext.Default.HubHangUp);
                this.ending ??= new HubDisconnect(HubDisconnectReason.ClientDisconnect, leaving.Message);
                this.Close();
                break;

            case MultipeerMessageKind.FileRequest:
                _ = this.FileRequest(message);
                break;

            case MultipeerMessageKind.MediaOpen:
                this.OpenMedia(message);
                break;

            case MultipeerMessageKind.MediaPacket:
                Volatile.Read(ref this.media)?.Deliver(message.Payload);
                break;

            case MultipeerMessageKind.MediaClose:
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
        var id = MultipeerProtocol.ReadFileResourceId(name);
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
        if (this.Binding is { } hub)
            hub.Endpoint.Disconnected(this.ConnectionId, this.ending ?? new HubDisconnect(HubDisconnectReason.ClientTimeout));
        else
            MultipeerPeers.Remove(this.ConnectionId);
        transport.Remove(this);
        this.Logger?.LogDebug("Multipeer session {Id} closed", this.ConnectionId);
    }


    public void Close() => connection.Dispose();


    // ---- to the client (IBleHubPeerChannel) ----

    public Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken)
    {
        this.Send(MultipeerMessageKind.Push, 0, eventName, arguments);
        return Task.CompletedTask;
    }


    public Task Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken)
    {
        this.ending ??= disconnect;
        try
        {
            this.Send(MultipeerProtocol.Encode(
                MultipeerMessageKind.Disconnect, 0, null,
                new HubHangUp((int)disconnect.Reason, disconnect.Message),
                MultipeerJsonContext.Default.HubHangUp
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
        this.Send(MultipeerProtocol.Encode(MultipeerMessageKind.HostRenamed, 0, null, new MultipeerRename(hostName), MultipeerJsonContext.Default.MultipeerRename));
        return Task.CompletedTask;
    }


    // ---- calls ----

    async Task Handshake(MultipeerMessage message)
    {
        if (Interlocked.Exchange(ref this.handshaking, 1) == 1)
        {
            this.SendError(message.Id, nameof(BleHubProtocolException), "Already connected");
            return;
        }

        string? rejection;
        HubHandshake? handshake = null;
        try
        {
            handshake = message.Read(MultipeerJsonContext.Default.HubHandshake);
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
        else if (handshake.Protocol != MultipeerProtocol.Version)
            rejection = $"Unsupported protocol version {handshake.Protocol}";
        else
        {
            MultipeerPeers.Add(this.ConnectionId, new MultipeerPeer(connection.RemoteName));
            this.Binding = hub;
            try
            {
                rejection = await hub.Endpoint.Connect(
                    this.ConnectionId,
                    new HandshakeInfo(FrameCodec.ProtocolVersion, handshake.Name, handshake.AppVersion, handshake.Properties),
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
                MultipeerPeers.Remove(this.ConnectionId);
            }
        }

        var host = transport.Host;
        try
        {
            this.Send(MultipeerProtocol.Encode(
                MultipeerMessageKind.HandshakeReply,
                message.Id,
                null,
                new MultipeerHandshakeReply(
                    rejection == null,
                    rejection,
                    host.HostName,
                    host.HostId,
                    rejection == null && host.FilesEnabled,
                    rejection == null ? host.MediaMaxPacketSize : null
                ),
                MultipeerJsonContext.Default.MultipeerHandshakeReply
            ));
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Could not answer {Peer}'s handshake", connection.RemoteName);
        }

        if (rejection != null)
        {
            this.Logger?.LogInformation("Refused {Peer}: {Reason}", connection.RemoteName, rejection);
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => this.Close(), TaskScheduler.Default);
        }
    }


    async Task Invoke(MultipeerMessage message)
    {
        if (this.Check(message, BleHubMethodKind.Invoke) is not { } endpoint)
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        this.calls[message.Id] = cts;
        BleHubInvocationResult? result = null;
        try
        {
            result = await endpoint.Invoke(this.ConnectionId, message.Name!, message.Payload, cts.Token).ConfigureAwait(false);
            this.Send(MultipeerMessageKind.Completion, message.Id, null, result.Result ?? []);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // the caller gave up or is gone - nobody is waiting for a reply
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "{Hub} call failed", this.Binding?.HubType.Name);
            this.SendError(message.Id, ex.GetType().Name, ex.Message);
        }
        finally
        {
            this.calls.TryRemove(message.Id, out _);
        }

        // Context.Abort() takes effect once the reply is out, as it does over BLE and HTTP
        if (result?.AbortRequested == true)
            await endpoint.Disconnect(this.ConnectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, result.AbortReason)).ConfigureAwait(false);
    }


    async Task Stream(MultipeerMessage message)
    {
        if (this.Check(message, BleHubMethodKind.Stream) is not { } endpoint)
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        this.calls[message.Id] = cts;
        try
        {
            await foreach (var item in endpoint.Stream(this.ConnectionId, message.Name!, message.Payload, cts.Token).ConfigureAwait(false))
                this.Send(MultipeerMessageKind.StreamItem, message.Id, null, item);

            this.Send(MultipeerMessageKind.StreamEnd, message.Id, null, []);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "{Hub} stream failed", this.Binding?.HubType.Name);
            this.SendError(message.Id, ex.GetType().Name, ex.Message);
        }
        finally
        {
            this.calls.TryRemove(message.Id, out _);
        }
    }


    async Task Rename(MultipeerMessage message)
    {
        if (this.Binding is not { } hub)
        {
            this.SendError(message.Id, nameof(BleHubProtocolException), "Handshake required");
            return;
        }
        try
        {
            var rename = message.Read(MultipeerJsonContext.Default.MultipeerRename);
            var refusal = await hub.Endpoint.Rename(this.ConnectionId, rename.Name, this.lifetime.Token).ConfigureAwait(false);
            if (refusal == null)
                this.Send(MultipeerMessageKind.Completion, message.Id, null, []);
            else
                this.SendError(message.Id, BleHubRemoteException.RenameRefused, refusal);
        }
        catch (Exception ex) when (!this.lifetime.IsCancellationRequested)
        {
            this.SendError(message.Id, ex.GetType().Name, ex.Message);
        }
    }


    /// <summary>
    /// The hub's endpoint when the call may go ahead; otherwise answers with the error and returns null
    /// </summary>
    IBleHubTransportEndpoint? Check(MultipeerMessage message, BleHubMethodKind expected)
    {
        if (this.Binding is not { } hub)
        {
            this.SendError(message.Id, nameof(BleHubProtocolException), "Handshake required");
            return null;
        }

        var method = message.Name ?? String.Empty;
        var kind = hub.Endpoint.GetMethodKind(method);
        if (kind == expected)
            return hub.Endpoint;

        // the same wording and error type as BLE and HTTP
        this.SendError(message.Id, "HubMethodNotFound", kind == BleHubMethodKind.NotFound
            ? $"{hub.HubType.Name} has no method '{method}'"
            : $"'{method}' is {(kind == BleHubMethodKind.Stream ? "a stream" : "not a stream")}");
        return null;
    }


    // ---- files ----

    async Task FileRequest(MultipeerMessage message)
    {
        MultipeerFileRequest request;
        try
        {
            request = message.Read(MultipeerJsonContext.Default.MultipeerFileRequest);
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Malformed file request from {Peer}", connection.RemoteName);
            this.ReplyFile(message.Id, 400, "Malformed file request", null);
            return;
        }

        var ft = transport.BleOptions.FileTransfers;
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

        var path = FileRoutes.ResolvePath(ft, request.Name);
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
            await connection.SendFile(path!, MultipeerProtocol.FileResourceName(message.Id), null, this.lifetime.Token).ConfigureAwait(false);
            this.Raise(client, L2CapTransferType.Download, request.Name, path!, length, sw.Elapsed);
        }
        catch (Exception ex)
        {
            this.Logger?.LogInformation(ex, "Sending {File} to {Peer} failed", request.Name, connection.RemoteName);
        }
    }


    (int Status, string Message)? CheckUpload(BleHubFileTransferOptions ft, MultipeerFileRequest request, string path, BleHubConnectedClient client)
    {
        if (!ft.AllowUploads)
            return (403, "Uploads are not allowed");
        if (request.Size == null)
            return (411, "A size is required");
        if (ft.MaxUploadSize is { } max && request.Size > max)
            return (413, $"Uploads are limited to {max} bytes");
        if (!ft.OverwriteExistingUploads && File.Exists(path))
            return (409, "The file already exists");
        if (transport.HttpOptions.AuthorizeFile?.Invoke(new UniversalHubFileRequest(L2CapTransferType.Upload, request.Name, request.Size, client)) == false)
            return (403, "The host refused the transfer");

        return null;
    }


    (int Status, string Message)? CheckDownload(BleHubFileTransferOptions ft, MultipeerFileRequest request, string path, BleHubConnectedClient client)
    {
        if (!ft.AllowDownloads)
            return (403, "Downloads are not allowed");
        if (!File.Exists(path))
            return (404, "No such file");
        if (transport.HttpOptions.AuthorizeFile?.Invoke(new UniversalHubFileRequest(L2CapTransferType.Download, request.Name, null, client)) == false)
            return (403, "The host refused the transfer");

        return null;
    }


    void Store(uint id, PendingUpload upload, string? received, Exception? error)
    {
        MultipeerFileDone done;
        try
        {
            if (error != null || received == null)
            {
                done = new MultipeerFileDone(false, error?.Message ?? "The file did not arrive", 0);
            }
            else
            {
                var bytes = new FileInfo(received).Length;
                if (bytes != upload.Size)
                {
                    done = new MultipeerFileDone(false, $"Expected {upload.Size} bytes but received {bytes}", bytes);
                }
                else if (!upload.Options.OverwriteExistingUploads && File.Exists(upload.Path))
                {
                    done = new MultipeerFileDone(false, "The file already exists", bytes);
                }
                else
                {
                    Directory.CreateDirectory(upload.Options.RootDirectory);
                    File.Move(received, upload.Path, true);
                    received = null;
                    done = new MultipeerFileDone(true, null, bytes);
                    this.Raise(upload.Client, L2CapTransferType.Upload, upload.Name, upload.Path, bytes, upload.Elapsed.Elapsed);
                }
            }
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "Storing {File} from {Peer} failed", upload.Name, connection.RemoteName);
            done = new MultipeerFileDone(false, ex.Message, 0);
        }
        finally
        {
            DeleteQuietly(received);
        }

        try
        {
            this.Send(MultipeerProtocol.Encode(MultipeerMessageKind.FileDone, id, null, done, MultipeerJsonContext.Default.MultipeerFileDone));
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Could not tell {Peer} how its upload went", connection.RemoteName);
        }
    }


    void Raise(BleHubConnectedClient client, L2CapTransferType type, string name, string path, long bytes, TimeSpan elapsed)
    {
        try
        {
            transport.Host.OnFileTransferred(new BleHubFileTransferredEventArgs(client, client.Id, type, name, path, bytes, elapsed));
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
            this.Send(MultipeerProtocol.Encode(MultipeerMessageKind.FileReply, id, null, new MultipeerFileReply(status, message, length), MultipeerJsonContext.Default.MultipeerFileReply));
        }
        catch (Exception ex)
        {
            this.Logger?.LogDebug(ex, "Could not answer {Peer}'s file request", connection.RemoteName);
        }
    }


    // ---- media ----

    void OpenMedia(MultipeerMessage message)
    {
        if (this.Binding is not { } hub)
        {
            this.SendError(message.Id, nameof(BleHubProtocolException), "Handshake required");
            return;
        }
        if (transport.Host.MediaMaxPacketSize is not { } max)
        {
            this.SendError(message.Id, nameof(BleHubException), "This host has no media channels");
            return;
        }

        var channel = new MultipeerMediaChannel((kind, packet) => this.Send(kind, 0, null, packet.Span), this.ConnectionId, max);
        Interlocked.Exchange(ref this.media, channel)?.Closed();
        if (!hub.Endpoint.MediaChannelOpened(this.ConnectionId, channel))
        {
            Interlocked.CompareExchange(ref this.media, null, channel);
            channel.Closed();
            this.SendError(message.Id, nameof(BleHubException), "Not connected");
            return;
        }
        this.Send(MultipeerMessageKind.MediaOpened, message.Id, null, []);
    }


    // ---- sending ----

    void Send(MultipeerMessageKind kind, uint id, string? name, ReadOnlySpan<byte> payload)
        => this.Send(MultipeerProtocol.Encode(kind, id, name, payload));


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
            this.Send(MultipeerProtocol.Encode(MultipeerMessageKind.Error, id, null, new MultipeerError(type, message), MultipeerJsonContext.Default.MultipeerError));
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


    sealed record PendingUpload(string Name, string Path, long Size, BleHubConnectedClient Client, BleHubFileTransferOptions Options, Stopwatch Elapsed);
}
