using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Shiny.UniversalHubs;

/// <summary>
/// Hub sessions over one device's Nearby Connections. Nearby allows one connection between two devices and one
/// advertisement and one discovery per app, so this sits between the radio and every hub host and client: it shares
/// discovery, keeps one connection per remote device, and carries each hub session as a channel inside it - in either
/// direction, so two devices can visit each other's hubs over the one connection.
/// </summary>
internal sealed class NearbyLink : INearbyRadioListener
{
    readonly INearbyRadio radio;
    readonly ILogger? logger;
    readonly Channel<Action> inbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    readonly object gate = new();
    readonly Dictionary<string, NearbyRemote> remotes = new(StringComparer.Ordinal);
    readonly Dictionary<string, NearbyEndpointInfo> found = new(StringComparer.Ordinal);
    readonly List<IObserver<NearbySighting>> discovering = new();
    readonly ConcurrentDictionary<long, OutgoingFile> outgoing = new();
    readonly ConcurrentDictionary<long, IncomingFile> incoming = new();
    // held while a file starts sending and while a file update is matched, so an early update finds its outgoing file
    readonly object fileGate = new();
    readonly int maxMessageSize;
    Advertisement? advertisement;
    (string ServiceId, NearbyStrategy Strategy)? service;
    uint nextChannel;


    /// <summary>
    /// Incoming files a remote may have announced or started without both halves (name and file) arriving yet
    /// </summary>
    internal const int MaxPendingFilesPerRemote = 32;

    /// <summary>
    /// Room for the peer-link framing around the largest payload or media packet
    /// </summary>
    internal const int MessageHeadroom = 64 * 1024;


    public NearbyLink(INearbyRadio radio, ILogger<NearbyLink>? logger = null, HubProtocolOptions? protocol = null, UniversalServerOptions? server = null)
    {
        this.radio = radio;
        this.logger = logger;

        // a message reassembled from Part frames is never bigger than a payload or media packet - past that it's an attack
        this.maxMessageSize = Math.Max(
            protocol?.MaxPayloadSize ?? new HubProtocolOptions().MaxPayloadSize,
            server?.MediaChannels?.MaxPacketSize ?? 0
        ) + MessageHeadroom;

        radio.Start(this);

        // the radio calls back on whatever thread it likes - everything happens here, one thing at a time, in order
        _ = Task.Run(async () =>
        {
            await foreach (var action in this.inbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex, "Nearby callback failed");
                }
            }
        });
    }


    public bool IsSupported => this.radio.IsSupported;


    // ---- advertising ----

    /// <summary>
    /// Advertises <paramref name="info"/> and hands every channel a client opens to <paramref name="accepted"/>. Calling
    /// it again replaces the advertisement; dispose to stop. Channels already open carry on.
    /// </summary>
    public async Task<IDisposable> Advertise(string serviceId, NearbyStrategy strategy, NearbyEndpointInfo info, Action<IPeerConnection> accepted)
    {
        this.UseService(serviceId, strategy);
        var bytes = NearbyProtocol.EncodeEndpointInfo(info);
        var ad = new Advertisement(accepted);

        Advertisement? previous;
        lock (this.gate)
        {
            previous = this.advertisement;
            this.advertisement = ad;
        }
        if (previous != null)
            this.radio.StopAdvertising();

        try
        {
            await this.radio.StartAdvertising(serviceId, bytes, strategy).ConfigureAwait(false);
        }
        catch
        {
            lock (this.gate)
            {
                if (this.advertisement == ad)
                    this.advertisement = null;
            }
            throw;
        }

        return Disposable.Create(() =>
        {
            lock (this.gate)
            {
                if (this.advertisement != ad)
                    return;
                this.advertisement = null;
            }
            this.radio.StopAdvertising();
        });
    }


    // ---- discovery ----

    /// <summary>
    /// Hub hosts as they appear and go - one discovery for every subscriber
    /// </summary>
    public IObservable<NearbySighting> Discover(string serviceId, NearbyStrategy strategy) => Observable.Create<NearbySighting>(async observer =>
    {
        this.UseService(serviceId, strategy);

        bool first;
        List<NearbySighting> current;
        lock (this.gate)
        {
            first = this.discovering.Count == 0;
            this.discovering.Add(observer);
            current = this.found.Select(x => new NearbySighting(x.Key, x.Value)).ToList();
        }
        foreach (var seen in current)
            observer.OnNext(seen);

        if (first)
        {
            try
            {
                await this.radio.StartDiscovery(serviceId, strategy).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (this.gate)
                    this.discovering.Remove(observer);
                observer.OnError(ex);
                return Disposable.Empty;
            }
        }

        return Disposable.Create(() =>
        {
            bool last;
            lock (this.gate)
            {
                last = this.discovering.Remove(observer) && this.discovering.Count == 0;
                if (last)
                    this.found.Clear();
            }
            if (last)
                this.radio.StopDiscovery();
        });
    });


    // ---- connecting ----

    /// <summary>
    /// Opens a channel to <paramref name="endpointId"/>, connecting to it first unless this device already is (either way)
    /// </summary>
    public async Task<IPeerConnection> Connect(string endpointId, string localName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        NearbyRemote remote;
        var request = false;
        lock (this.gate)
        {
            if (!this.remotes.TryGetValue(endpointId, out remote!))
            {
                remote = new NearbyRemote(endpointId);
                this.remotes[endpointId] = remote;
                request = true;
            }
        }

        if (request)
        {
            try
            {
                await this.radio.RequestConnection(endpointId, NearbyProtocol.EncodeRequestInfo(localName)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Forget(remote, ex);
                throw new HubException($"Nearby could not reach the host: {ex.Message}", ex);
            }
        }

        try
        {
            await remote.Connected.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            this.GiveUp(remote, request);
            throw new HubException("The host did not accept the Nearby connection in time");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the request is still out - a connection it completes later is hung up in ConnectionResult
            this.GiveUp(remote, request);
            throw;
        }

        var channel = this.OpenChannel(remote, mine: true, Interlocked.Increment(ref this.nextChannel));
        this.Send(remote, NearbyProtocol.Frame(NearbyFrameKind.Open, true, channel.Id, []));
        return channel;
    }


    // ---- from the radio ----

    public void EndpointFound(string endpointId, byte[] info) => this.Post(() =>
    {
        if (NearbyProtocol.DecodeEndpointInfo(info) is not { } host)
        {
            this.logger?.LogDebug("Ignoring Nearby endpoint {Endpoint}: not a hub host this version understands", endpointId);
            return;
        }

        List<IObserver<NearbySighting>> watching;
        lock (this.gate)
        {
            if (this.discovering.Count == 0)
                return;
            this.found[endpointId] = host;
            watching = this.discovering.ToList();
        }
        foreach (var observer in watching)
            observer.OnNext(new NearbySighting(endpointId, host));
    });


    public void EndpointLost(string endpointId) => this.Post(() =>
    {
        List<IObserver<NearbySighting>> watching;
        lock (this.gate)
        {
            if (!this.found.Remove(endpointId))
                return;
            watching = this.discovering.ToList();
        }
        foreach (var observer in watching)
            observer.OnNext(new NearbySighting(endpointId, null));
    });


    public void ConnectionInitiated(string endpointId, byte[] info, bool incoming) => this.Post(() =>
    {
        NearbyRemote? remote;
        bool accept;
        lock (this.gate)
        {
            this.remotes.TryGetValue(endpointId, out remote);
            accept = !incoming ? remote != null : this.advertisement != null;
            if (incoming && accept && remote == null)
            {
                remote = new NearbyRemote(endpointId);
                this.remotes[endpointId] = remote;
            }
        }

        if (!accept)
        {
            this.logger?.LogDebug("Refusing a Nearby connection from {Endpoint}: nothing to offer it", endpointId);
            this.radio.RejectConnection(endpointId);
            return;
        }

        remote!.Name = incoming ? NearbyProtocol.DecodeRequestInfo(info) : remote.Name;
        _ = this.Accept(remote);
    });


    public void ConnectionResult(string endpointId, bool connected, string? error) => this.Post(() =>
    {
        NearbyRemote? remote;
        lock (this.gate)
            this.remotes.TryGetValue(endpointId, out remote);
        if (remote == null)
        {
            // a connection nobody wants any more (its Connect timed out or was canceled) - don't leave it open
            if (connected)
            {
                this.logger?.LogDebug("Hanging up an abandoned Nearby connection to {Endpoint}", endpointId);
                this.radio.Disconnect(endpointId);
            }
            return;
        }

        if (connected)
        {
            this.logger?.LogDebug("Nearby connected to {Endpoint}", endpointId);
            remote.Connected.TrySetResult();
        }
        else
        {
            this.logger?.LogInformation("Nearby connection to {Endpoint} failed: {Error}", endpointId, error);
            this.Forget(remote, new HubException($"Nearby connection refused: {error ?? "rejected"}"));
        }
    });


    public void Disconnected(string endpointId) => this.Post(() =>
    {
        NearbyRemote? remote;
        lock (this.gate)
            this.remotes.TryGetValue(endpointId, out remote);
        if (remote != null)
            this.Forget(remote, new HubDisconnectedException("The Nearby connection was lost"));
    });


    public void BytesReceived(string endpointId, byte[] data) => this.Post(() =>
    {
        NearbyRemote? remote;
        lock (this.gate)
            this.remotes.TryGetValue(endpointId, out remote);
        if (remote == null)
            return;

        try
        {
            this.Receive(remote, data);
        }
        catch (HubProtocolException ex)
        {
            this.logger?.LogWarning(ex, "Dropping {Endpoint}: unreadable Nearby frame", endpointId);
            this.radio.Disconnect(endpointId);
            this.Forget(remote, ex);
        }
    });


    public void FileTransfer(string endpointId, long payloadId, long transferred, long total, NearbyTransferStatus status, string? receivedPath) => this.Post(() =>
    {
        OutgoingFile? sending;
        lock (this.fileGate)
            this.outgoing.TryGetValue(payloadId, out sending);

        if (sending != null)
        {
            switch (status)
            {
                case NearbyTransferStatus.InProgress:
                    sending.Progress?.Invoke(total <= 0 ? 0 : (double)transferred / total);
                    break;

                case NearbyTransferStatus.Success:
                    sending.Progress?.Invoke(1);
                    sending.Done.TrySetResult();
                    break;

                default:
                    sending.Done.TrySetException(new HubException($"The file transfer {(status == NearbyTransferStatus.Canceled ? "was canceled" : "failed")}"));
                    break;
            }
            return;
        }

        // an incoming file from a device this link has forgotten (or never knew) - nobody will ever name it
        NearbyRemote? remote;
        lock (this.gate)
            this.remotes.TryGetValue(endpointId, out remote);
        if (remote == null)
        {
            DeleteQuietly(receivedPath);
            return;
        }

        // an incoming file: the name frame may come before or after the payload - whichever is second delivers it
        if (!this.incoming.TryGetValue(payloadId, out var file))
        {
            if (this.PendingFiles(remote) >= MaxPendingFilesPerRemote)
            {
                this.logger?.LogWarning("Dropping {Endpoint}: too many unfinished Nearby files", endpointId);
                DeleteQuietly(receivedPath);
                this.radio.CancelFile(payloadId);
                this.radio.Disconnect(endpointId);
                this.Forget(remote, new HubProtocolException("Too many unfinished files"));
                return;
            }
            file = this.incoming.GetOrAdd(payloadId, _ => new IncomingFile());
        }
        file.Endpoint = endpointId;
        switch (status)
        {
            case NearbyTransferStatus.InProgress:
                if (file.Channel != null && file.Name != null && total > 0)
                    file.Channel.FileProgress(file.Name, (double)transferred / total);
                break;

            case NearbyTransferStatus.Success:
                file.Path = receivedPath;
                file.Done = true;
                this.TryDeliver(payloadId, file);
                break;

            default:
                file.Error = new HubException("The file did not arrive");
                file.Done = true;
                this.TryDeliver(payloadId, file);
                break;
        }
    });


    // ---- channels ----

    internal void Send(NearbyChannel channel, byte[] message)
    {
        // a channel's frames go out together, so pieces of two messages never interleave
        lock (channel.SendLock)
        {
            foreach (var frame in NearbyProtocol.Split(channel.Mine, channel.Id, message))
                this.Send(channel.Remote, frame);
        }
    }


    internal async Task SendFile(NearbyChannel channel, string path, string name, Action<double>? progress, CancellationToken cancellationToken)
    {
        var file = new OutgoingFile(channel.Remote, progress);
        long id;
        lock (channel.SendLock)
        {
            // registered before an update for it can be matched - the radio may report on it before SendFile returns
            lock (this.fileGate)
            {
                id = this.radio.SendFile(channel.Remote.EndpointId, path);
                this.outgoing[id] = file;
            }
            try
            {
                this.Send(channel.Remote, NearbyProtocol.FileNameFrame(channel.Mine, channel.Id, id, name));
            }
            catch
            {
                this.outgoing.TryRemove(id, out _);
                this.radio.CancelFile(id);
                throw;
            }
        }

        // the connection may have gone just before the file was registered - Forget didn't see it then
        lock (this.gate)
        {
            if (!this.remotes.TryGetValue(channel.Remote.EndpointId, out var current) || current != channel.Remote)
                file.Done.TrySetException(new HubDisconnectedException("The Nearby connection has ended"));
        }

        try
        {
            using var reg = cancellationToken.Register(() => this.radio.CancelFile(id));
            await file.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.outgoing.TryRemove(id, out _);
        }
    }


    internal void Close(NearbyChannel channel)
    {
        var remote = channel.Remote;
        bool wasOpen;
        lock (this.gate)
            wasOpen = remote.Channels.Remove(channel.Key);
        if (!wasOpen)
            return;

        try
        {
            this.Send(remote, NearbyProtocol.Frame(NearbyFrameKind.Close, channel.Mine, channel.Id, []));
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Could not tell {Endpoint} a channel closed", remote.EndpointId);
        }
        channel.Closed();
        this.LingerOrHangUp(remote);
    }


    void Receive(NearbyRemote remote, byte[] data)
    {
        var (kind, senderOpened, id, body) = NearbyProtocol.ReadFrame(data);
        var key = (Mine: !senderOpened, Id: id);

        NearbyChannel? channel;
        lock (this.gate)
            remote.Channels.TryGetValue(key, out channel);

        switch (kind)
        {
            case NearbyFrameKind.Open:
                Advertisement? ad;
                lock (this.gate)
                    ad = this.advertisement;
                if (!senderOpened || channel != null)
                    throw new HubProtocolException("Unexpected channel open");

                if (ad == null)
                {
                    this.Send(remote, NearbyProtocol.Frame(NearbyFrameKind.Close, false, id, []));
                    return;
                }
                ad.Accepted(this.OpenChannel(remote, mine: false, id));
                break;

            case NearbyFrameKind.Message:
                channel?.Deliver(body);
                break;

            case NearbyFrameKind.Part:
            case NearbyFrameKind.Last:
                if (channel == null)
                    return;

                channel.Assembling ??= new MemoryStream();
                if (channel.Assembling.Length + body.Length > this.maxMessageSize)
                {
                    channel.Assembling = null;
                    throw new HubProtocolException($"A Nearby message over {this.maxMessageSize} bytes");
                }
                channel.Assembling.Write(body.Span);
                if (kind == NearbyFrameKind.Last)
                {
                    var whole = channel.Assembling.ToArray();
                    channel.Assembling = null;
                    channel.Deliver(whole);
                }
                break;

            case NearbyFrameKind.Close:
                if (channel == null)
                    return;
                lock (this.gate)
                    remote.Channels.Remove(key);
                channel.Closed();
                this.LingerOrHangUp(remote);
                break;

            case NearbyFrameKind.FileName:
                if (body.Length < 8)
                    return;

                var payloadId = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(body.Span);
                if (!this.incoming.TryGetValue(payloadId, out var file))
                {
                    if (this.PendingFiles(remote) >= MaxPendingFilesPerRemote)
                        throw new HubProtocolException("Too many unfinished files");
                    file = this.incoming.GetOrAdd(payloadId, _ => new IncomingFile());
                }
                file.Endpoint = remote.EndpointId;
                file.Name = System.Text.Encoding.UTF8.GetString(body.Span[8..]);

                // a file for a channel that has already closed is deleted once it arrives
                file.Channel = channel;
                file.Discard = channel == null;
                this.TryDeliver(payloadId, file);
                break;

            default:
                this.logger?.LogDebug("Ignoring Nearby frame {Kind} from {Endpoint}", kind, remote.EndpointId);
                break;
        }
    }


    NearbyChannel OpenChannel(NearbyRemote remote, bool mine, uint id)
    {
        var channel = new NearbyChannel(this, remote, mine, id);
        lock (this.gate)
        {
            remote.Channels[channel.Key] = channel;
            remote.Idle?.Cancel();
            remote.Idle = null;
        }
        return channel;
    }


    void TryDeliver(long payloadId, IncomingFile file)
    {
        if (!file.Done || file.Name == null || (file.Channel == null && !file.Discard))
            return;

        if (!this.incoming.TryRemove(payloadId, out _))
            return;

        // nobody to hand it to - the file is this link's, so it goes
        if (file.Channel == null || file.Channel.IsClosed)
            DeleteQuietly(file.Path);
        else
            file.Channel.FileReceived(file.Name, file.Path, file.Error);
    }


    int PendingFiles(NearbyRemote remote) => this.incoming.Values.Count(x => x.Endpoint == remote.EndpointId || x.Channel?.Remote == remote);


    void GiveUp(NearbyRemote remote, bool requested)
    {
        if (!requested)
            return;

        this.radio.Disconnect(remote.EndpointId);
        this.Forget(remote, null);
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
            // a temp file - nothing more to do
        }
    }


    /// <summary>
    /// The last channel to a device closed - hang up unless another opens soon (a client switching hubs)
    /// </summary>
    void LingerOrHangUp(NearbyRemote remote)
    {
        CancellationTokenSource idle;
        lock (this.gate)
        {
            if (remote.Channels.Count > 0 || !this.remotes.ContainsKey(remote.EndpointId))
                return;

            remote.Idle?.Cancel();
            idle = remote.Idle = new CancellationTokenSource();
        }

        _ = Task.Delay(TimeSpan.FromSeconds(2), idle.Token).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;

            lock (this.gate)
            {
                if (remote.Channels.Count > 0 || remote.Idle != idle)
                    return;
            }
            this.radio.Disconnect(remote.EndpointId);
            this.Post(() => this.Forget(remote, null));
        }, TaskScheduler.Default);
    }


    void Forget(NearbyRemote remote, Exception? error)
    {
        List<NearbyChannel> channels;
        lock (this.gate)
        {
            if (this.remotes.TryGetValue(remote.EndpointId, out var current) && current == remote)
                this.remotes.Remove(remote.EndpointId);
            channels = remote.Channels.Values.ToList();
            remote.Channels.Clear();
            remote.Idle?.Cancel();
        }

        if (error != null)
            remote.Connected.TrySetException(error);
        else
            remote.Connected.TrySetCanceled();

        foreach (var channel in channels)
            channel.Closed();

        foreach (var (id, file) in this.incoming.Where(x => x.Value.Endpoint == remote.EndpointId || x.Value.Channel?.Remote == remote).ToList())
        {
            if (this.incoming.TryRemove(id, out _))
                DeleteQuietly(file.Path);
        }

        // files on their way out to it will never hear how they went
        var ending = error ?? new HubDisconnectedException("The Nearby connection has ended");
        foreach (var (id, file) in this.outgoing.Where(x => x.Value.Remote == remote).ToList())
        {
            this.outgoing.TryRemove(id, out _);
            file.Done.TrySetException(ending);
        }
    }


    async Task Accept(NearbyRemote remote)
    {
        try
        {
            await this.radio.AcceptConnection(remote.EndpointId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogInformation(ex, "Could not accept the Nearby connection from {Endpoint}", remote.EndpointId);
            this.Post(() => this.Forget(remote, ex));
        }
    }


    void Send(NearbyRemote remote, byte[] frame)
    {
        if (!remote.Connected.Task.IsCompletedSuccessfully)
            throw new HubDisconnectedException("The Nearby connection has ended");

        this.radio.SendBytes(remote.EndpointId, frame);
    }


    void UseService(string serviceId, NearbyStrategy strategy)
    {
        lock (this.gate)
        {
            if (this.service is { } current && current != (serviceId, strategy))
                throw new InvalidOperationException($"Nearby is set up as '{current.ServiceId}' ({current.Strategy}) - every Nearby host and client in an app uses the same service id and strategy, not '{serviceId}' ({strategy})");

            this.service = (serviceId, strategy);
        }
    }


    void Post(Action action) => this.inbox.Writer.TryWrite(action);


    sealed record Advertisement(Action<IPeerConnection> Accepted);

    sealed class OutgoingFile(NearbyRemote remote, Action<double>? progress)
    {
        public NearbyRemote Remote => remote;
        public Action<double>? Progress => progress;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class IncomingFile
    {
        public string? Endpoint { get; set; }
        public NearbyChannel? Channel { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }
        public Exception? Error { get; set; }
        public bool Done { get; set; }

        /// <summary>
        /// Named for a channel that had already closed - deleted when it arrives
        /// </summary>
        public bool Discard { get; set; }
    }
}


/// <summary>
/// A hub host seen over Nearby
/// </summary>
/// <param name="EndpointId">Nearby's id for it</param>
/// <param name="Info">What it advertises; null when it has gone</param>
internal sealed record NearbySighting(string EndpointId, NearbyEndpointInfo? Info);


/// <summary>
/// One remote device and the channels open to it
/// </summary>
internal sealed class NearbyRemote(string endpointId)
{
    public string EndpointId => endpointId;
    public string Name { get; set; } = "Hub peer";
    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Dictionary<(bool Mine, uint Id), NearbyChannel> Channels { get; } = new();
    public CancellationTokenSource? Idle { get; set; }
}


/// <summary>
/// One hub session: a channel inside the connection to a remote device
/// </summary>
internal sealed class NearbyChannel(NearbyLink link, NearbyRemote remote, bool mine, uint id) : PeerConnectionBase
{
    public NearbyRemote Remote => remote;

    /// <summary>
    /// Whether this device opened it - the two ends number their own channels
    /// </summary>
    public bool Mine => mine;
    public uint Id => id;
    public (bool Mine, uint Id) Key => (mine, id);
    public object SendLock { get; } = new();
    public MemoryStream? Assembling { get; set; }

    public override string RemoteName => remote.Name;


    public override void Send(byte[] message)
    {
        if (this.IsClosed)
            throw new HubDisconnectedException("The Nearby session has ended");

        link.Send(this, message);
    }


    public override Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken)
    {
        if (this.IsClosed)
            throw new HubDisconnectedException("The Nearby session has ended");

        return link.SendFile(this, path, name, progress, cancellationToken);
    }


    public override void Dispose() => link.Close(this);

    internal void Deliver(ReadOnlyMemory<byte> message) => this.RaiseReceived(message);
    internal void FileProgress(string name, double fraction) => this.RaiseFileProgress(name, fraction);
    internal void FileReceived(string name, string? path, Exception? error) => this.RaiseFileReceived(name, path, error);
    internal void Closed() => this.RaiseClosed();
    internal new bool IsClosed => base.IsClosed;
}
