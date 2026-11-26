using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Base class for the generated hub proxies - owns the BLE connection, handshake, call correlation, push dispatch and files
/// </summary>
public abstract class BleHubClient : IBleHubConnection, IDisposable
{
    readonly BleHubClientServices services;
    readonly ILogger? logger;
    readonly SemaphoreSlim connectLock = new(1, 1);
    readonly SemaphoreSlim mediaLock = new(1, 1);
    Connection? connection;


    protected BleHubClient(BleHubClientServices services, string serviceUuid, string characteristicUuid)
    {
        this.services = services;
        this.ServiceUuid = BleUuid.Normalize(serviceUuid, nameof(serviceUuid));
        this.CharacteristicUuid = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));
        this.logger = services.LoggerFactory?.CreateLogger(this.GetType().FullName!);
    }


    public string ServiceUuid { get; }
    public string CharacteristicUuid { get; }
    public BleHubClientStatus Status { get; private set; } = BleHubClientStatus.Disconnected;
    public BleHubHostInfo? Host => this.connection?.Host;
    public string? HostName => this.connection?.HostName;
    public string? ClientName => this.connection?.ClientName;

    public bool CanTransferFiles =>
        this.Status == BleHubClientStatus.Connected && this.connection?.External?.CanTransferFiles == true;

    public bool CanOpenMediaChannel =>
        this.Status == BleHubClientStatus.Connected && this.connection?.External?.CanOpenMediaChannel == true;

    /// <summary>
    /// The transport carrying this connection when it isn't BLE, otherwise null
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IBleHubClientTransport? ExternalTransport => this.connection?.External is FrameClientTransport ? null : this.connection?.External;

    public event EventHandler<BleHubStatusChangedEventArgs>? StatusChanged;
    public event EventHandler? Connected;
    public event EventHandler<HubDisconnect>? Disconnected;
    public event EventHandler<string?>? HostRenamed;


    public IObservable<BleHubHostInfo> Discover()
    {
        var ble = this.services.BleManager ?? throw new InvalidOperationException("IBleManager is not registered - call AddBluetoothLE()");
        return Observable
            .FromAsync(() => ble.RequestAccessAsync())
            .SelectMany(access => access == AccessState.Available
                ? ble.Scan(new ScanConfig(this.ServiceUuid))
                : Observable.Throw<ScanResult>(new BleHubException($"Bluetooth is not available ({access})"))
            )
            // a scan on the device that is hosting can report the device's own advertisement - checked per result, since
            // this app's host can start or stop advertising mid-scan
            .Where(x => this.services.LocalAdvertisement?.IsLocal(this.ServiceUuid, x.AdvertisementData?.LocalName) != true)
            .Select(x => new BleHubHostInfo(x.Peripheral, x.AdvertisementData?.LocalName ?? x.Peripheral.Name, x.Rssi));
    }


    public async Task Connect(BleHubHostInfo host, BleHubConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        var ble = this.services.BleManager ?? throw new InvalidOperationException("IBleManager is not registered - call AddBluetoothLE()");
        if (ble.IsScanning)
            ble.StopScan();

        // A scan started since this host was discovered drops it from the manager's cache (Shiny does that on
        // Apple), and the connection's events then go to the manager's new instance - connecting the old one waits
        // forever. Connect the manager's current instance for the same device.
        if (ble.GetKnownPeripheral(host.Peripheral.Uuid) is { } current && !ReferenceEquals(current, host.Peripheral))
        {
            this.logger?.LogDebug("Using the current instance of {Peripheral} - the discovered one went stale", host.Peripheral.Uuid);
            host = host with { Peripheral = current };
        }

        var p = host.Peripheral;
        await this.ConnectTransportCore(host, events =>
        {
            this.services.Connections.Acquire(p.Uuid);
            var link = new BleFrameLink(p, this.ServiceUuid, this.CharacteristicUuid, this.services.Connections, this.logger);
            return new FrameClientTransport(this.services.Options, link, events, this.logger);
        }, options, cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// Asks for the largest ATT MTU (<see cref="FrameCodec.MaxAttMtu"/>) on a new BLE connection, so hub messages go in as
    /// few frames as possible. Android negotiates what is asked for; Apple platforms negotiate on their own and can't be
    /// asked, so the current MTU stands. Returns the ATT MTU - Shiny reports the payload size (ATT MTU minus the header).
    /// </summary>
    internal static async Task<int> NegotiateMtu(IPeripheral peripheral, ILogger? logger, CancellationToken cancellationToken)
    {
        var payload = peripheral.Mtu;
        try
        {
            payload = await peripheral.TryRequestMtuAsync(FrameCodec.MaxAttMtu, cancelToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogDebug(ex, "MTU request failed - continuing with the current MTU");
            payload = peripheral.Mtu;
        }

        var attMtu = payload + BleConstants.AttHeaderSize;
        logger?.LogInformation("ATT MTU {Mtu} (asked for {Requested}, {Payload} byte frames)", attMtu, FrameCodec.MaxAttMtu, FrameCodec.GetFrameSize(attMtu));
        return attMtu;
    }


    /// <summary>
    /// Connects over a caller supplied frame link (tests / in-memory). Frames from the host go to <see cref="ReceiveFrame"/>.
    /// </summary>
    internal Task ConnectTransport(
        Func<byte[], CancellationToken, Task> write,
        int mtu,
        BleHubConnectOptions? options,
        CancellationToken cancellationToken,
        Func<ushort, bool, CancellationToken, Task<L2CapChannel>>? openL2Cap = null,
        Func<CancellationToken, Task<int>>? relink = null
    ) => this.ConnectTransportCore(
        null,
        events => new FrameClientTransport(this.services.Options, new DelegateFrameLink(write, mtu, openL2Cap, relink), events, this.logger),
        options,
        cancellationToken
    );


    /// <summary>
    /// Connects over a transport other than BLE. <paramref name="createTransport"/> receives the callbacks for pushes and
    /// closes, and returns the transport for this one connection.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Task ConnectExternal(
        Func<IBleHubClientTransportEvents, IBleHubClientTransport> createTransport,
        BleHubConnectOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(createTransport);
        return this.ConnectTransportCore(null, createTransport, options, cancellationToken);
    }


    async Task ConnectTransportCore(
        BleHubHostInfo? host,
        Func<IBleHubClientTransportEvents, IBleHubClientTransport> createTransport,
        BleHubConnectOptions? options,
        CancellationToken cancellationToken
    )
    {
        await this.connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertDisconnected();
            var conn = this.Begin(host);
            conn.External = createTransport(new ExternalEvents(this, conn));

            // the transport says whether it can resume - it knows whether it can reopen its link
            var ack = await conn.External.Handshake(this.CreateHandshake(options, options?.AutomaticReconnect ?? true), cancellationToken).ConfigureAwait(false);
            this.Accept(conn, ack, options);
        }
        catch (Exception ex)
        {
            await this.Teardown(new HubDisconnect(HubDisconnectReason.ConnectionFailed, ex.Message), true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            this.connectLock.Release();
        }
    }


    FrameClientTransport? FrameTransport => this.connection?.External as FrameClientTransport;

    internal void ReceiveFrame(ReadOnlySpan<byte> frame) => this.FrameTransport?.OnFrame(frame);

    /// <summary>
    /// Simulates the link dropping with no resume (tests)
    /// </summary>
    internal Task LoseConnection(string reason)
        => this.FrameTransport?.Lose(new HubDisconnect(HubDisconnectReason.ClientTimeout, reason)) ?? Task.CompletedTask;

    /// <summary>
    /// Simulates the link dropping the way the radio reports it - resumed when it can be (tests)
    /// </summary>
    internal void DropLink(string reason) => this.FrameTransport?.OnLinkLost(new HubDisconnect(HubDisconnectReason.ClientTimeout, reason));


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
        var conn = this.connection ?? throw new BleHubDisconnectedException("Not connected to a host");
        var external = conn.External ?? throw new BleHubDisconnectedException("Not connected to a host");
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
                    ? new BleHubDisconnectedException("The connection to the host was closed", conn.CloseReason)
                    : new TimeoutException($"No reply from the host within {this.services.Options.RequestTimeout}");
            }
        }
        conn.ClientName = name;
    }


    public async Task<L2CapTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var external = this.AssertFiles();
        await using var file = File.OpenRead(localFilePath);
        return await external.Upload(file, file.Length, remoteFileName ?? Path.GetFileName(localFilePath), progress, cancellationToken).ConfigureAwait(false);
    }


    public Task<L2CapTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.AssertFiles().Upload(source, length, remoteFileName, progress, cancellationToken);


    public async Task<L2CapTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var external = this.AssertFiles();
        var temp = localFilePath + ".part";
        try
        {
            L2CapTransferResult result;
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


    public async Task<IBleHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken = default)
    {
        this.AssertConnected();
        var conn = this.connection ?? throw new BleHubDisconnectedException("Not connected to a host");

        await this.mediaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (conn.Media is { IsOpen: true } open)
                return open;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conn.Lifetime.Token);
            IBleHubMediaChannel media;
            try
            {
                var external = conn.External ?? throw new BleHubDisconnectedException("Not connected to a host");
                if (!external.CanOpenMediaChannel)
                    throw new NotSupportedException("The host does not serve media channels");

                media = await external.OpenMediaChannel(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BleHubDisconnectedException("The connection to the host was closed", conn.CloseReason);
            }

            conn.Media = media;
            // Teardown cancels the lifetime and then closes conn.Media - one of us sees the other
            if (conn.Lifetime.IsCancellationRequested)
            {
                await media.DisposeAsync().ConfigureAwait(false);
                throw new BleHubDisconnectedException("The connection to the host was closed", conn.CloseReason);
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
    protected BleHubArgumentWriter CreateArguments() => new(this.services.Serializer);


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async Task<T> InvokeCore<T>(string method, BleHubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        var result = await this.InvokeRaw(method, arguments.ToArray(), cancellationToken).ConfigureAwait(false);
        return this.services.Serializer.Deserialize<T>(result.Span);
    }


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async Task InvokeVoidCore(string method, BleHubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        await this.InvokeRaw(method, arguments.ToArray(), cancellationToken).ConfigureAwait(false);
    }


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected async IAsyncEnumerable<T> StreamCore<T>(string method, BleHubArgumentWriter arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
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
    protected Task<ReadOnlyMemory<byte>> InvokeSerializedCore(string method, BleHubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        return this.InvokeRaw(method, arguments.ToArray(), cancellationToken);
    }


    /// <summary>
    /// A stream whose items are left as the host's serializer wrote them
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSerializedCore(string method, BleHubArgumentWriter arguments, CancellationToken cancellationToken)
    {
        this.AssertConnected();
        return this.ExternalStream(this.Transport, method, arguments.ToArray(), cancellationToken);
    }


    Task<ReadOnlyMemory<byte>> InvokeRaw(string method, byte[] arguments, CancellationToken cancellationToken)
        => this.ExternalInvoke(this.Transport, method, arguments, cancellationToken);


    IBleHubClientTransport Transport => this.connection?.External ?? throw new BleHubDisconnectedException("Not connected to a host");


    async Task<ReadOnlyMemory<byte>> ExternalInvoke(IBleHubClientTransport external, string method, byte[] arguments, CancellationToken cancellationToken)
    {
        this.AssertPayload(arguments);
        var conn = this.connection ?? throw new BleHubDisconnectedException("Not connected to a host");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conn.Lifetime.Token);
        cts.CancelAfter(this.services.Options.RequestTimeout);
        try
        {
            return await external.Invoke(method, arguments, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw conn.Lifetime.IsCancellationRequested
                ? new BleHubDisconnectedException("The connection to the host was closed", conn.CloseReason)
                : new TimeoutException($"No reply from the host within {this.services.Options.RequestTimeout}");
        }
    }


    async IAsyncEnumerable<ReadOnlyMemory<byte>> ExternalStream(IBleHubClientTransport external, string method, byte[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        this.AssertPayload(arguments);
        var conn = this.connection ?? throw new BleHubDisconnectedException("Not connected to a host");
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
                throw new BleHubDisconnectedException("The connection to the host was closed", conn.CloseReason);
            }
            if (!next)
                yield break;

            yield return e.Current;
        }
    }


    void AssertPayload(byte[] arguments)
    {
        if (arguments.Length > this.services.Options.MaxPayloadSize)
            throw new BleHubException($"Payload is {arguments.Length} bytes which exceeds MaxPayloadSize ({this.services.Options.MaxPayloadSize}) - send large content as a file");
    }


    /// <summary>
    /// Raise the matching hub event - implemented by the generated proxy
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected abstract void OnPush(string name, BleHubArgumentReader args);


    [EditorBrowsable(EditorBrowsableState.Never)]
    protected void OnUnknownPush(string name)
        => this.logger?.LogWarning("Host pushed '{Event}' which this contract does not declare - are both sides on the same contract?", name);


    // ---- connection lifecycle ----

    Connection Begin(BleHubHostInfo? host)
    {
        var conn = new Connection(host);
        this.connection = conn;
        this.SetStatus(BleHubClientStatus.Connecting, null);
        _ = Task.Run(() => this.PumpPushes(conn));
        return conn;
    }


    HandshakeInfo CreateHandshake(BleHubConnectOptions? options, bool resumable) => new(
        FrameCodec.ProtocolVersion,
        options?.Name,
        options?.AppVersion,
        options?.Properties,
        resumable
    );


    void Accept(Connection conn, HandshakeAck ack, BleHubConnectOptions? options)
    {
        if (!ack.Accepted)
            throw new BleHubException($"Host refused the connection: {ack.Reason}");

        conn.Ack = ack;
        conn.HostName = ack.HostName;
        conn.ClientName = options?.Name;
        this.SetStatus(BleHubClientStatus.Connected, null);
        this.Connected?.Invoke(this, EventArgs.Empty);
        this.logger?.LogInformation("Connected to '{Host}'", ack.HostName);
    }


    /// <summary>
    /// Pushes (and host renames) are raised one at a time, in the order the host sent them
    /// </summary>
    async Task PumpPushes(Connection conn)
    {
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
                    this.OnPush(message.Name ?? "", new BleHubArgumentReader(this.services.Serializer, message.Payload));
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, "Handler for host event '{Event}' failed", message.Name ?? message.Kind.ToString());
            }
        }
    }


    async Task Teardown(HubDisconnect disconnect, bool cancelConnection)
    {
        // connection loss, host disconnect and app disconnect can race - only the first one tears down
        var conn = Interlocked.Exchange(ref this.connection, null);
        if (conn == null)
            return;

        var wasConnected = this.Status is BleHubClientStatus.Connected or BleHubClientStatus.Reconnecting;
        this.SetStatus(BleHubClientStatus.Disconnecting, disconnect);

        conn.Pushes.Writer.TryComplete();
        conn.CloseReason = disconnect;
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

        this.SetStatus(BleHubClientStatus.Disconnected, disconnect);
        if (wasConnected)
            this.Disconnected?.Invoke(this, disconnect);
    }


    void AssertDisconnected()
    {
        if (this.Status != BleHubClientStatus.Disconnected)
            throw new InvalidOperationException($"Client is already {this.Status}");
    }


    void AssertConnected()
    {
        if (this.Status != BleHubClientStatus.Connected)
            throw new BleHubDisconnectedException($"Client is {this.Status}, not Connected");
    }


    IBleHubClientTransport AssertFiles()
    {
        this.AssertConnected();
        var external = this.Transport;
        if (!external.CanTransferFiles)
            throw new BleHubFileTransferNotSupportedException("The host does not serve file transfers");

        return external;
    }


    void SetStatus(BleHubClientStatus status, HubDisconnect? disconnect)
    {
        if (this.Status == status)
            return;

        this.Status = status;
        this.StatusChanged?.Invoke(this, new BleHubStatusChangedEventArgs(status, disconnect));
    }


    sealed class ExternalEvents(BleHubClient client, Connection conn) : IBleHubClientTransportEvents
    {
        public void Pushed(string eventName, ReadOnlyMemory<byte> arguments)
            => conn.Pushes.Writer.TryWrite(new BleHubMessage(FrameKind.Push, 0, eventName, arguments));

        public void HostRenamed(string? hostName)
            => conn.Pushes.Writer.TryWrite(new BleHubMessage(FrameKind.HostRenamed, 0, null, ProtocolSerializer.Serialize(new RenameInfo(hostName))));

        public void Closed(HubDisconnect disconnect)
        {
            // only the connection this transport was created for - a late close from an old transport must not end a new one
            if (ReferenceEquals(client.connection, conn))
                _ = client.Teardown(disconnect, false);
        }

        public void Reconnecting(HubDisconnect drop)
        {
            if (!ReferenceEquals(client.connection, conn) || client.Status != BleHubClientStatus.Connected)
                return;

            client.logger?.LogInformation("Link to '{Host}' dropped ({Reason}) - resuming", conn.HostName, drop);
            client.SetStatus(BleHubClientStatus.Reconnecting, drop);
            _ = client.CloseMedia(conn);
        }

        public void Reconnected()
        {
            if (!ReferenceEquals(client.connection, conn) || client.Status != BleHubClientStatus.Reconnecting)
                return;

            client.SetStatus(BleHubClientStatus.Connected, null);
            client.logger?.LogInformation("Resumed on '{Host}'", conn.HostName);
        }
    }


    sealed class Connection(BleHubHostInfo? host)
    {
        public BleHubHostInfo? Host { get; } = host;
        public HandshakeAck? Ack { get; set; }
        public string? HostName { get; set; }
        public string? ClientName { get; set; }
        public IBleHubClientTransport? External { get; set; }

        IBleHubMediaChannel? media;
        public IBleHubMediaChannel? Media
        {
            get => Volatile.Read(ref this.media);
            set => Interlocked.Exchange(ref this.media, value);
        }
        public IBleHubMediaChannel? TakeMedia() => Interlocked.Exchange(ref this.media, null);
        public CancellationTokenSource Lifetime { get; } = new();
        public HubDisconnect? CloseReason { get; set; }

        public Channel<BleHubMessage> Pushes { get; } = Channel.CreateUnbounded<BleHubMessage>(new UnboundedChannelOptions { SingleReader = true });
    }
}
