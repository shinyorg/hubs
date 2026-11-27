using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// Base class for the generated hub proxies - owns the connection over whichever transport carries it: handshake, call
/// timeouts, push dispatch, files and media
/// </summary>
public abstract class HubProxy : IHubConnection, IDisposable
{
    readonly HubClientServices services;
    readonly ILogger? logger;
    readonly SemaphoreSlim connectLock = new(1, 1);
    readonly SemaphoreSlim mediaLock = new(1, 1);
    // guards the connection / status pair, so a teardown and a handshake reply can't interleave
    readonly object stateGate = new();
    Connection? connection;
    // the last connection's push pump - the next one starts after it, so pushes never run concurrently or out of order
    Task pushPump = Task.CompletedTask;


    protected HubProxy(HubClientServices services, string hubName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hubName);
        this.services = services;
        this.HubName = hubName;
        this.logger = services.LoggerFactory?.CreateLogger(this.GetType().FullName!);
    }


    /// <summary>
    /// The hub's name - how this client asks for it on every transport
    /// </summary>
    public string HubName { get; }
    internal HubClientServices Services => this.services;
    internal ILogger? Logger => this.logger;
    public HubClientStatus Status { get; private set; } = HubClientStatus.Disconnected;
    public string? HostName => this.connection?.HostName;
    public string? ClientName => this.connection?.ClientName;

    public bool CanTransferFiles =>
        this.Status == HubClientStatus.Connected && this.connection?.External?.CanTransferFiles == true;

    public bool CanOpenMediaChannel =>
        this.Status == HubClientStatus.Connected && this.connection?.External?.CanOpenMediaChannel == true;

    /// <summary>
    /// The link carrying this connection, null while disconnected
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IHubConnectionTransport? ConnectionTransport => this.connection?.External;

    public event EventHandler<HubStatusChangedEventArgs>? StatusChanged;
    public event EventHandler? Connected;
    public event EventHandler<HubDisconnect>? Disconnected;
    public event EventHandler<string?>? HostRenamed;


    /// <summary>
    /// Connects over a transport's link. <paramref name="createTransport"/> receives the callbacks for pushes and
    /// closes, and returns the transport for this one connection.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task ConnectExternal(
        Func<IHubConnectionTransportEvents, IHubConnectionTransport> createTransport,
        HubHandshakeOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(createTransport);
        return this.ConnectTransportCore(createTransport, options, cancellationToken);
    }


    async Task ConnectTransportCore(
        Func<IHubConnectionTransportEvents, IHubConnectionTransport> createTransport,
        HubHandshakeOptions? options,
        CancellationToken cancellationToken
    )
    {
        await this.connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // outside the try below - a connect refused for being already connected must not tear down the live connection
            this.AssertDisconnected();
            var conn = this.Begin();
            try
            {
                conn.External = createTransport(new ExternalEvents(this, conn));

                // the transport says whether it can resume - it knows whether it can reopen its link
                var ack = await conn.External.Handshake(this.CreateHandshake(options, options?.AutomaticReconnect ?? true), cancellationToken).ConfigureAwait(false);
                this.Accept(conn, ack, options);
            }
            catch (Exception ex)
            {
                // only the connection this call began - it may already be gone (closed or disconnected mid-handshake), and
                // then its teardown finishes before this call returns, so the client is Disconnected when it throws
                await this.Teardown(new HubDisconnect(HubDisconnectReason.ConnectionFailed, ex.Message), true, conn).ConfigureAwait(false);
                await conn.TornDown.Task.ConfigureAwait(false);

                // the app disconnected while this was connecting - say so, rather than as a failure worth retrying elsewhere
                if (conn.CloseReason is { Reason: HubDisconnectReason.ClientDisconnect } && ex is not HubDisconnectedException)
                    throw new HubDisconnectedException("The client disconnected while connecting", conn.CloseReason);

                throw;
            }
        }
        finally
        {
            this.connectLock.Release();
        }
    }


    async Task CloseMedia(Connection conn)
    {
        if (conn.TakeMedia() is not { } media)
            return;

        try
        {
            await media.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Media channel did not close cleanly");
        }
    }


    // the transport tells its host it is leaving when it closes
    public Task Disconnect() => this.Teardown(new HubDisconnect(HubDisconnectReason.ClientDisconnect), true);


    public async Task Rename(string? name, CancellationToken cancellationToken = default)
    {
        this.AssertConnected();
        var conn = this.connection ?? throw new HubDisconnectedException("Not connected to a host");
        var external = conn.External ?? throw new HubDisconnectedException("Not connected to a host");
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conn.Lifetime.Token);
            cts.CancelAfter(this.services.Options.RequestTimeout);
            try
            {
                await external.Rename(name, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw conn.Lifetime.IsCancellationRequested
                    ? new HubDisconnectedException("The connection to the host was closed", conn.CloseReason)
                    : new TimeoutException($"No reply from the host within {this.services.Options.RequestTimeout}");
            }
        }
        conn.ClientName = name;
    }


    public async Task<HubFileTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var external = this.AssertFiles();
        await using var file = File.OpenRead(localFilePath);
        return await external.Upload(file, file.Length, remoteFileName ?? Path.GetFileName(localFilePath), progress, cancellationToken).ConfigureAwait(false);
    }


    public Task<HubFileTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.AssertFiles().Upload(source, length, remoteFileName, progress, cancellationToken);


    public async Task<HubFileTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var external = this.AssertFiles();
        var temp = localFilePath + ".part";
        try
        {
            HubFileTransferResult result;
            await using (var file = File.Create(temp))
                result = await external.Download(remoteFileName, file, progress, cancellationToken).ConfigureAwait(false);

            File.Move(temp, localFilePath, true);
            return result;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }


    public async Task<IHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken = default)
    {
        this.AssertConnected();
        var conn = this.connection ?? throw new HubDisconnectedException("Not connected to a host");

        await this.mediaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (conn.Media is { IsOpen: true } open)
                return open;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conn.Lifetime.Token);
            IHubMediaChannel media;
            try
            {
                var external = conn.External ?? throw new HubDisconnectedException("Not connected to a host");
                if (!external.CanOpenMediaChannel)
                    throw new NotSupportedException("The host does not serve media channels");

                media = await external.OpenMediaChannel(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HubDisconnectedException("The connection to the host was closed", conn.CloseReason);
            }

            conn.Media = media;
            // Teardown cancels the lifetime and then closes conn.Media - one of us sees the other
            if (conn.Lifetime.IsCancellationRequested)
            {
                await media.DisposeAsync().ConfigureAwait(false);
                throw new HubDisconnectedException("The connection to the host was closed", conn.CloseReason);
            }
            this.logger?.LogInformation("Media channel open (max packet {Size} bytes)", media.MaxPacketSize);
            return media;
        }
        finally
        {
            this.mediaLock.Release();
        }
    }


    public void Dispose() => _ = this.Teardown(new HubDisconnect(HubDisconnectReason.ClientDisconnect, "Disposed"), true);


    // ---- used by generated proxies ----

    [EditorBrowsable(EditorBrowsableState.Never)]
    protected HubArgumentWriter CreateArguments() => new(this.services.Serializer);


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async Task<T> InvokeCore<T>(string method, HubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        var result = await this.InvokeRaw(method, arguments.ToArray(), cancellationToken).ConfigureAwait(false);
        return this.services.Serializer.Deserialize<T>(result.Span);
    }


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async Task InvokeVoidCore(string method, HubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        await this.InvokeRaw(method, arguments.ToArray(), cancellationToken).ConfigureAwait(false);
    }


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async IAsyncEnumerable<T> StreamCore<T>(string method, HubArgumentWriter arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        this.AssertConnected();
        var items = this.ExternalStream(this.Transport, method, arguments.ToArray(), cancellationToken);
        await foreach (var item in items.ConfigureAwait(false))
            yield return this.services.Serializer.Deserialize<T>(item.Span);
    }


    /// <summary>
    /// A call whose result is left as the host's serializer wrote it - for a proxy dispatched by name, which never sees the
    /// values' types
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected Task<ReadOnlyMemory<byte>> InvokeSerializedCore(string method, HubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        return this.InvokeRaw(method, arguments.ToArray(), cancellationToken);
    }


    /// <summary>
    /// A stream whose items are left as the host's serializer wrote them
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSerializedCore(string method, HubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        return this.ExternalStream(this.Transport, method, arguments.ToArray(), cancellationToken);
    }


    Task<ReadOnlyMemory<byte>> InvokeRaw(string method, byte[] arguments, CancellationToken cancellationToken)
        => this.ExternalInvoke(this.Transport, method, arguments, cancellationToken);


    IHubConnectionTransport Transport => this.connection?.External ?? throw new HubDisconnectedException("Not connected to a host");


    async Task<ReadOnlyMemory<byte>> ExternalInvoke(IHubConnectionTransport external, string method, byte[] arguments, CancellationToken cancellationToken)
    {
        this.AssertPayload(arguments);
        var conn = this.connection ?? throw new HubDisconnectedException("Not connected to a host");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conn.Lifetime.Token);
        cts.CancelAfter(this.services.Options.RequestTimeout);
        try
        {
            return await external.Invoke(method, arguments, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw conn.Lifetime.IsCancellationRequested
                ? new HubDisconnectedException("The connection to the host was closed", conn.CloseReason)
                : new TimeoutException($"No reply from the host within {this.services.Options.RequestTimeout}");
        }
    }


    async IAsyncEnumerable<ReadOnlyMemory<byte>> ExternalStream(IHubConnectionTransport external, string method, byte[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        this.AssertPayload(arguments);
        var conn = this.connection ?? throw new HubDisconnectedException("Not connected to a host");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conn.Lifetime.Token);
        await using var e = external.Stream(method, arguments, cts.Token).GetAsyncEnumerator(cts.Token);

        while (true)
        {
            bool next;
            try
            {
                next = await e.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && conn.Lifetime.IsCancellationRequested)
            {
                throw new HubDisconnectedException("The connection to the host was closed", conn.CloseReason);
            }
            if (!next)
                yield break;

            yield return e.Current;
        }
    }


    void AssertPayload(byte[] arguments)
    {
        if (arguments.Length > this.services.Options.MaxPayloadSize)
            throw new HubException($"Payload is {arguments.Length} bytes which exceeds MaxPayloadSize ({this.services.Options.MaxPayloadSize}) - send large content as a file");
    }


    /// <summary>
    /// Raise the matching hub event - implemented by the generated proxy
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected abstract void OnPush(string name, HubArgumentReader args);


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected void OnUnknownPush(string name)
        => this.logger?.LogWarning("Host pushed '{Event}' which this contract does not declare - are both sides on the same contract?", name);


    // ---- connection lifecycle ----

    Connection Begin()
    {
        var conn = new Connection();
        lock (this.stateGate)
        {
            this.connection = conn;
            this.SetStatus(HubClientStatus.Connecting, null);
        }
        var previous = this.pushPump;
        this.pushPump = Task.Run(() => this.PumpPushes(previous, conn));
        return conn;
    }


    HandshakeInfo CreateHandshake(HubHandshakeOptions? options, bool resumable) => new(
        FrameCodec.ProtocolVersion,
        options?.Name,
        options?.AppVersion,
        options?.Properties,
        resumable,
        this.services.Serializer.Name
    );


    void Accept(Connection conn, HandshakeAck ack, HubHandshakeOptions? options)
    {
        if (!ack.Accepted)
            throw new HubException($"Host refused the connection: {ack.Reason}");

        lock (this.stateGate)
        {
            // the link closed (or the app disconnected) while the reply was on its way - the connection is already torn
            // down, so this connect failed rather than leaving a Connected client with no connection
            if (!ReferenceEquals(this.connection, conn) || conn.Lifetime.IsCancellationRequested)
                throw new HubDisconnectedException("The connection to the host closed during the handshake", conn.CloseReason);

            conn.Ack = ack;
            conn.HostName = ack.HostName;
            conn.ClientName = options?.Name;
            // raised under the gate, so a teardown that follows is seen after Connected, never before it
            this.SetStatus(HubClientStatus.Connected, null);
            this.Connected?.Invoke(this, EventArgs.Empty);
        }
        this.logger?.LogInformation("Connected to '{Host}'", ack.HostName);
    }


    /// <summary>
    /// Pushes (and host renames) are raised one at a time, in the order the host sent them. A connection's pump waits for
    /// the previous connection's to finish raising what that connection received, so a quick reconnect never runs two
    /// connections' handlers at once.
    /// </summary>
    async Task PumpPushes(Task previous, Connection conn)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Previous push pump failed");
        }

        await foreach (var message in conn.Pushes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (message.Kind == FrameKind.HostRenamed)
                {
                    conn.HostName = ProtocolSerializer.ReadRename(message.Payload).Name;
                    this.logger?.LogInformation("Host renamed to '{Host}'", conn.HostName);
                    this.HostRenamed?.Invoke(this, conn.HostName);
                }
                else
                {
                    this.OnPush(message.Name ?? "", new HubArgumentReader(this.services.Serializer, message.Payload));
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Handler for host event '{Event}' failed", message.Name ?? message.Kind.ToString());
            }
        }
    }


    /// <param name="expected">Tear down only this connection - null for whichever is current</param>
    async Task Teardown(HubDisconnect disconnect, bool cancelConnection, Connection? expected = null)
    {
        // connection loss, host disconnect and app disconnect can race - only the first one tears down
        Connection? conn;
        bool wasConnected;
        lock (this.stateGate)
        {
            conn = this.connection;
            if (conn == null || (expected != null && !ReferenceEquals(conn, expected)))
                return;

            // Accept checks the connection under the gate - once it is gone, a handshake reply can't connect
            this.connection = null;
            conn.CloseReason = disconnect;
            wasConnected = this.Status is HubClientStatus.Connected or HubClientStatus.Reconnecting;
            this.SetStatus(HubClientStatus.Disconnecting, disconnect);
        }

        try
        {
            // pushes already received are still raised (in order); nothing more is queued
            conn.Pushes.Writer.TryComplete();
            conn.Lifetime.Cancel();
            await this.CloseMedia(conn).ConfigureAwait(false);

            if (conn.External != null && cancelConnection)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await conn.External.Close(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger?.LogDebug(ex, "Transport did not close cleanly");
                }
            }

            this.SetStatus(HubClientStatus.Disconnected, disconnect);
        }
        finally
        {
            conn.TornDown.TrySetResult();
        }
        if (wasConnected)
            this.Disconnected?.Invoke(this, disconnect);
    }


    void AssertDisconnected()
    {
        if (this.Status != HubClientStatus.Disconnected)
            throw new InvalidOperationException($"Client is already {this.Status}");
    }


    void AssertConnected()
    {
        if (this.Status != HubClientStatus.Connected)
            throw new HubDisconnectedException($"Client is {this.Status}, not Connected");
    }


    IHubConnectionTransport AssertFiles()
    {
        this.AssertConnected();
        var external = this.Transport;
        if (!external.CanTransferFiles)
            throw new HubFileTransferNotSupportedException("The host does not serve file transfers");

        return external;
    }


    void SetStatus(HubClientStatus status, HubDisconnect? disconnect)
    {
        if (this.Status == status)
            return;

        this.Status = status;
        this.StatusChanged?.Invoke(this, new HubStatusChangedEventArgs(status, disconnect));
    }


    sealed class ExternalEvents(HubProxy client, Connection conn) : IHubConnectionTransportEvents
    {
        public void Pushed(string eventName, ReadOnlyMemory<byte> arguments)
            => conn.Pushes.Writer.TryWrite(new HubMessage(FrameKind.Push, 0, eventName, arguments));

        public void HostRenamed(string? hostName)
            => conn.Pushes.Writer.TryWrite(new HubMessage(FrameKind.HostRenamed, 0, null, ProtocolSerializer.Serialize(new RenameInfo(hostName))));

        public void Closed(HubDisconnect disconnect)
        {
            // only the connection this transport was created for - a late close from an old transport must not end a new one
            _ = client.Teardown(disconnect, false, conn);
        }

        public void Reconnecting(HubDisconnect drop)
        {
            lock (client.stateGate)
            {
                if (!ReferenceEquals(client.connection, conn) || client.Status != HubClientStatus.Connected)
                    return;

                client.SetStatus(HubClientStatus.Reconnecting, drop);
            }
            client.logger?.LogInformation("Link to '{Host}' dropped ({Reason}) - resuming", conn.HostName, drop);
            _ = client.CloseMedia(conn);
        }

        public void Reconnected()
        {
            lock (client.stateGate)
            {
                if (!ReferenceEquals(client.connection, conn) || client.Status != HubClientStatus.Reconnecting)
                    return;

                client.SetStatus(HubClientStatus.Connected, null);
            }
            client.logger?.LogInformation("Resumed on '{Host}'", conn.HostName);
        }
    }


    sealed class Connection
    {
        public HandshakeAck? Ack { get; set; }
        public string? HostName { get; set; }
        public string? ClientName { get; set; }
        public IHubConnectionTransport? External { get; set; }

        IHubMediaChannel? media;
        public IHubMediaChannel? Media
        {
            get => Volatile.Read(ref this.media);
            set => Interlocked.Exchange(ref this.media, value);
        }
        public IHubMediaChannel? TakeMedia() => Interlocked.Exchange(ref this.media, null);
        public CancellationTokenSource Lifetime { get; } = new();
        public HubDisconnect? CloseReason { get; set; }
        public TaskCompletionSource TornDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel<HubMessage> Pushes { get; } = Channel.CreateUnbounded<HubMessage>(new UnboundedChannelOptions { SingleReader = true });
    }
}
