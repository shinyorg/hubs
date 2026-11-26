using System.Collections.Concurrent;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;
using HostedPeripheral = Shiny.BluetoothLE.Hosting.IPeripheral;

namespace Shiny.UniversalHubs;

/// <summary>
/// What a hub was told by UseBluetoothLE: the GATT service and the characteristic it is served on
/// </summary>
internal sealed record BleHubSettings(string ServiceUuid, string CharacteristicUuid);


/// <summary>
/// BLE hosting settings, beside the server's own (<see cref="UniversalServerOptions"/>)
/// </summary>
public sealed class BluetoothLETransportOptions
{
    /// <summary>
    /// How often to sweep for clients that dropped without unsubscribing
    /// </summary>
    public TimeSpan ClientSweepInterval { get; set; } = TimeSpan.FromSeconds(10);
}


/// <summary>
/// Serves hubs over BLE: every hub set up with UseBluetoothLE is a write+notify characteristic in its GATT service, its
/// frames run through a frame session, and files and media channels go over L2CAP. Added with <c>hub.UseBluetoothLE(...)</c>.
/// </summary>
public sealed class BleHostTransport : IHubTransport, IDisposable
{
    readonly IBleHostingManager hosting;
    readonly UniversalServerOptions serverOptions;
    readonly BluetoothLETransportOptions options;
    readonly BleHubProtocolOptions protocolOptions;
    readonly IServiceProvider services;
    readonly ILoggerFactory? loggerFactory;
    readonly ILogger? logger;
    readonly LocalHubAdvertisement? localAdvertisement;
    readonly SemaphoreSlim startLock = new(1, 1);
    readonly ConcurrentDictionary<IHubBinding, FrameHostSession> sessions = new();
    readonly ConcurrentDictionary<IHubBinding, byte> running = new();
    readonly Dictionary<IHubBinding, IGattCharacteristic> characteristics = new();
    string? serviceUuid;
    L2CapInstance? fileServer;
    L2CapInstance? mediaServer;
    Timer? sweepTimer;
    bool infrastructureRunning;
    bool fileTransferSecure;
    ushort fileTransferPsm;
    ushort mediaPsm;
    bool mediaSecure;
    int mediaMaxPacketSize;


    internal BleHostTransport(
        IBleHostingManager hosting,
        UniversalServerOptions serverOptions,
        BluetoothLETransportOptions options,
        BleHubProtocolOptions protocolOptions,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null,
        LocalHubAdvertisement? localAdvertisement = null
    )
    {
        this.hosting = hosting;
        this.serverOptions = serverOptions;
        this.options = options;
        this.protocolOptions = protocolOptions;
        this.services = services;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory?.CreateLogger<BleHostTransport>();
        this.localAdvertisement = localAdvertisement;
    }


    public string DisplayName => "Bluetooth LE";
    public bool IsSupported => true;
    public bool IsRunning(IHubBinding hub) => this.running.ContainsKey(hub);
    bool AnyRunning => !this.running.IsEmpty;

    UniversalHubHost Host => this.services.GetRequiredService<UniversalHubHost>();

    /// <summary>
    /// Every hub served over BLE, running or not - each keeps its characteristic in the service
    /// </summary>
    IEnumerable<IHubBinding> BleHubs => this.Host.Hubs.Where(x => x.GetSettings<BleHubSettings>() != null);


    internal FrameHostSession GetSession(IHubBinding hub) => this.sessions.GetOrAdd(hub, h =>
    {
        var runtime = (HubRuntime)h.Endpoint;
        var session = new FrameHostSession(runtime, this.protocolOptions, this.loggerFactory?.CreateLogger($"{typeof(FrameHostSession).FullName}.{h.Name}"))
        {
            FileTransferPsm = this.fileTransferPsm,
            FileTransferSecure = this.fileTransferSecure,
            MediaChannelPsm = this.mediaPsm,
            MediaChannelSecure = this.mediaSecure,
            MediaMaxPacketSize = this.mediaMaxPacketSize,
            IsRunning = () => this.running.ContainsKey(h),
            Transport = this
        };
        session.Notify = (link, frame, ct) => this.Notify(h, link, frame, ct);
        return session;
    });


    public async Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        await this.startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var starting = hubs.Where(x => !this.running.ContainsKey(x) && x.GetSettings<BleHubSettings>() != null).ToList();
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
                    this.sweepTimer = new Timer(_ => this.Sweep(), null, this.options.ClientSweepInterval, this.options.ClientSweepInterval);
                    this.infrastructureRunning = true;
                    startedInfrastructure = true;
                }

                // one GATT service holds every hub - hubs that are stopped stay in it but refuse handshakes, so starting/stopping
                // one hub never yanks the service out from under another hub's clients
                await this.EnsureService().ConfigureAwait(false);

                foreach (var hub in starting)
                {
                    this.GetSession(hub);
                    this.running[hub] = 0;
                }

                await this.UpdateAdvertising().ConfigureAwait(false);
                this.logger?.LogInformation("BLE hosting {Hubs}", String.Join(", ", starting.Select(x => x.Name)));
            }
            catch
            {
                foreach (var hub in starting)
                    this.running.TryRemove(hub, out _);

                this.RemoveServiceWhenUnused();
                if (startedInfrastructure || !this.AnyRunning)
                    await this.StopInfrastructure().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            this.startLock.Release();
        }
    }


    public async Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect)
    {
        await this.startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var stopping = hubs.Where(x => this.running.ContainsKey(x)).ToList();
            if (stopping.Count == 0)
                return;

            // refuse new handshakes first, then ask everyone connected over BLE to leave
            foreach (var hub in stopping)
                this.running.TryRemove(hub, out _);

            await Task.WhenAll(stopping.Select(x => this.DisconnectAll(x, disconnect))).ConfigureAwait(false);

            // the other hubs share the service and the advertisement - both stay until the last hub stops
            this.RemoveServiceWhenUnused();
            if (!this.AnyRunning)
                await this.StopInfrastructure().ConfigureAwait(false);

            this.logger?.LogInformation("Stopped BLE hosting of {Hubs}: {Reason}", String.Join(", ", stopping.Select(x => x.Name)), disconnect.Description);
        }
        finally
        {
            this.startLock.Release();
        }
    }


    /// <summary>
    /// Disconnects a hub's BLE clients - held ones too - and the links still hand-shaking
    /// </summary>
    async Task DisconnectAll(IHubBinding hub, HubDisconnect disconnect)
    {
        var session = this.GetSession(hub);
        var runtime = session.Endpoint;
        var ids = runtime.ConnectionIds(channel => channel is FrameHostLink.LinkChannel own && ReferenceEquals(own.Session, session));
        await Task.WhenAll(ids.Select(x => runtime.Disconnect(x, disconnect))).ConfigureAwait(false);
        await session.DisconnectPending(disconnect).ConfigureAwait(false);
    }


    public async Task HostRenamed(CancellationToken cancellationToken)
    {
        if (this.AnyRunning)
            await this.UpdateAdvertising(force: true).ConfigureAwait(false);
    }


    public void Dispose()
    {
        this.running.Clear();
        this.RemoveServiceWhenUnused();

        // a host that never started has nothing on the radio to stop - and may be on a device without a usable one
        if (this.infrastructureRunning)
            _ = this.StopInfrastructure();

        this.startLock.Dispose();
    }


    async Task EnsureService()
    {
        var hubs = this.BleHubs.ToList();
        if (this.serviceUuid != null)
        {
            if (hubs.All(this.characteristics.ContainsKey))
                return;

            // a hub added since the service went up has no characteristic in it, and a GATT service can't gain one - rebuild
            // it. BLE clients of the other hubs lose their subscriptions with it, so they're asked to reconnect first.
            var rebuild = new HubDisconnect(HubDisconnectReason.ServerShutdown, "The host's hubs changed - reconnect");
            await Task.WhenAll(this.characteristics.Keys.ToList().Select(x => this.DisconnectAll(x, rebuild))).ConfigureAwait(false);

            this.hosting.RemoveService(this.serviceUuid);
            this.serviceUuid = null;
            this.characteristics.Clear();
            this.logger?.LogInformation("Rebuilding the GATT service for a new hub");
        }

        var serviceUuid = hubs.Select(x => x.GetSettings<BleHubSettings>()!.ServiceUuid).Distinct(StringComparer.OrdinalIgnoreCase).Single();

        // a previous run that crashed may have left the service behind
        this.hosting.RemoveService(serviceUuid);
        await this.hosting.AddService(serviceUuid, true, sb =>
        {
            foreach (var hub in hubs)
            {
                var h = hub;
                this.characteristics[h] = sb.AddCharacteristic(h.GetSettings<BleHubSettings>()!.CharacteristicUuid, cb => cb
                    .SetWrite(req => this.OnWrite(h, req), WriteOptions.Write)
                    .SetNotification(sub => this.OnSubscription(h, sub), NotificationOptions.Notify)
                );
            }
        }).ConfigureAwait(false);
        this.serviceUuid = serviceUuid;
    }


    /// <summary>
    /// Removes the GATT service once no hub is running
    /// </summary>
    void RemoveServiceWhenUnused()
    {
        if (this.serviceUuid == null || this.AnyRunning)
            return;

        this.hosting.RemoveService(this.serviceUuid);
        this.serviceUuid = null;
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

        var uuid = this.serviceUuid!;
        await this.hosting.StartAdvertising(new AdvertisementOptions(this.serverOptions.LocalName, [uuid])).ConfigureAwait(false);
        this.localAdvertisement?.Set(uuid, this.serverOptions.LocalName);
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


    Task OnWrite(IHubBinding hub, WriteRequest request)
    {
        if (request.Offset != 0)
        {
            Respond(request, GattState.InvalidOffset);
            return Task.CompletedTask;
        }

        // Shiny reports the payload size (ATT MTU minus the header) - the session works in ATT MTU
        var ok = this.GetSession(hub).OnFrame(request.Peripheral.Uuid, request.Peripheral.Mtu + BleConstants.AttHeaderSize, request.Peripheral, request.Data);
        Respond(request, ok ? GattState.Success : GattState.Failure);
        return Task.CompletedTask;
    }


    Task OnSubscription(IHubBinding hub, CharacteristicSubscription sub)
    {
        if (!sub.IsSubscribing)
            // a client leaving on purpose says so first (a Disconnect frame), so an unsubscribe on its own is a dropped link -
            // both iOS and Android report a central that vanished as an unsubscribe
            this.GetSession(hub).OnPeerGone(sub.Peripheral.Uuid, new HubDisconnect(HubDisconnectReason.ClientTimeout, "Unsubscribed"));

        return Task.CompletedTask;
    }


    Task Notify(IHubBinding hub, FrameHostLink link, byte[] frame, CancellationToken cancellationToken)
    {
        if (!this.characteristics.TryGetValue(hub, out var ch))
            throw new BleHubException("Hub host is not running");

        return ch.Notify(frame, cancellationToken, (HostedPeripheral)link.Native!);
    }


    void Sweep()
    {
        // not every platform reports an unsubscribe when a central simply drops - reap anyone no longer subscribed
        foreach (var (hub, ch) in this.characteristics.ToList())
        {
            try
            {
                var subscribed = ch.SubscribedCentrals.Select(x => x.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // links, not connection ids: a client that resumed is on a different central than it first connected through
                var session = this.GetSession(hub);
                foreach (var link in session.Links.Where(x => !subscribed.Contains(x.LinkId)).ToList())
                    session.OnPeerGone(link.LinkId, new HubDisconnect(HubDisconnectReason.ClientTimeout));
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Client sweep failed");
            }
        }
    }


    // an L2CAP channel names the central it came from - the link, which a resumed client's connection id isn't
    BleHubConnectedClient? FindClient(string peerId) => this.sessions.Values
        .Select(x => x.FindClientOnLink(peerId))
        .FirstOrDefault(x => x != null);


    void SetFileTransfer(ushort psm, bool secure)
    {
        this.fileTransferPsm = psm;
        this.fileTransferSecure = secure;
        foreach (var session in this.sessions.Values)
        {
            session.FileTransferPsm = psm;
            session.FileTransferSecure = secure;
        }
    }


    async Task StartFileServer()
    {
        var ft = this.serverOptions.FileTransfers;
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
                    OnProgress = e => this.Host.OnFileProgress(new BleHubFileProgressEventArgs(
                        this.FindClient(e.PeerIdentifier), e.PeerIdentifier, e.Type, e.FileName, e.Progress
                    )),
                    OnCompleted = r => this.Host.OnFileTransferred(new BleHubFileTransferredEventArgs(
                        this.FindClient(r.PeerIdentifier), r.PeerIdentifier, r.Result.Type, r.Result.FileName, r.LocalFilePath, r.Result.BytesTransferred, r.Result.Elapsed
                    )),
                    OnError = (req, ex) => this.logger?.LogWarning(ex, "File transfer {File} failed", req?.FileName)
                };
                if (ft.Transfer != null)
                    serverOptions.Transfer = ft.Transfer;

                this.fileServer = await this.hosting.OpenL2CapFileServer(serverOptions).ConfigureAwait(false);
            }
            this.SetFileTransfer(this.fileServer.Value.Psm, ft.Secure);
            this.logger?.LogInformation("File transfers served on PSM {Psm}", this.fileTransferPsm);
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
        this.mediaPsm = psm;
        this.mediaSecure = secure;
        this.mediaMaxPacketSize = maxPacketSize;
        foreach (var session in this.sessions.Values)
        {
            session.MediaChannelPsm = psm;
            session.MediaChannelSecure = secure;
            session.MediaMaxPacketSize = maxPacketSize;
        }
    }


    async Task StartMediaServer()
    {
        var media = this.serverOptions.MediaChannels;
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
            var match = this.sessions.Values
                .Select(x => (Session: x, ConnectionId: x.FindMediaToken(token)))
                .FirstOrDefault(x => x.ConnectionId != null);

            if (match.ConnectionId is { } connectionId)
            {
                // answered before the hub hears of it, so nothing the hub sends can get ahead of the answer
                using var cts = new CancellationTokenSource(MediaHandshakeTimeout);
                await channel.SendControl(new[] { MediaFrameCodec.Accepted }, cts.Token).ConfigureAwait(false);
                if (!match.Session.AttachMediaChannel(connectionId, channel))
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
