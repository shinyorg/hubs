using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// Finds universal hub hosts over MultipeerConnectivity and connects to them - one session per hub connection
/// </summary>
internal sealed class MultipeerClientTransport(IMultipeerLink link, MultipeerClientOptions options, ILoggerFactory? loggerFactory) : IUniversalHubClientTransport
{
    readonly ILogger? logger = loggerFactory?.CreateLogger<MultipeerClientTransport>();


    public HubTransports Transport => HubTransports.Multipeer;


    // nothing to find where there is no MultipeerConnectivity (Android) - not an error worth logging on every scan
    public IObservable<HubHostSighting> Discover(BleHubClient client) => !link.IsSupported ? Observable.Empty<HubHostSighting>() : Observable.Create<HubHostSighting>(observer =>
    {
        // a lost peer only comes with its peer - remember what it was called when it was found
        var names = new ConcurrentDictionary<object, string>();
        return link
            .Browse(options.ServiceType, this.DisplayName(null))
            .Subscribe(
                seen =>
                {
                    if (seen.Info == null)
                    {
                        if (names.TryRemove(seen.Peer, out var gone))
                            observer.OnNext(new HubHostSighting(gone, null, x => x with { Multipeer = null }));
                    }
                    else if (this.Serves(client, seen))
                    {
                        var name = seen.Info.TryGetValue(MultipeerProtocol.InfoName, out var n) ? n : seen.DisplayName;
                        var hostId = seen.Info.TryGetValue(MultipeerProtocol.InfoHostId, out var id) ? id : null;
                        var host = new MultipeerHost(seen.DisplayName, seen.Info) { Peer = seen.Peer };
                        names[seen.Peer] = name;
                        observer.OnNext(new HubHostSighting(name, hostId, x => x with { Multipeer = host }));
                    }
                },
                observer.OnError
            );
    });


    public async Task Connect(BleHubClient client, HubHostInfo host, BleHubConnectOptions connectOptions, CancellationToken cancellationToken)
    {
        var peer = host.Multipeer?.Peer ?? throw new BleHubException($"{host.Name} was not seen over Multipeer");
        var connection = await link.Connect(peer, options.InviteTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.ConnectExternal(
                events => new MultipeerHubTransport(connection, events, client.CharacteristicUuid, loggerFactory?.CreateLogger<MultipeerHubTransport>()),
                connectOptions,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }


    string DisplayName(string? name) => MultipeerNames.Truncate(options.DisplayName ?? name ?? "Hub client");


    bool Serves(BleHubClient client, MultipeerSighting seen)
    {
        if (!seen.Info!.TryGetValue(MultipeerProtocol.InfoHubs, out var hubs))
            return false;

        var mine = client.CharacteristicUuid;
        if (!hubs.Split(',').Any(x => x.Trim().Equals(mine, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (seen.Info.TryGetValue(MultipeerProtocol.InfoProtocol, out var proto) && proto != MultipeerProtocol.Version.ToString())
        {
            this.logger?.LogInformation("Ignoring {Host}: it speaks Multipeer hub protocol {Protocol}", seen.DisplayName, proto);
            return false;
        }
        return true;
    }
}


/// <summary>
/// Carries one hub connection over a Multipeer session
/// </summary>
internal sealed class MultipeerHubTransport : IBleHubClientTransport, IHubTransportKind, IMultipeerConnectionHandler
{
    readonly IMultipeerConnection connection;
    readonly IBleHubClientTransportEvents events;
    readonly string characteristicUuid;
    readonly ILogger? logger;
    readonly object sendLock = new();
    readonly ConcurrentDictionary<uint, TaskCompletionSource<MultipeerMessage>> replies = new();
    readonly ConcurrentDictionary<uint, Channel<MultipeerMessage>> streams = new();
    readonly ConcurrentDictionary<uint, TaskCompletionSource<MultipeerMessage>> fileDone = new();
    readonly ConcurrentDictionary<uint, PendingDownload> downloads = new();
    MultipeerMediaChannel? media;
    HubDisconnect? hostDisconnect;
    int nextId;
    int mediaMaxPacketSize;
    bool files;
    volatile bool leaving;
    volatile bool closed;


    public MultipeerHubTransport(IMultipeerConnection connection, IBleHubClientTransportEvents events, string characteristicUuid, ILogger? logger)
    {
        this.connection = connection;
        this.events = events;
        this.characteristicUuid = characteristicUuid;
        this.logger = logger;
        connection.Start(this);
    }


    public HubTransports Transport => HubTransports.Multipeer;

    /// <summary>
    /// The host's id, from the handshake reply
    /// </summary>
    public string? HostId { get; private set; }


    public async Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken)
    {
        var reply = (await this.Request(
            MultipeerMessageKind.Handshake,
            this.characteristicUuid,
            id => MultipeerProtocol.Encode(
                MultipeerMessageKind.Handshake, id, this.characteristicUuid,
                new HubHandshake(MultipeerProtocol.Version, info.Name, info.AppVersion, info.Properties),
                MultipeerJsonContext.Default.HubHandshake
            ),
            cancellationToken
        ).ConfigureAwait(false)).Read(MultipeerJsonContext.Default.MultipeerHandshakeReply);

        this.files = reply.Files;
        this.mediaMaxPacketSize = reply.MediaMaxPacketSize ?? 0;
        this.HostId = reply.HostId;

        if (!reply.Accepted)
            this.Shutdown();

        return new HandshakeAck(reply.Accepted, reply.Reason, reply.HostName, 0, false);
    }


    public async Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken)
    {
        var reply = await this.Request(
            MultipeerMessageKind.Invoke,
            method,
            id => MultipeerProtocol.Encode(MultipeerMessageKind.Invoke, id, method, arguments),
            cancellationToken
        ).ConfigureAwait(false);

        return reply.Payload;
    }


    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = this.NextId();
        var items = Channel.CreateUnbounded<MultipeerMessage>(new UnboundedChannelOptions { SingleReader = true });
        this.streams[id] = items;
        var ended = false;
        try
        {
            this.Send(MultipeerProtocol.Encode(MultipeerMessageKind.Stream, id, method, arguments));
            await foreach (var message in items.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (message.Kind == MultipeerMessageKind.StreamEnd)
                {
                    ended = true;
                    yield break;
                }
                if (message.Kind == MultipeerMessageKind.Error)
                {
                    ended = true;
                    throw ToRemote(message);
                }
                yield return message.Payload;
            }
        }
        finally
        {
            this.streams.TryRemove(id, out _);
            if (!ended && !this.closed)
                this.TrySend(MultipeerProtocol.Encode(MultipeerMessageKind.Cancel, id, null, []));
        }
    }


    public async Task Rename(string? name, CancellationToken cancellationToken)
        => await this.Request(
            MultipeerMessageKind.Rename,
            null,
            id => MultipeerProtocol.Encode(MultipeerMessageKind.Rename, id, null, new MultipeerRename(name), MultipeerJsonContext.Default.MultipeerRename),
            cancellationToken
        ).ConfigureAwait(false);


    // ---- files ----

    public bool CanTransferFiles => this.files && !this.closed;


    public async Task<L2CapTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (!this.files)
            throw new BleHubFileTransferNotSupportedException("The host does not serve file transfers");

        var sw = Stopwatch.StartNew();
        var temp = Path.Combine(Path.GetTempPath(), "shiny-multipeer-" + Guid.NewGuid().ToString("N"));
        uint id = 0;
        try
        {
            // a Multipeer resource is a file - stage the stream as one first
            await using (var file = File.Create(temp))
                await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

            var reply = await this.Request(
                MultipeerMessageKind.FileRequest,
                null,
                x =>
                {
                    id = x;
                    this.fileDone[x] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return MultipeerProtocol.Encode(MultipeerMessageKind.FileRequest, x, null, new MultipeerFileRequest(true, remoteFileName, length), MultipeerJsonContext.Default.MultipeerFileRequest);
                },
                cancellationToken
            ).ConfigureAwait(false);
            ThrowIfRefused(reply.Read(MultipeerJsonContext.Default.MultipeerFileReply), "Upload");

            await this.connection.SendFile(
                temp,
                MultipeerProtocol.FileResourceName(id),
                fraction => progress?.Report(Progress((long)(fraction * length), length, sw.Elapsed)),
                cancellationToken
            ).ConfigureAwait(false);

            var done = (await this.fileDone[id].Task.WaitAsync(cancellationToken).ConfigureAwait(false)).Read(MultipeerJsonContext.Default.MultipeerFileDone);
            if (!done.Succeeded)
                throw new BleHubException($"Upload failed: {done.Message}");

            progress?.Report(Progress(length, length, sw.Elapsed));
            return new L2CapTransferResult(L2CapTransferType.Upload, remoteFileName, length, sw.Elapsed);
        }
        finally
        {
            this.fileDone.TryRemove(id, out _);
            File.Delete(temp);
        }
    }


    public async Task<L2CapTransferResult> Download(string remoteFileName, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (!this.files)
            throw new BleHubFileTransferNotSupportedException("The host does not serve file transfers");

        var sw = Stopwatch.StartNew();
        uint id = 0;
        PendingDownload? download = null;
        try
        {
            var reply = (await this.Request(
                MultipeerMessageKind.FileRequest,
                null,
                x =>
                {
                    // waiting before asking: the resource follows the reply straight away
                    id = x;
                    download = new PendingDownload(sw, progress);
                    this.downloads[x] = download;
                    return MultipeerProtocol.Encode(MultipeerMessageKind.FileRequest, x, null, new MultipeerFileRequest(false, remoteFileName, null), MultipeerJsonContext.Default.MultipeerFileRequest);
                },
                cancellationToken
            ).ConfigureAwait(false)).Read(MultipeerJsonContext.Default.MultipeerFileReply);
            ThrowIfRefused(reply, "Download");
            download!.Length = reply.Length;

            var path = await download.Arrived.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long copied;
                await using (var file = File.OpenRead(path))
                {
                    await file.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    copied = file.Length;
                }
                progress?.Report(Progress(copied, copied, sw.Elapsed));
                return new L2CapTransferResult(L2CapTransferType.Download, remoteFileName, copied, sw.Elapsed);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            this.downloads.TryRemove(id, out _);
        }
    }


    // ---- media ----

    public bool CanOpenMediaChannel => this.mediaMaxPacketSize > 0 && !this.closed;


    public async Task<IBleHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken)
    {
        if (!this.CanOpenMediaChannel)
            throw new NotSupportedException("The host has no media channels");

        if (Volatile.Read(ref this.media) is { IsOpen: true } open)
            return open;

        var channel = new MultipeerMediaChannel((kind, packet) => this.Send(MultipeerProtocol.Encode(kind, 0, null, packet.Span)), null, this.mediaMaxPacketSize);
        Interlocked.Exchange(ref this.media, channel)?.Closed();
        try
        {
            await this.Request(
                MultipeerMessageKind.MediaOpen,
                null,
                id => MultipeerProtocol.Encode(MultipeerMessageKind.MediaOpen, id, null, []),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref this.media, null, channel);
            channel.Closed();
            throw new BleHubException($"Could not open a media channel: {ex.Message}", ex);
        }
        return channel;
    }


    // ---- closing ----

    public async Task Close(CancellationToken cancellationToken)
    {
        if (this.closed)
            return;

        this.leaving = true;
        this.TrySend(MultipeerProtocol.Encode(
            MultipeerMessageKind.Disconnect, 0, null,
            new HubHangUp((int)HubDisconnectReason.ClientDisconnect, null),
            MultipeerJsonContext.Default.HubHangUp
        ));

        // give the goodbye a moment to leave before the session goes - otherwise the host sees a dropped link
        try
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        this.connection.Dispose();
    }


    void Shutdown()
    {
        this.leaving = true;
        this.connection.Dispose();
    }


    // ---- from the host ----

    public void Received(ReadOnlyMemory<byte> data)
    {
        MultipeerMessage message;
        try
        {
            message = MultipeerProtocol.Decode(data);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Unreadable message from the host");
            return;
        }

        switch (message.Kind)
        {
            case MultipeerMessageKind.Push:
                this.events.Pushed(message.Name ?? String.Empty, message.Payload);
                break;

            case MultipeerMessageKind.HostRenamed:
                this.events.HostRenamed(message.Read(MultipeerJsonContext.Default.MultipeerRename).Name);
                break;

            case MultipeerMessageKind.StreamItem:
            case MultipeerMessageKind.StreamEnd:
                if (this.streams.TryGetValue(message.Id, out var stream))
                    stream.Writer.TryWrite(message);
                break;

            case MultipeerMessageKind.Error:
                if (this.streams.TryGetValue(message.Id, out var failed))
                    failed.Writer.TryWrite(message);
                else if (this.replies.TryRemove(message.Id, out var waiting))
                    waiting.TrySetException(ToRemote(message));
                break;

            case MultipeerMessageKind.FileDone:
                if (this.fileDone.TryGetValue(message.Id, out var done))
                    done.TrySetResult(message);
                break;

            case MultipeerMessageKind.Disconnect:
                var hangUp = message.Read(MultipeerJsonContext.Default.HubHangUp);
                this.hostDisconnect = new HubDisconnect((HubDisconnectReason)hangUp.Reason, hangUp.Message);
                this.connection.Dispose();
                break;

            case MultipeerMessageKind.MediaPacket:
                Volatile.Read(ref this.media)?.Deliver(message.Payload);
                break;

            case MultipeerMessageKind.MediaClose:
                Interlocked.Exchange(ref this.media, null)?.Closed();
                break;

            default:
                // HandshakeReply, Completion, FileReply, MediaOpened
                if (this.replies.TryRemove(message.Id, out var reply))
                    reply.TrySetResult(message);
                break;
        }
    }


    public void FileProgress(string name, double fraction)
    {
        if (MultipeerProtocol.ReadFileResourceId(name) is { } id && this.downloads.TryGetValue(id, out var download) && download.Length is { } length)
            download.Progress?.Report(Progress((long)(fraction * length), length, download.Elapsed.Elapsed));
    }


    public void FileReceived(string name, string? path, Exception? error)
    {
        if (MultipeerProtocol.ReadFileResourceId(name) is not { } id || !this.downloads.TryRemove(id, out var download))
        {
            if (path != null)
                File.Delete(path);
            return;
        }

        if (path == null)
            download.Arrived.TrySetException(new BleHubException($"Download failed: {error?.Message ?? "the file did not arrive"}"));
        else
            download.Arrived.TrySetResult(path);
    }


    public void Closed()
    {
        this.closed = true;

        var disconnect = this.hostDisconnect ?? new HubDisconnect(HubDisconnectReason.ClientTimeout, "The Multipeer session ended");
        var gone = new BleHubDisconnectedException("The connection to the host was closed", disconnect);
        foreach (var reply in this.replies.Values)
            reply.TrySetException(gone);
        foreach (var stream in this.streams.Values)
            stream.Writer.TryComplete(gone);
        foreach (var done in this.fileDone.Values)
            done.TrySetException(gone);
        foreach (var download in this.downloads.Values)
            download.Arrived.TrySetException(gone);
        Interlocked.Exchange(ref this.media, null)?.Closed();

        // the app asked to leave (or the host refused us) - BleHubClient already knows
        if (!this.leaving)
            this.events.Closed(disconnect);
    }


    // ---- plumbing ----

    async Task<MultipeerMessage> Request(MultipeerMessageKind kind, string? name, Func<uint, byte[]> encode, CancellationToken cancellationToken)
    {
        var id = this.NextId();
        var reply = new TaskCompletionSource<MultipeerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.replies[id] = reply;
        try
        {
            this.Send(encode(id));
            return await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (kind is MultipeerMessageKind.Invoke)
                this.TrySend(MultipeerProtocol.Encode(MultipeerMessageKind.Cancel, id, null, []));
            throw;
        }
        finally
        {
            this.replies.TryRemove(id, out _);
        }
    }


    uint NextId() => (uint)Interlocked.Increment(ref this.nextId);


    void Send(byte[] message)
    {
        if (this.closed)
            throw new BleHubDisconnectedException("The connection to the host was closed", this.hostDisconnect);

        lock (this.sendLock)
            this.connection.Send(message);
    }


    void TrySend(byte[] message)
    {
        try
        {
            this.Send(message);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Could not send to the host");
        }
    }


    static BleHubRemoteException ToRemote(MultipeerMessage message)
    {
        var error = message.Read(MultipeerJsonContext.Default.MultipeerError);
        return new BleHubRemoteException(error.Type, error.Message);
    }


    // the same exceptions as the Wi-Fi transport for the same refusals
    static void ThrowIfRefused(MultipeerFileReply reply, string what)
    {
        if (reply.Status == 200)
            return;

        var message = reply.Message ?? $"Status {reply.Status}";
        throw reply.Status == 404 && message.Contains("does not serve files", StringComparison.Ordinal)
            ? new BleHubFileTransferNotSupportedException(message)
            : new BleHubException($"{what} failed ({reply.Status}): {message}");
    }


    static TransferProgress Progress(long transferred, long? total, TimeSpan elapsed)
        => new(elapsed.TotalSeconds <= 0 ? 0 : (long)(transferred / elapsed.TotalSeconds), total, transferred);


    sealed class PendingDownload(Stopwatch elapsed, IProgress<TransferProgress>? progress)
    {
        public Stopwatch Elapsed => elapsed;
        public IProgress<TransferProgress>? Progress => progress;
        public long? Length { get; set; }
        public TaskCompletionSource<string> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
