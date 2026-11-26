using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// Carries one hub connection over a peer-link session (Multipeer, Nearby). When the session drops and the host holds the
/// connection, it opens a new session and resumes on it - same connection on the host, missed pushes replayed.
/// </summary>
internal sealed class PeerHubTransport : IHubConnectionTransport
{
    static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan MaxResumeDelay = TimeSpan.FromSeconds(2);

    readonly Func<CancellationToken, Task<IPeerConnection>>? reopen;
    readonly CancellationTokenSource lifetime = new();
    readonly IHubConnectionTransportEvents events;
    readonly string characteristicUuid;
    readonly ILogger? logger;
    readonly object sendLock = new();
    readonly ConcurrentDictionary<uint, TaskCompletionSource<PeerMessage>> replies = new();
    readonly ConcurrentDictionary<uint, Channel<PeerMessage>> streams = new();
    readonly ConcurrentDictionary<uint, TaskCompletionSource<PeerMessage>> fileDone = new();
    readonly ConcurrentDictionary<uint, PendingDownload> downloads = new();
    PeerMediaChannel? media;
    HubDisconnect? hostDisconnect;
    IPeerConnection connection = null!;
    Link? current;
    string? resumeToken;
    int resumeWindowSeconds;
    int nextId;
    int mediaMaxPacketSize;
    bool files;
    volatile bool leaving;
    volatile bool closed;
    volatile bool resuming;


    /// <param name="reopen">Opens a new session to the same host, for a resume - null when this transport can't</param>
    public PeerHubTransport(
        string transport,
        IPeerConnection connection,
        IHubConnectionTransportEvents events,
        string characteristicUuid,
        ILogger? logger,
        Func<CancellationToken, Task<IPeerConnection>>? reopen = null
    )
    {
        this.Transport = transport;
        this.events = events;
        this.characteristicUuid = characteristicUuid;
        this.logger = logger;
        this.reopen = reopen;
        this.Attach(connection);
    }


    /// <summary>
    /// Makes <paramref name="session"/> the one this connection talks over - anything an earlier session reports from now
    /// on is ignored
    /// </summary>
    void Attach(IPeerConnection session)
    {
        var link = new Link(this, session);
        this.current = link;
        this.connection = session;
        session.Start(link);
    }


    /// <summary>
    /// The transport's name, for logs
    /// </summary>
    public string Transport { get; }

    /// <summary>
    /// The host's id, from the handshake reply
    /// </summary>
    public string? HostId { get; private set; }


    public async Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken)
    {
        var reply = (await this.Request(
            PeerMessageKind.Handshake,
            this.characteristicUuid,
            id => PeerProtocol.Encode(
                PeerMessageKind.Handshake, id, this.characteristicUuid,
                new HubHandshake(PeerProtocol.Version, info.Name, info.AppVersion, info.Properties, info.Resumable && this.reopen != null),
                PeerJsonContext.Default.HubHandshake
            ),
            cancellationToken
        ).ConfigureAwait(false)).Read(PeerJsonContext.Default.PeerHandshakeReply);

        this.files = reply.Files;
        this.mediaMaxPacketSize = reply.MediaMaxPacketSize ?? 0;
        this.HostId = reply.HostId;
        this.resumeToken = reply.ResumeToken;
        this.resumeWindowSeconds = reply.ResumeWindowSeconds ?? 0;

        if (!reply.Accepted)
            this.Shutdown();

        return new HandshakeAck(reply.Accepted, reply.Reason, reply.HostName, 0, false);
    }


    public async Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken)
    {
        var reply = await this.Request(
            PeerMessageKind.Invoke,
            method,
            id => PeerProtocol.Encode(PeerMessageKind.Invoke, id, method, arguments),
            cancellationToken
        ).ConfigureAwait(false);

        return reply.Payload;
    }


    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = this.NextId();
        var items = Channel.CreateUnbounded<PeerMessage>(new UnboundedChannelOptions { SingleReader = true });
        this.streams[id] = items;
        var ended = false;
        try
        {
            this.Send(PeerProtocol.Encode(PeerMessageKind.Stream, id, method, arguments));
            await foreach (var message in items.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (message.Kind == PeerMessageKind.StreamEnd)
                {
                    ended = true;
                    yield break;
                }
                if (message.Kind == PeerMessageKind.Error)
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
                this.TrySend(PeerProtocol.Encode(PeerMessageKind.Cancel, id, null, []));
        }
    }


    public async Task Rename(string? name, CancellationToken cancellationToken)
        => await this.Request(
            PeerMessageKind.Rename,
            null,
            id => PeerProtocol.Encode(PeerMessageKind.Rename, id, null, new PeerRename(name), PeerJsonContext.Default.PeerRename),
            cancellationToken
        ).ConfigureAwait(false);


    // ---- files ----

    public bool CanTransferFiles => this.files && !this.closed && !this.resuming;


    public async Task<HubFileTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<HubTransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (!this.files)
            throw new HubFileTransferNotSupportedException("The host does not serve file transfers");

        var sw = Stopwatch.StartNew();
        var temp = Path.Combine(Path.GetTempPath(), "shiny-peerlink-" + Guid.NewGuid().ToString("N"));
        uint id = 0;
        try
        {
            // a Multipeer resource is a file - stage the stream as one first
            await using (var file = File.Create(temp))
                await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

            var reply = await this.Request(
                PeerMessageKind.FileRequest,
                null,
                x =>
                {
                    id = x;
                    this.fileDone[x] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return PeerProtocol.Encode(PeerMessageKind.FileRequest, x, null, new PeerFileRequest(true, remoteFileName, length), PeerJsonContext.Default.PeerFileRequest);
                },
                cancellationToken
            ).ConfigureAwait(false);
            ThrowIfRefused(reply.Read(PeerJsonContext.Default.PeerFileReply), "Upload");

            await this.connection.SendFile(
                temp,
                PeerProtocol.FileResourceName(id),
                fraction => progress?.Report(Progress((long)(fraction * length), length, sw.Elapsed)),
                cancellationToken
            ).ConfigureAwait(false);

            var done = (await this.fileDone[id].Task.WaitAsync(cancellationToken).ConfigureAwait(false)).Read(PeerJsonContext.Default.PeerFileDone);
            if (!done.Succeeded)
                throw new HubException($"Upload failed: {done.Message}");

            progress?.Report(Progress(length, length, sw.Elapsed));
            return new HubFileTransferResult(HubTransferDirection.Upload, remoteFileName, length, sw.Elapsed);
        }
        finally
        {
            this.fileDone.TryRemove(id, out _);
            File.Delete(temp);
        }
    }


    public async Task<HubFileTransferResult> Download(string remoteFileName, Stream destination, IProgress<HubTransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (!this.files)
            throw new HubFileTransferNotSupportedException("The host does not serve file transfers");

        var sw = Stopwatch.StartNew();
        uint id = 0;
        PendingDownload? download = null;
        try
        {
            var reply = (await this.Request(
                PeerMessageKind.FileRequest,
                null,
                x =>
                {
                    // waiting before asking: the resource follows the reply straight away
                    id = x;
                    download = new PendingDownload(sw, progress);
                    this.downloads[x] = download;
                    return PeerProtocol.Encode(PeerMessageKind.FileRequest, x, null, new PeerFileRequest(false, remoteFileName, null), PeerJsonContext.Default.PeerFileRequest);
                },
                cancellationToken
            ).ConfigureAwait(false)).Read(PeerJsonContext.Default.PeerFileReply);
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
                return new HubFileTransferResult(HubTransferDirection.Download, remoteFileName, copied, sw.Elapsed);
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

    public bool CanOpenMediaChannel => this.mediaMaxPacketSize > 0 && !this.closed && !this.resuming;


    public async Task<IHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken)
    {
        if (!this.CanOpenMediaChannel)
            throw new NotSupportedException("The host has no media channels");

        if (Volatile.Read(ref this.media) is { IsOpen: true } open)
            return open;

        var channel = new PeerMediaChannel((kind, packet) => this.Send(PeerProtocol.Encode(kind, 0, null, packet.Span)), null, this.mediaMaxPacketSize);
        Interlocked.Exchange(ref this.media, channel)?.Closed();
        try
        {
            await this.Request(
                PeerMessageKind.MediaOpen,
                null,
                id => PeerProtocol.Encode(PeerMessageKind.MediaOpen, id, null, []),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref this.media, null, channel);
            channel.Closed();
            throw new HubException($"Could not open a media channel: {ex.Message}", ex);
        }
        return channel;
    }


    // ---- closing ----

    public async Task Close(CancellationToken cancellationToken)
    {
        if (this.closed)
            return;

        this.leaving = true;
        this.lifetime.Cancel();
        this.TrySend(PeerProtocol.Encode(
            PeerMessageKind.Disconnect, 0, null,
            new HubHangUp((int)HubDisconnectReason.ClientDisconnect, null),
            PeerJsonContext.Default.HubHangUp
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
        this.lifetime.Cancel();
        this.connection.Dispose();
    }


    // ---- from the host ----

    public void Received(ReadOnlyMemory<byte> data)
    {
        PeerMessage message;
        try
        {
            message = PeerProtocol.Decode(data);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Unreadable message from the host");
            return;
        }

        switch (message.Kind)
        {
            case PeerMessageKind.Push:
                this.events.Pushed(message.Name ?? String.Empty, message.Payload);
                break;

            case PeerMessageKind.HostRenamed:
                this.events.HostRenamed(message.Read(PeerJsonContext.Default.PeerRename).Name);
                break;

            case PeerMessageKind.StreamItem:
            case PeerMessageKind.StreamEnd:
                if (this.streams.TryGetValue(message.Id, out var stream))
                    stream.Writer.TryWrite(message);
                break;

            case PeerMessageKind.Error:
                if (this.streams.TryGetValue(message.Id, out var failed))
                    failed.Writer.TryWrite(message);
                else if (this.replies.TryRemove(message.Id, out var waiting))
                    waiting.TrySetException(ToRemote(message));
                break;

            case PeerMessageKind.FileDone:
                if (this.fileDone.TryGetValue(message.Id, out var done))
                    done.TrySetResult(message);
                break;

            case PeerMessageKind.Disconnect:
                var hangUp = message.Read(PeerJsonContext.Default.HubHangUp);
                this.hostDisconnect = new HubDisconnect((HubDisconnectReason)hangUp.Reason, hangUp.Message);
                this.connection.Dispose();
                break;

            case PeerMessageKind.MediaPacket:
                Volatile.Read(ref this.media)?.Deliver(message.Payload);
                break;

            case PeerMessageKind.MediaClose:
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
        if (PeerProtocol.ReadFileResourceId(name) is { } id && this.downloads.TryGetValue(id, out var download) && download.Length is { } length)
            download.Progress?.Report(Progress((long)(fraction * length), length, download.Elapsed.Elapsed));
    }


    public void FileReceived(string name, string? path, Exception? error)
    {
        if (PeerProtocol.ReadFileResourceId(name) is not { } id || !this.downloads.TryRemove(id, out var download))
        {
            if (path != null)
                File.Delete(path);
            return;
        }

        if (path == null)
            download.Arrived.TrySetException(new HubException($"Download failed: {error?.Message ?? "the file did not arrive"}"));
        else
            download.Arrived.TrySetResult(path);
    }


    void LinkClosed(Link link)
    {
        // a session this connection already moved off
        if (!ReferenceEquals(this.current, link))
            return;

        var disconnect = this.hostDisconnect ?? new HubDisconnect(HubDisconnectReason.ClientTimeout, $"The {this.Transport} session ended");
        this.FailPending(new HubDisconnectedException("The connection to the host was closed", disconnect));

        // a resume attempt's session failing - the resume loop tries again
        if (this.resuming)
            return;

        if (this.CanResume)
        {
            this.resuming = true;
            this.events.Reconnecting(disconnect);
            _ = this.Resume(disconnect);
            return;
        }

        this.closed = true;

        // the app asked to leave (or the host refused us) - HubProxy already knows
        if (!this.leaving)
            this.events.Closed(disconnect);
    }


    // a dropped session, not a goodbye - and the host said it holds this connection
    bool CanResume =>
        !this.leaving
        && this.hostDisconnect == null
        && this.reopen != null
        && this.resumeToken != null
        && this.resumeWindowSeconds > 0;


    void FailPending(Exception gone)
    {
        foreach (var reply in this.replies.Values)
            reply.TrySetException(gone);
        foreach (var stream in this.streams.Values)
            stream.Writer.TryComplete(gone);
        foreach (var done in this.fileDone.Values)
            done.TrySetException(gone);
        foreach (var download in this.downloads.Values)
            download.Arrived.TrySetException(gone);
        Interlocked.Exchange(ref this.media, null)?.Closed();
    }


    /// <summary>
    /// Opens new sessions to the host and offers it the resume token, until it takes the connection back, refuses, or its
    /// resume window runs out
    /// </summary>
    async Task Resume(HubDisconnect drop)
    {
        this.logger?.LogInformation("{Transport} session dropped ({Reason}) - resuming", this.Transport, drop);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(this.resumeWindowSeconds);
        var delay = TimeSpan.FromMilliseconds(250);
        string? refusal = null;

        while (refusal == null && !this.leaving && DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
            attempt.CancelAfter(remaining < AttemptTimeout ? remaining : AttemptTimeout);
            var id = this.NextId();
            try
            {
                var session = await this.reopen!(attempt.Token).ConfigureAwait(false);
                var waiting = new TaskCompletionSource<PeerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                this.replies[id] = waiting;
                this.Attach(session);
                lock (this.sendLock)
                    session.Send(PeerProtocol.Encode(PeerMessageKind.Resume, id, this.characteristicUuid, new PeerResume(this.resumeToken!), PeerJsonContext.Default.PeerResume));

                var reply = (await waiting.Task.WaitAsync(attempt.Token).ConfigureAwait(false)).Read(PeerJsonContext.Default.PeerHandshakeReply);
                if (!reply.Accepted)
                {
                    refusal = reply.Reason ?? "The host could not resume the connection";
                }
                else
                {
                    this.resumeToken = reply.ResumeToken ?? this.resumeToken;
                    this.files = reply.Files;
                    this.mediaMaxPacketSize = reply.MediaMaxPacketSize ?? 0;
                    this.resuming = false;
                    this.logger?.LogInformation("Resumed over {Transport}", this.Transport);
                    this.events.Reconnected();
                    return;
                }
            }
            catch (Exception ex) when (!this.leaving)
            {
                this.logger?.LogDebug(ex, "Resume attempt failed - retrying");
                try
                {
                    await Task.Delay(delay, this.lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                delay = delay * 2 > MaxResumeDelay ? MaxResumeDelay : delay * 2;
            }
            catch (Exception)
            {
                // the app is leaving
            }
            finally
            {
                this.replies.TryRemove(id, out _);
            }
        }

        this.resuming = false;
        this.closed = true;
        try
        {
            this.connection.Dispose();
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Session did not close cleanly");
        }

        if (!this.leaving)
        {
            this.logger?.LogInformation("Could not resume over {Transport}: {Reason}", this.Transport, refusal ?? "the resume window ran out");
            this.events.Closed(new HubDisconnect(drop.Reason, refusal ?? drop.Message ?? "Could not get back to the host in time"));
        }
    }


    /// <summary>
    /// Hears one session for this connection - and goes quiet once the connection has moved to a newer one
    /// </summary>
    sealed class Link(PeerHubTransport owner, IPeerConnection session) : IPeerConnectionHandler
    {
        public IPeerConnection Session => session;

        bool IsCurrent => ReferenceEquals(owner.current, this);

        public void Received(ReadOnlyMemory<byte> message)
        {
            if (this.IsCurrent)
                owner.Received(message);
        }

        public void FileProgress(string name, double fraction)
        {
            if (this.IsCurrent)
                owner.FileProgress(name, fraction);
        }

        public void FileReceived(string name, string? path, Exception? error)
        {
            if (this.IsCurrent)
                owner.FileReceived(name, path, error);
            else if (path != null)
                File.Delete(path);
        }

        public void Closed() => owner.LinkClosed(this);
    }


    // ---- plumbing ----

    async Task<PeerMessage> Request(PeerMessageKind kind, string? name, Func<uint, byte[]> encode, CancellationToken cancellationToken)
    {
        var id = this.NextId();
        var reply = new TaskCompletionSource<PeerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.replies[id] = reply;
        try
        {
            this.Send(encode(id));
            return await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reply.Task.IsCompletedSuccessfully)
        {
            // the reply arrived just before the session ended (Context.Abort) - it still counts
            return reply.Task.Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (kind is PeerMessageKind.Invoke)
                this.TrySend(PeerProtocol.Encode(PeerMessageKind.Cancel, id, null, []));
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
            throw new HubDisconnectedException("The connection to the host was closed", this.hostDisconnect);
        if (this.resuming)
            throw new HubDisconnectedException("The link to the host dropped - resuming");

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


    static HubRemoteException ToRemote(PeerMessage message)
    {
        var error = message.Read(PeerJsonContext.Default.PeerError);
        return new HubRemoteException(error.Type, error.Message);
    }


    // the same exceptions as the Wi-Fi transport for the same refusals
    static void ThrowIfRefused(PeerFileReply reply, string what)
    {
        if (reply.Status == 200)
            return;

        var message = reply.Message ?? $"Status {reply.Status}";
        throw reply.Status == 404 && message.Contains("does not serve files", StringComparison.Ordinal)
            ? new HubFileTransferNotSupportedException(message)
            : new HubException($"{what} failed ({reply.Status}): {message}");
    }


    static HubTransferProgress Progress(long transferred, long? total, TimeSpan elapsed)
        => new(elapsed.TotalSeconds <= 0 ? 0 : (long)(transferred / elapsed.TotalSeconds), total, transferred);


    sealed class PendingDownload(Stopwatch elapsed, IProgress<HubTransferProgress>? progress)
    {
        public Stopwatch Elapsed => elapsed;
        public IProgress<HubTransferProgress>? Progress => progress;
        public long? Length { get; set; }
        public TaskCompletionSource<string> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
