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
    readonly HubClientProtocol protocol;
    readonly SemaphoreSlim connectLock = new(1, 1);
    readonly SemaphoreSlim mediaLock = new(1, 1);
    Connection? connection;


    protected BleHubClient(BleHubClientServices services, string serviceUuid, string characteristicUuid)
    {
        this.services = services;
        this.ServiceUuid = BleUuid.Normalize(serviceUuid, nameof(serviceUuid));
        this.CharacteristicUuid = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));
        this.logger = services.LoggerFactory?.CreateLogger(this.GetType().FullName!);

        this.protocol = new HubClientProtocol(services.Options, this.Write, this.logger);
        this.protocol.Pushed += message => this.connection?.Pushes.Writer.TryWrite(message);
        this.protocol.DisconnectRequested += info =>
        {
            // hosts older than the reason code only ever sent a Disconnect to remove one client
            var disconnect = info.ToDisconnect(HubDisconnectReason.ServerDisconnect);
            this.logger?.LogInformation("Host ended the session: {Reason}", disconnect);
            _ = this.Teardown(disconnect, true);
        };
    }


    public string ServiceUuid { get; }
    public string CharacteristicUuid { get; }
    public BleHubClientStatus Status { get; private set; } = BleHubClientStatus.Disconnected;
    public BleHubHostInfo? Host => this.connection?.Host;
    public string? HostName => this.connection?.HostName;
    public string? ClientName => this.connection?.ClientName;

    public bool CanTransferFiles =>
        this.Status == BleHubClientStatus.Connected
        && (this.connection?.External is { } external
            ? external.CanTransferFiles
            : this.connection?.Ack?.FileTransferPsm > 0 && this.connection.Host?.Peripheral.IsL2CapAvailable() == true);

    public bool CanOpenMediaChannel =>
        this.Status == BleHubClientStatus.Connected
        && this.connection is { } conn
        && (conn.External is { } external
            ? external.CanOpenMediaChannel
            : conn.Ack is { MediaChannelPsm: > 0, MediaToken: not null } && conn.OpenL2Cap != null);

    /// <summary>
    /// The transport carrying this connection when it isn't BLE, otherwise null
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IBleHubClientTransport? ExternalTransport => this.connection?.External;

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
        await this.connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertDisconnected();
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
            var conn = this.Begin(host);
            this.services.Connections.Acquire(p.Uuid);
            conn.OwnsBleConnection = true;

            await p.ConnectAsync(new ConnectionConfig(false), cancellationToken).ConfigureAwait(false);
            var attMtu = await NegotiateMtu(p, this.logger, cancellationToken).ConfigureAwait(false);

            // fail fast if the host does not serve this hub
            await p.GetCharacteristicAsync(this.ServiceUuid, this.CharacteristicUuid, cancellationToken).ConfigureAwait(false);

            var subscribed = p.WaitForCharacteristicSubscriptionAsync(this.ServiceUuid, this.CharacteristicUuid, cancellationToken);
            conn.Subscriptions.Add(p
                .NotifyCharacteristic(this.ServiceUuid, this.CharacteristicUuid, false)
                .Subscribe(
                    r =>
                    {
                        if (r.Data is { Length: > 0 } data)
                            this.protocol.OnFrame(data);
                    },
                    ex => this.logger?.LogWarning(ex, "Notification subscription failed")
                ));
            await subscribed.ConfigureAwait(false);

            conn.Subscriptions.Add(p
                .WhenStatusChanged()
                .Where(x => x == ConnectionState.Disconnected)
                .Subscribe(__ => { _ = this.Teardown(new HubDisconnect(HubDisconnectReason.ClientTimeout), false); }));

            conn.Write = (frame, ct) => p.WriteCharacteristicAsync(this.ServiceUuid, this.CharacteristicUuid, frame, true, ct, 10000);
            if (p.IsL2CapAvailable())
                conn.OpenL2Cap = (psm, secure, ct) => p.OpenL2CapChannelAsync(psm, secure, ct);

            await this.Complete(conn, attMtu, options, cancellationToken).ConfigureAwait(false);
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
    /// Connects over a caller supplied transport (tests / in-memory). Frames from the host go to <see cref="ReceiveFrame"/>.
    /// </summary>
    internal async Task ConnectTransport(
        Func<byte[], CancellationToken, Task> write,
        int mtu,
        BleHubConnectOptions? options,
        CancellationToken cancellationToken,
        Func<ushort, bool, CancellationToken, Task<L2CapChannel>>? openL2Cap = null
    )
    {
        await this.connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertDisconnected();
            var conn = this.Begin(null);
            conn.Write = write;
            conn.OpenL2Cap = openL2Cap;
            await this.Complete(conn, mtu, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await this.Teardown(new HubDisconnect(HubDisconnectReason.ConnectionFailed, ex.Message), false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            this.connectLock.Release();
        }
    }


    /// <summary>
    /// Connects over a transport other than BLE. <paramref name="createTransport"/> receives the callbacks for pushes and
    /// closes, and returns the transport for this one connection.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public async Task ConnectExternal(
        Func<IBleHubClientTransportEvents, IBleHubClientTransport> createTransport,
        BleHubConnectOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(createTransport);
        await this.connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            this.AssertDisconnected();
            var conn = this.Begin(null);
            conn.External = createTransport(new ExternalEvents(this, conn));

            var ack = await conn.External.Handshake(this.CreateHandshake(options), cancellationToken).ConfigureAwait(false);
            this.Accept(conn, ack, 0, options);
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


    internal void ReceiveFrame(ReadOnlySpan<byte> frame) => this.protocol.OnFrame(frame);

    /// <summary>
    /// Simulates the link dropping (tests)
    /// </summary>
    internal Task LoseConnection(string reason) => this.Teardown(new HubDisconnect(HubDisconnectReason.ClientTimeout, reason), false);


    public async Task Disconnect()
    {
        var disconnect = new HubDisconnect(HubDisconnectReason.ClientDisconnect);

        // over BLE, say goodbye first so the host doesn't take the unsubscribe that follows for a dropped link
        // (another transport tells its host itself when it closes)
        if (this.Status == BleHubClientStatus.Connected && this.connection is { External: null, Write: not null })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await this.protocol.SendDisconnect(disconnect, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "Could not tell the host we are leaving");
            }
        }
        await this.Teardown(disconnect, true).ConfigureAwait(false);
    }


    public async Task Rename(string? name, CancellationToken cancellationToken = default)
    {
        this.AssertConnected();
        var conn = this.connection ?? throw new BleHubDisconnectedException("Not connected to a host");

        if (conn.External is { } external)
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
        else
        {
            await this.protocol.Rename(name, cancellationToken).ConfigureAwait(false);
        }
        conn.ClientName = name;
    }


    public async Task<L2CapTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (this.AssertExternalFiles() is { } external)
        {
            await using var file = File.OpenRead(localFilePath);
            return await external.Upload(file, file.Length, remoteFileName ?? Path.GetFileName(localFilePath), progress, cancellationToken).ConfigureAwait(false);
        }

        var (p, ack) = this.AssertFiles();
        return await p.UploadFile(ack.FileTransferPsm, localFilePath, remoteFileName, ack.FileTransferSecure, ToAction(progress), null, cancellationToken).ConfigureAwait(false);
    }


    public async Task<L2CapTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (this.AssertExternalFiles() is { } external)
            return await external.Upload(source, length, remoteFileName, progress, cancellationToken).ConfigureAwait(false);

        var (p, ack) = this.AssertFiles();
        using var channel = await p.OpenL2CapChannelAsync(ack.FileTransferPsm, ack.FileTransferSecure, cancellationToken).ConfigureAwait(false);
        return await channel.UploadFile(source, remoteFileName, length, ToAction(progress), null, cancellationToken).ConfigureAwait(false);
    }


    public async Task<L2CapTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (this.AssertExternalFiles() is { } external)
        {
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

        var (p, ack) = this.AssertFiles();
        return await p.DownloadFile(ack.FileTransferPsm, remoteFileName, localFilePath, ack.FileTransferSecure, ToAction(progress), null, cancellationToken).ConfigureAwait(false);
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
                if (conn.External is { } external)
                {
                    if (!external.CanOpenMediaChannel)
                        throw new NotSupportedException("The host does not serve media channels");

                    media = await external.OpenMediaChannel(cts.Token).ConfigureAwait(false);
                }
                else
                {
                    media = await this.OpenL2CapMedia(conn, cts.Token).ConfigureAwait(false);
                }
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


    async Task<IBleHubMediaChannel> OpenL2CapMedia(Connection conn, CancellationToken cancellationToken)
    {
        var ack = conn.Ack!;
        if (ack.MediaChannelPsm == 0 || ack.MediaToken == null || ack.MediaMaxPacketSize < 1)
            throw new NotSupportedException("The host does not serve media channels");

        var open = conn.OpenL2Cap ?? throw new NotSupportedException("L2CAP is not available on this device");
        var l2cap = await open(ack.MediaChannelPsm, ack.MediaChannelSecure, cancellationToken).ConfigureAwait(false);
        var channel = FramedMediaChannel.Over(
            l2cap.DataReceived,
            ack.MediaMaxPacketSize,
            (bytes, ct) => l2cap.Write(bytes).ToTask(ct),
            l2cap.Dispose,
            this.logger
        );

        try
        {
            await channel.SendControl(System.Text.Encoding.UTF8.GetBytes(ack.MediaToken), cancellationToken).ConfigureAwait(false);
            byte[] answer;
            try
            {
                answer = await channel.ReadControl(this.services.Options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (BleHubException)
            {
                // the host closes the channel on a token it doesn't know
                throw new BleHubException("The host refused the media channel");
            }
            if (answer is not [MediaFrameCodec.Accepted])
                throw new BleHubException("The host refused the media channel");

            return channel;
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
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
        var items = this.connection?.External is { } external
            ? this.ExternalStream(external, method, arguments.ToArray(), cancellationToken)
            : this.protocol.Stream(method, arguments.ToArray(), cancellationToken);

        await foreach (var item in items.ConfigureAwait(false))
            yield return this.services.Serializer.Deserialize<T>(item.Span);
    }


    Task<ReadOnlyMemory<byte>> InvokeRaw(string method, byte[] arguments, CancellationToken cancellationToken)
        => this.connection?.External is { } external
            ? this.ExternalInvoke(external, method, arguments, cancellationToken)
            : this.protocol.Invoke(method, arguments, cancellationToken);


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


    async Task Complete(Connection conn, int mtu, BleHubConnectOptions? options, CancellationToken cancellationToken)
    {
        this.protocol.Mtu = mtu;
        var ack = await this.protocol.Handshake(this.CreateHandshake(options), cancellationToken).ConfigureAwait(false);
        this.Accept(conn, ack, mtu, options);
    }


    HandshakeInfo CreateHandshake(BleHubConnectOptions? options) => new(
        FrameCodec.ProtocolVersion,
        options?.Name,
        options?.AppVersion,
        options?.Properties
    );


    void Accept(Connection conn, HandshakeAck ack, int mtu, BleHubConnectOptions? options)
    {
        if (!ack.Accepted)
            throw new BleHubException($"Host refused the connection: {ack.Reason}");

        conn.Ack = ack;
        conn.HostName = ack.HostName;
        conn.ClientName = options?.Name;
        this.SetStatus(BleHubClientStatus.Connected, null);
        this.Connected?.Invoke(this, EventArgs.Empty);
        this.logger?.LogInformation("Connected to '{Host}' (MTU {Mtu}, file PSM {Psm})", ack.HostName, mtu, ack.FileTransferPsm);
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

        var wasConnected = this.Status == BleHubClientStatus.Connected;
        this.SetStatus(BleHubClientStatus.Disconnecting, disconnect);
        foreach (var sub in conn.Subscriptions)
            sub.Dispose();

        conn.Pushes.Writer.TryComplete();
        this.protocol.FailAll(new BleHubDisconnectedException("The connection to the host was closed", disconnect));
        conn.CloseReason = disconnect;
        conn.Lifetime.Cancel();
        if (conn.Media is { } media)
        {
            try
            {
                await media.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "Media channel did not close cleanly");
            }
        }

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

        var p = conn.Host?.Peripheral;
        if (p != null && conn.OwnsBleConnection && this.services.Connections.Release(p.Uuid) && cancelConnection)
        {
            try
            {
                await p.DisconnectAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "Disconnect did not complete cleanly");
                p.CancelConnection();
            }
        }

        this.SetStatus(BleHubClientStatus.Disconnected, disconnect);
        if (wasConnected)
            this.Disconnected?.Invoke(this, disconnect);
    }


    Task Write(byte[] frame, CancellationToken cancellationToken)
    {
        var write = this.connection?.Write ?? throw new BleHubDisconnectedException("Not connected to a host");
        return write(frame, cancellationToken);
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


    IBleHubClientTransport? AssertExternalFiles()
    {
        this.AssertConnected();
        if (this.connection?.External is not { } external)
            return null;

        if (!external.CanTransferFiles)
            throw new BleHubFileTransferNotSupportedException("The host does not serve file transfers");

        return external;
    }


    (IPeripheral Peripheral, HandshakeAck Ack) AssertFiles()
    {
        this.AssertConnected();
        var conn = this.connection!;
        if (conn.Ack!.FileTransferPsm == 0)
            throw new BleHubFileTransferNotSupportedException("The host does not serve file transfers");

        var p = conn.Host?.Peripheral;
        if (p == null || !p.IsL2CapAvailable())
            throw new BleHubFileTransferNotSupportedException("L2CAP is not available on this device");

        return (p, conn.Ack);
    }


    void SetStatus(BleHubClientStatus status, HubDisconnect? disconnect)
    {
        if (this.Status == status)
            return;

        this.Status = status;
        this.StatusChanged?.Invoke(this, new BleHubStatusChangedEventArgs(status, disconnect));
    }


    static Action<TransferProgress>? ToAction(IProgress<TransferProgress>? progress)
        => progress == null ? null : progress.Report;


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
    }


    sealed class Connection(BleHubHostInfo? host)
    {
        public BleHubHostInfo? Host { get; } = host;
        public HandshakeAck? Ack { get; set; }
        public string? HostName { get; set; }
        public string? ClientName { get; set; }
        public bool OwnsBleConnection { get; set; }
        public Func<byte[], CancellationToken, Task>? Write { get; set; }
        public IBleHubClientTransport? External { get; set; }
        public Func<ushort, bool, CancellationToken, Task<L2CapChannel>>? OpenL2Cap { get; set; }

        IBleHubMediaChannel? media;
        public IBleHubMediaChannel? Media
        {
            get => Volatile.Read(ref this.media);
            set => Interlocked.Exchange(ref this.media, value);
        }
        public CancellationTokenSource Lifetime { get; } = new();
        public HubDisconnect? CloseReason { get; set; }
        public List<IDisposable> Subscriptions { get; } = new();
        public Channel<BleHubMessage> Pushes { get; } = Channel.CreateUnbounded<BleHubMessage>(new UnboundedChannelOptions { SingleReader = true });
    }
}
