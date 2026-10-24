using Microsoft.Extensions.DependencyInjection;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs.Protocol;
using HostedPeripheral = Shiny.BluetoothLE.Hosting.IPeripheral;

namespace Shiny.BluetoothLE.Hubs;

public interface IBleHubHost
{
    /// <summary>
    /// True while at least one hub is running
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// The L2CAP PSM serving file transfers, 0 when not available
    /// </summary>
    ushort FileTransferPsm { get; }

    event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;
    event EventHandler<BleHubFileProgressEventArgs>? FileTransferProgress;

    /// <summary>
    /// Starts every registered hub - requests BLE access, adds the GATT services, opens the file server (if enabled) and
    /// starts advertising. Start a single hub with IHubContext&lt;THub&gt;.Start().
    /// </summary>
    Task Start(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops every hub - tells their clients to disconnect, then stops advertising and removes the services
    /// </summary>
    Task Stop(string? reason = null);

    /// <summary>
    /// Changes the host's name without stopping anything: sets <see cref="BleHubHostOptions.LocalName"/>, re-advertises
    /// under the new name while hubs are running, and tells every connected client (<see cref="IBleHubConnection.HostRenamed"/>).
    /// </summary>
    Task Rename(string? localName, CancellationToken cancellationToken = default);
}


internal sealed class BleHubHost : IBleHubHost, IDisposable
{
    readonly IBleHostingManager hosting;
    readonly BleHubHostOptions hostOptions;
    readonly IServiceProvider services;
    readonly ILogger<BleHubHost>? logger;
    readonly LocalHubAdvertisement? localAdvertisement;
    readonly string serviceUuid;
    readonly Dictionary<Type, HubRuntime> runtimes = new();
    readonly Dictionary<HubRuntime, IGattCharacteristic> characteristics = new();
    readonly SemaphoreSlim startLock = new(1, 1);
    L2CapInstance? fileServer;
    L2CapInstance? mediaServer;
    Timer? sweepTimer;


    public BleHubHost(
        IBleHostingManager hosting,
        IEnumerable<BleHubRegistration> registrations,
        BleHubHostOptions hostOptions,
        BleHubProtocolOptions options,
        IBleHubSerializer serializer,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null,
        LocalHubAdvertisement? localAdvertisement = null
    )
    {
        this.hosting = hosting;
        this.localAdvertisement = localAdvertisement;
        this.hostOptions = hostOptions;
        this.services = services;
        this.logger = loggerFactory?.CreateLogger<BleHubHost>();
        this.serviceUuid = BleUuid.Normalize(hostOptions.ServiceUuid, nameof(hostOptions.ServiceUuid));

        var list = registrations.ToList();
        var duplicate = list.GroupBy(x => x.CharacteristicUuid, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Hubs {String.Join(", ", duplicate.Select(x => x.HubType.Name))} share characteristic {duplicate.Key} - each hub needs its own");

        foreach (var reg in list)
        {
            var runtime = new HubRuntime(reg, BleHubDispatchers.Get(reg.HubType), services, options, serializer, loggerFactory?.CreateLogger(reg.HubType.FullName!))
            {
                HostName = hostOptions.LocalName
            };
            runtime.Notify = (peer, frame, ct) => this.Notify(runtime, peer, frame, ct);
            this.runtimes.Add(reg.HubType, runtime);
        }
    }


    bool serviceAdded;
    bool infrastructureRunning;


    /// <summary>
    /// True while at least one hub is running
    /// </summary>
    public bool IsRunning => this.runtimes.Values.Any(x => x.IsRunning);
    public ushort FileTransferPsm { get; private set; }
    public event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;
    public event EventHandler<BleHubFileProgressEventArgs>? FileTransferProgress;


    internal HubRuntime GetRuntime(Type hubType) => this.runtimes.TryGetValue(hubType, out var runtime)
        ? runtime
        : throw new InvalidOperationException($"{hubType.Name} is not registered - add it with AddBleHubServer(server => server.AddHub<{hubType.Name}>(...))");


    public Task Start(CancellationToken cancellationToken = default)
    {
        if (this.runtimes.Count == 0)
            throw new InvalidOperationException("No hubs are registered - add them with AddBleHubServer");

        return this.StartHubs(this.runtimes.Values.ToList(), cancellationToken);
    }


    public Task Stop(string? reason = null) => this.StopHubs(this.runtimes.Values.ToList(), new HubDisconnect(HubDisconnectReason.ServerShutdown, reason));


    public async Task Rename(string? localName, CancellationToken cancellationToken = default)
    {
        await this.startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (String.Equals(this.hostOptions.LocalName, localName, StringComparison.Ordinal))
                return;

            this.hostOptions.LocalName = localName;
            if (this.IsRunning)
                await this.UpdateAdvertising(force: true).ConfigureAwait(false);

            // every hub, running or not - a stopped hub can still have clients on another transport
            await Task.WhenAll(this.runtimes.Values.Select(x => x.SetHostName(localName, cancellationToken))).ConfigureAwait(false);
            this.logger?.LogInformation("Host renamed to '{Name}'", localName);
        }
        finally
        {
            this.startLock.Release();
        }
    }


    public Task StartHub(HubRuntime runtime, CancellationToken cancellationToken) => this.StartHubs([runtime], cancellationToken);
    public Task StopHub(HubRuntime runtime, string? reason) => this.StopHubs([runtime], new HubDisconnect(HubDisconnectReason.ServerShutdown, reason ?? "Hub stopped"));


    async Task StartHubs(IReadOnlyList<HubRuntime> hubs, CancellationToken cancellationToken)
    {
        await this.startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var starting = hubs.Where(x => !x.IsRunning).ToList();
            if (starting.Count == 0)
                return;

            var startedInfrastructure = false;
            try
            {
                if (!this.infrastructureRunning)
                {
                    var access = await this.hosting.RequestAccess().ConfigureAwait(false);
                    if (access != AccessState.Available)
                        throw new BleHubException($"Bluetooth hosting is not available ({access})");

                    await this.StartFileServer().ConfigureAwait(false);
                    await this.StartMediaServer().ConfigureAwait(false);
                    this.sweepTimer = new Timer(_ => this.Sweep(), null, this.hostOptions.ClientSweepInterval, this.hostOptions.ClientSweepInterval);
                    this.infrastructureRunning = true;
                    startedInfrastructure = true;
                }

                // one GATT service holds every hub - hubs that are stopped stay in it but refuse handshakes, so starting/stopping
                // one hub never yanks the service out from under another hub's clients
                await this.EnsureService().ConfigureAwait(false);

                foreach (var runtime in starting)
                {
                    runtime.HostName = this.hostOptions.LocalName;
                    runtime.IsRunning = true;
                }

                await this.UpdateAdvertising().ConfigureAwait(false);
                this.logger?.LogInformation("Started hub(s) {Hubs}", String.Join(", ", starting.Select(x => x.Registration.HubType.Name)));
            }
            catch
            {
                foreach (var runtime in starting)
                    runtime.IsRunning = false;

                this.RemoveServiceWhenUnused();
                if (startedInfrastructure || !this.IsRunning)
                    await this.StopInfrastructure().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            this.startLock.Release();
        }
    }


    async Task StopHubs(IReadOnlyList<HubRuntime> hubs, HubDisconnect disconnect)
    {
        await this.startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var stopping = hubs.Where(x => x.IsRunning).ToList();
            if (stopping.Count == 0)
                return;

            // refuse new handshakes first, then ask everyone connected to leave
            foreach (var runtime in stopping)
                runtime.IsRunning = false;

            await Task.WhenAll(stopping.Select(x => x.DisconnectAll(disconnect))).ConfigureAwait(false);

            // the other hubs share the service and the advertisement - both stay until the last hub stops
            this.RemoveServiceWhenUnused();
            if (!this.IsRunning)
                await this.StopInfrastructure().ConfigureAwait(false);

            this.logger?.LogInformation("Stopped hub(s) {Hubs}: {Reason}", String.Join(", ", stopping.Select(x => x.Registration.HubType.Name)), disconnect.Description);
        }
        finally
        {
            this.startLock.Release();
        }
    }


    public void Dispose()
    {
        foreach (var runtime in this.runtimes.Values)
            runtime.IsRunning = false;

        this.RemoveServiceWhenUnused();
        _ = this.StopInfrastructure();
        this.startLock.Dispose();
    }


    async Task EnsureService()
    {
        if (this.serviceAdded)
            return;

        // a previous run that crashed may have left the service behind
        this.hosting.RemoveService(this.serviceUuid);
        await this.hosting.AddService(this.serviceUuid, true, sb =>
        {
            foreach (var runtime in this.runtimes.Values)
            {
                var rt = runtime;
                this.characteristics[rt] = sb.AddCharacteristic(rt.Registration.CharacteristicUuid, cb => cb
                    .SetWrite(req => this.OnWrite(rt, req), WriteOptions.Write)
                    .SetNotification(sub => this.OnSubscription(rt, sub), NotificationOptions.Notify)
                );
            }
        }).ConfigureAwait(false);
        this.serviceAdded = true;
    }


    /// <summary>
    /// Removes the GATT service once no hub is running
    /// </summary>
    void RemoveServiceWhenUnused()
    {
        if (!this.serviceAdded || this.IsRunning)
            return;

        this.hosting.RemoveService(this.serviceUuid);
        this.serviceAdded = false;
        this.characteristics.Clear();
    }


    /// <summary>
    /// Advertises the service while any hub is running - starting or stopping one hub beside another changes nothing on air.
    /// <paramref name="force"/> restarts the advertisement (the name has changed).
    /// </summary>
    async Task UpdateAdvertising(bool force = false)
    {
        if (!force && this.hosting.IsAdvertising)
            return;

        if (this.hosting.IsAdvertising)
        {
            this.hosting.StopAdvertising();
            this.localAdvertisement?.Clear();
        }

        await this.hosting.StartAdvertising(new AdvertisementOptions(this.hostOptions.LocalName, [this.serviceUuid])).ConfigureAwait(false);
        this.localAdvertisement?.Set(this.serviceUuid, this.hostOptions.LocalName);
    }


    Task StopInfrastructure()
    {
        this.infrastructureRunning = false;
        this.sweepTimer?.Dispose();
        this.sweepTimer = null;

        this.fileServer?.Dispose();
        this.fileServer = null;
        this.SetFileTransfer(0, false);

        this.mediaServer?.Dispose();
        this.mediaServer = null;
        this.SetMediaChannels(0, false, 0);

        if (this.hosting.IsAdvertising)
            this.hosting.StopAdvertising();

        this.localAdvertisement?.Clear();
        return Task.CompletedTask;
    }


    Task OnWrite(HubRuntime runtime, WriteRequest request)
    {
        if (request.Offset != 0)
        {
            Respond(request, GattState.InvalidOffset);
            return Task.CompletedTask;
        }

        // Shiny reports the payload size (ATT MTU minus the header) - the runtime works in ATT MTU
        var ok = runtime.OnFrame(request.Peripheral.Uuid, request.Peripheral.Mtu + BleConstants.AttHeaderSize, request.Peripheral, request.Data);
        Respond(request, ok ? GattState.Success : GattState.Failure);
        return Task.CompletedTask;
    }


    Task OnSubscription(HubRuntime runtime, CharacteristicSubscription sub)
    {
        if (!sub.IsSubscribing)
            // a client leaving on purpose says so first (a Disconnect frame), so an unsubscribe on its own is a dropped link -
            // both iOS and Android report a central that vanished as an unsubscribe
            runtime.OnPeerGone(sub.Peripheral.Uuid, new HubDisconnect(HubDisconnectReason.ClientTimeout, "Unsubscribed"));

        return Task.CompletedTask;
    }


    Task Notify(HubRuntime runtime, HostPeer peer, byte[] frame, CancellationToken cancellationToken)
    {
        if (!this.characteristics.TryGetValue(runtime, out var ch))
            throw new BleHubException("Hub host is not running");

        return ch.Notify(frame, cancellationToken, (HostedPeripheral)peer.Native!);
    }


    void Sweep()
    {
        // not every platform reports an unsubscribe when a central simply drops - reap anyone no longer subscribed
        foreach (var (runtime, ch) in this.characteristics.ToList())
        {
            try
            {
                var subscribed = ch.SubscribedCentrals.Select(x => x.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // clients on another transport aren't GATT subscribers - that transport reports when they leave
                foreach (var peer in runtime.Peers.Where(x => x.Channel == null).ToList())
                {
                    if (!subscribed.Contains(peer.Id))
                        runtime.OnPeerGone(peer.Id, new HubDisconnect(HubDisconnectReason.ClientTimeout));
                }
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Client sweep failed");
            }
        }
    }


    BleHubConnectedClient? FindClient(string peerId) => this.runtimes.Values
        .Select(x => x.FindClient(peerId))
        .FirstOrDefault(x => x != null);


    void SetFileTransfer(ushort psm, bool secure)
    {
        this.FileTransferPsm = psm;
        foreach (var runtime in this.runtimes.Values)
        {
            runtime.FileTransferPsm = psm;
            runtime.FileTransferSecure = secure;
        }
    }


    async Task StartFileServer()
    {
        var ft = this.hostOptions.FileTransfers;
        if (ft == null)
            return;

        try
        {
            var handler = this.services.GetService<IBleHubFileHandler>();
            if (handler != null)
            {
                this.fileServer = await this.hosting.HandleL2CapRequests(
                    ft.Secure,
                    (req, ct) => handler.Handle(new BleHubFileRequest(req, this.FindClient(req.PeerIdentifier)), ct),
                    ft.Transfer,
                    (req, ex) => this.logger?.LogWarning(ex, "File transfer {File} failed", req?.FileName)
                ).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateDirectory(ft.RootDirectory);
                var serverOptions = new L2CapFileServerOptions(ft.RootDirectory)
                {
                    Secure = ft.Secure,
                    AllowUploads = ft.AllowUploads,
                    AllowDownloads = ft.AllowDownloads,
                    OverwriteExistingUploads = ft.OverwriteExistingUploads,
                    MaxUploadSize = ft.MaxUploadSize,
                    Authorize = ft.Authorize == null ? null : req => ft.Authorize(new BleHubFileRequest(req, this.FindClient(req.PeerIdentifier))),
                    OnProgress = e => this.FileTransferProgress?.Invoke(this, new BleHubFileProgressEventArgs(
                        this.FindClient(e.PeerIdentifier), e.PeerIdentifier, e.Type, e.FileName, e.Progress
                    )),
                    OnCompleted = r => this.FileTransferred?.Invoke(this, new BleHubFileTransferredEventArgs(
                        this.FindClient(r.PeerIdentifier), r.PeerIdentifier, r.Result.Type, r.Result.FileName, r.LocalFilePath, r.Result.BytesTransferred, r.Result.Elapsed
                    )),
                    OnError = (req, ex) => this.logger?.LogWarning(ex, "File transfer {File} failed", req?.FileName)
                };
                if (ft.Transfer != null)
                    serverOptions.Transfer = ft.Transfer;

                this.fileServer = await this.hosting.OpenL2CapFileServer(serverOptions).ConfigureAwait(false);
            }
            this.SetFileTransfer(this.fileServer.Value.Psm, ft.Secure);
            this.logger?.LogInformation("File transfers served on PSM {Psm}", this.FileTransferPsm);
        }
        catch (Exception ex)
        {
            // L2CAP needs iOS 11+/Android 10+ - the hubs still work without it
            this.logger?.LogWarning(ex, "File transfers are unavailable on this device");
            this.SetFileTransfer(0, false);
        }
    }


    /// <summary>
    /// How long a new media channel has to present its token
    /// </summary>
    internal static readonly TimeSpan MediaHandshakeTimeout = TimeSpan.FromSeconds(10);


    void SetMediaChannels(ushort psm, bool secure, int maxPacketSize)
    {
        foreach (var runtime in this.runtimes.Values)
        {
            runtime.MediaChannelPsm = psm;
            runtime.MediaChannelSecure = secure;
            runtime.MediaMaxPacketSize = maxPacketSize;
        }
    }


    async Task StartMediaServer()
    {
        var media = this.hostOptions.MediaChannels;
        if (media == null)
            return;

        try
        {
            this.mediaServer = await this.hosting.OpenL2Cap(media.Secure, ch => this.OnMediaChannel(ch, media.MaxPacketSize)).ConfigureAwait(false);
            this.SetMediaChannels(this.mediaServer.Value.Psm, media.Secure, media.MaxPacketSize);
            this.logger?.LogInformation("Media channels served on PSM {Psm}", this.mediaServer.Value.Psm);
        }
        catch (Exception ex)
        {
            // L2CAP needs iOS 11+/Android 10+ - the hubs still work without it
            this.logger?.LogWarning(ex, "Media channels are unavailable on this device");
            this.SetMediaChannels(0, false, 0);
        }
    }


    void OnMediaChannel(L2CapChannel l2cap, int maxPacketSize)
    {
        // subscribed before anything else, so the token can't be missed
        var channel = FramedMediaChannel.Over(
            l2cap.DataReceived,
            maxPacketSize,
            (bytes, ct) => l2cap.Write(bytes).ToTask(ct),
            l2cap.Dispose,
            this.logger
        );
        _ = this.AcceptMediaChannel(channel, l2cap.Identifier);
    }


    /// <summary>
    /// The first frame is the token from a client's handshake ack: answer Accepted and hand the channel to that client's
    /// hub, or close it
    /// </summary>
    internal async Task AcceptMediaChannel(FramedMediaChannel channel, string peerIdentifier)
    {
        try
        {
            var token = await channel.ReadControl(MediaHandshakeTimeout, CancellationToken.None).ConfigureAwait(false);
            foreach (var runtime in this.runtimes.Values)
            {
                if (runtime.FindMediaToken(token) is not { } connectionId)
                    continue;

                // answered before the hub hears of it, so nothing the hub sends can get ahead of the answer
                using var cts = new CancellationTokenSource(MediaHandshakeTimeout);
                await channel.SendControl(new[] { MediaFrameCodec.Accepted }, cts.Token).ConfigureAwait(false);
                if (!runtime.AttachMediaChannel(connectionId, channel))
                    await channel.DisposeAsync().ConfigureAwait(false);

                return;
            }
            this.logger?.LogWarning("Refused a media channel from {Peer}: unknown token", peerIdentifier);
        }
        catch (Exception ex)
        {
            this.logger?.LogInformation(ex, "Media channel from {Peer} did not open", peerIdentifier);
        }
        await channel.DisposeAsync().ConfigureAwait(false);
    }


    static void Respond(WriteRequest request, GattState state)
    {
        if (request.IsReplyNeeded)
            request.Respond(state);
    }
}
