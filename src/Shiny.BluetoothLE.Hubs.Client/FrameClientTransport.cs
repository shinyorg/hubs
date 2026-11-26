using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// The link under a <see cref="FrameClientTransport"/> - a GATT characteristic over BLE, or an in-memory radio in tests
/// </summary>
internal abstract class FrameLink
{
    /// <summary>
    /// Opens the link and starts feeding the host's frames to <see cref="FrameClientTransport.OnFrame"/> and a lost link to
    /// <see cref="FrameClientTransport.OnLinkLost"/>. Called to connect, and again for each resume after the link dropped.
    /// Returns the ATT MTU.
    /// </summary>
    public abstract Task<int> Open(FrameClientTransport transport, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <see cref="Open"/> can be called again after the link dropped
    /// </summary>
    public abstract bool CanReopen { get; }

    public abstract Task Write(byte[] frame, CancellationToken cancellationToken);

    /// <summary>
    /// Whether L2CAP channels (files, media) can be opened to the host
    /// </summary>
    public virtual bool CanOpenL2Cap => false;

    public virtual Task<L2CapChannel> OpenL2Cap(ushort psm, bool secure, CancellationToken cancellationToken)
        => throw new NotSupportedException("L2CAP is not available on this device");

    /// <summary>
    /// The connection is over. <paramref name="disconnect"/> is false when the link is already gone.
    /// Called once.
    /// </summary>
    public virtual Task Close(bool disconnect) => Task.CompletedTask;
}


/// <summary>
/// A hub connection over the frame protocol (<see cref="FrameCodec"/>) - handshake, calls, pushes, resume after a dropped
/// link, and L2CAP files and media - on whatever <see cref="FrameLink"/> carries the frames
/// </summary>
internal sealed class FrameClientTransport : IBleHubClientTransport
{
    static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan MaxResumeDelay = TimeSpan.FromSeconds(2);

    readonly BleHubProtocolOptions options;
    readonly FrameLink link;
    readonly IBleHubClientTransportEvents events;
    readonly ILogger? logger;
    readonly HubClientProtocol protocol;
    readonly CancellationTokenSource lifetime = new();
    HandshakeAck? ack;
    bool resumable;
    int closed;
    volatile bool resuming;


    public FrameClientTransport(BleHubProtocolOptions options, FrameLink link, IBleHubClientTransportEvents events, ILogger? logger)
    {
        this.options = options;
        this.link = link;
        this.events = events;
        this.logger = logger;

        this.protocol = new HubClientProtocol(options, link.Write, logger);
        this.protocol.Pushed += message =>
        {
            if (message.Kind == FrameKind.HostRenamed)
                events.HostRenamed(ProtocolSerializer.ReadRename(message.Payload).Name);
            else
                events.Pushed(message.Name ?? "", message.Payload);
        };
        this.protocol.DisconnectRequested += info =>
        {
            // hosts older than the reason code only ever sent a Disconnect to remove one client
            var disconnect = info.ToDisconnect(HubDisconnectReason.ServerDisconnect);
            logger?.LogInformation("Host ended the session: {Reason}", disconnect);
            _ = this.End(disconnect, true);
        };
    }


    bool IsClosed => Volatile.Read(ref this.closed) == 1;


    public void OnFrame(ReadOnlySpan<byte> frame) => this.protocol.OnFrame(frame);


    /// <summary>
    /// The link dropped. A connection the host holds gets back onto it; anything else ends.
    /// </summary>
    public void OnLinkLost(HubDisconnect drop)
    {
        // a drop while already resuming is that attempt failing - the resume loop retries
        if (this.IsClosed || this.resuming)
            return;

        if (this.ack is { Accepted: true, ResumeToken: not null, ResumeWindowSeconds: > 0 } && this.resumable && this.link.CanReopen)
            _ = this.Resume(drop);
        else
            _ = this.End(drop, false);
    }


    /// <summary>
    /// Ends the connection without a resume, the way a lost link the host can't hold does
    /// </summary>
    public Task Lose(HubDisconnect drop) => this.End(drop, false);


    public async Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken)
    {
        this.protocol.Mtu = await this.link.Open(this, cancellationToken).ConfigureAwait(false);
        this.resumable = info.Resumable && this.link.CanReopen;

        var ack = await this.protocol.Handshake(info with { Resumable = this.resumable }, cancellationToken).ConfigureAwait(false);
        this.ack = ack;
        if (ack.Accepted)
            this.logger?.LogInformation("Handshake over frames (MTU {Mtu}, file PSM {Psm})", this.protocol.Mtu, ack.FileTransferPsm);

        return ack;
    }


    public Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken)
        => this.protocol.Invoke(method, arguments, cancellationToken);

    public IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, CancellationToken cancellationToken)
        => this.protocol.Stream(method, arguments, cancellationToken);

    public Task Rename(string? name, CancellationToken cancellationToken)
        => this.protocol.Rename(name, cancellationToken);


    public bool CanTransferFiles => this.ack?.FileTransferPsm > 0 && this.link.CanOpenL2Cap && !this.resuming;


    public async Task<L2CapTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var ack = this.AssertFiles();
        using var channel = await this.link.OpenL2Cap(ack.FileTransferPsm, ack.FileTransferSecure, cancellationToken).ConfigureAwait(false);
        return await channel.UploadFile(source, remoteFileName, length, ToAction(progress), null, cancellationToken).ConfigureAwait(false);
    }


    public async Task<L2CapTransferResult> Download(string remoteFileName, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var ack = this.AssertFiles();
        using var channel = await this.link.OpenL2Cap(ack.FileTransferPsm, ack.FileTransferSecure, cancellationToken).ConfigureAwait(false);
        return await channel.DownloadFile(remoteFileName, destination, ToAction(progress), null, cancellationToken).ConfigureAwait(false);
    }


    public bool CanOpenMediaChannel => this.ack is { MediaChannelPsm: > 0, MediaToken: not null } && this.link.CanOpenL2Cap;


    public async Task<IBleHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken)
    {
        var ack = this.ack;
        if (ack == null || ack.MediaChannelPsm == 0 || ack.MediaToken == null || ack.MediaMaxPacketSize < 1)
            throw new NotSupportedException("The host does not serve media channels");

        if (!this.link.CanOpenL2Cap)
            throw new NotSupportedException("L2CAP is not available on this device");

        var l2cap = await this.link.OpenL2Cap(ack.MediaChannelPsm, ack.MediaChannelSecure, cancellationToken).ConfigureAwait(false);
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
                answer = await channel.ReadControl(this.options.RequestTimeout, cancellationToken).ConfigureAwait(false);
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


    public async Task Close(CancellationToken cancellationToken)
    {
        // say goodbye first, so the host doesn't take the link closing for a dropped one
        if (!this.IsClosed && !this.resuming && this.ack is { Accepted: true })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                await this.protocol.SendDisconnect(new HubDisconnect(HubDisconnectReason.ClientDisconnect), cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "Could not tell the host we are leaving");
            }
        }
        await this.Shutdown(new HubDisconnect(HubDisconnectReason.ClientDisconnect), true).ConfigureAwait(false);
    }


    /// <summary>
    /// Re-opens the link and hands the host its resume token, until the host takes the connection back, refuses, or its
    /// resume window runs out
    /// </summary>
    async Task Resume(HubDisconnect drop)
    {
        var ack = this.ack!;
        this.resuming = true;
        this.events.Reconnecting(drop);
        this.protocol.FailAll(new BleHubDisconnectedException("The link to the host dropped - the call may not have completed", drop));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(ack.ResumeWindowSeconds);
        var delay = TimeSpan.FromMilliseconds(250);
        string? refusal = null;

        try
        {
            while (refusal == null && !this.IsClosed && DateTimeOffset.UtcNow < deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
                attempt.CancelAfter(remaining < AttemptTimeout ? remaining : AttemptTimeout);
                try
                {
                    this.protocol.Mtu = await this.link.Open(this, attempt.Token).ConfigureAwait(false);
                    var reply = await this.protocol.Resume(new ResumeInfo(FrameCodec.ProtocolVersion, ack.ResumeToken!), attempt.Token).ConfigureAwait(false);
                    if (!reply.Accepted)
                    {
                        refusal = reply.Reason ?? "The host could not resume the connection";
                    }
                    else if (!this.IsClosed)
                    {
                        var previousName = this.ack?.HostName;
                        this.ack = reply;
                        this.resuming = false;
                        if (reply.HostName != null && reply.HostName != previousName)
                            this.events.HostRenamed(reply.HostName);

                        this.events.Reconnected();
                        return;
                    }
                }
                catch (BleHubRemoteException ex)
                {
                    // a host older than resume doesn't know the frame
                    refusal = ex.Message;
                }
                catch (Exception ex) when (!this.lifetime.IsCancellationRequested)
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
                    // closed (Disconnect, Dispose) while resuming
                    return;
                }
            }
        }
        finally
        {
            this.resuming = false;
        }

        if (!this.IsClosed)
        {
            this.logger?.LogInformation("Could not resume: {Reason}", refusal ?? "the resume window ran out");
            await this.End(new HubDisconnect(drop.Reason, refusal ?? drop.Message ?? "Could not get back to the host in time"), true).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// The connection ended without the app asking - tell the client, then close the link
    /// </summary>
    async Task End(HubDisconnect disconnect, bool disconnectLink)
    {
        if (Interlocked.Exchange(ref this.closed, 1) == 1)
            return;

        this.lifetime.Cancel();
        this.protocol.FailAll(new BleHubDisconnectedException("The connection to the host was closed", disconnect));
        this.events.Closed(disconnect);
        await this.CloseLink(disconnectLink).ConfigureAwait(false);
    }


    async Task Shutdown(HubDisconnect disconnect, bool disconnectLink)
    {
        if (Interlocked.Exchange(ref this.closed, 1) == 1)
            return;

        this.lifetime.Cancel();
        this.protocol.FailAll(new BleHubDisconnectedException("The connection to the host was closed", disconnect));
        await this.CloseLink(disconnectLink).ConfigureAwait(false);
    }


    async Task CloseLink(bool disconnect)
    {
        try
        {
            await this.link.Close(disconnect).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Link did not close cleanly");
        }
    }


    HandshakeAck AssertFiles()
    {
        var ack = this.ack;
        if (ack == null || ack.FileTransferPsm == 0)
            throw new BleHubFileTransferNotSupportedException("The host does not serve file transfers");

        if (!this.link.CanOpenL2Cap)
            throw new BleHubFileTransferNotSupportedException("L2CAP is not available on this device");

        return ack;
    }


    static Action<TransferProgress>? ToAction(IProgress<TransferProgress>? progress)
        => progress == null ? null : progress.Report;
}


/// <summary>
/// The frame link to a BLE peripheral: the hub's characteristic (write + notify) and its L2CAP channels
/// </summary>
internal sealed class BleFrameLink(
    IPeripheral peripheral,
    string serviceUuid,
    string characteristicUuid,
    BleConnectionTracker connections,
    ILogger? logger
) : FrameLink
{
    readonly List<IDisposable> subscriptions = new();

    public override bool CanReopen => true;
    public override bool CanOpenL2Cap => peripheral.IsL2CapAvailable();


    public override async Task<int> Open(FrameClientTransport transport, CancellationToken cancellationToken)
    {
        this.DropSubscriptions();
        await peripheral.ConnectAsync(new ConnectionConfig(false), cancellationToken).ConfigureAwait(false);
        var attMtu = await BleHubClient.NegotiateMtu(peripheral, logger, cancellationToken).ConfigureAwait(false);

        // fail fast if the host does not serve this hub
        await peripheral.GetCharacteristicAsync(serviceUuid, characteristicUuid, cancellationToken).ConfigureAwait(false);

        var subscribed = peripheral.WaitForCharacteristicSubscriptionAsync(serviceUuid, characteristicUuid, cancellationToken);
        this.AddSubscription(peripheral
            .NotifyCharacteristic(serviceUuid, characteristicUuid, false)
            .Subscribe(
                r =>
                {
                    if (r.Data is { Length: > 0 } data)
                        transport.OnFrame(data);
                },
                ex => logger?.LogWarning(ex, "Notification subscription failed")
            ));
        await subscribed.ConfigureAwait(false);

        this.AddSubscription(peripheral
            .WhenStatusChanged()
            .Where(x => x == ConnectionState.Disconnected)
            .Subscribe(__ => transport.OnLinkLost(new HubDisconnect(HubDisconnectReason.ClientTimeout))));

        return attMtu;
    }


    public override Task Write(byte[] frame, CancellationToken cancellationToken)
        => peripheral.WriteCharacteristicAsync(serviceUuid, characteristicUuid, frame, true, cancellationToken, 10000);


    public override Task<L2CapChannel> OpenL2Cap(ushort psm, bool secure, CancellationToken cancellationToken)
        => peripheral.OpenL2CapChannelAsync(psm, secure, cancellationToken);


    public override async Task Close(bool disconnect)
    {
        this.DropSubscriptions();

        // several hub clients can share the connection - only the last one out disconnects
        if (!connections.Release(peripheral.Uuid) || !disconnect)
            return;

        try
        {
            await peripheral.DisconnectAsync(timeout: TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Disconnect did not complete cleanly");
            peripheral.CancelConnection();
        }
    }


    void AddSubscription(IDisposable subscription)
    {
        lock (this.subscriptions)
            this.subscriptions.Add(subscription);
    }


    void DropSubscriptions()
    {
        List<IDisposable> old;
        lock (this.subscriptions)
        {
            old = this.subscriptions.ToList();
            this.subscriptions.Clear();
        }
        foreach (var sub in old)
            sub.Dispose();
    }
}


/// <summary>
/// A frame link made of callbacks - an in-memory radio in tests
/// </summary>
internal sealed class DelegateFrameLink(
    Func<byte[], CancellationToken, Task> write,
    int mtu,
    Func<ushort, bool, CancellationToken, Task<L2CapChannel>>? openL2Cap,
    Func<CancellationToken, Task<int>>? relink
) : FrameLink
{
    bool opened;

    public override bool CanReopen => relink != null;
    public override bool CanOpenL2Cap => openL2Cap != null;

    public override Task<int> Open(FrameClientTransport transport, CancellationToken cancellationToken)
    {
        if (!this.opened)
        {
            this.opened = true;
            return Task.FromResult(mtu);
        }
        return relink != null ? relink(cancellationToken) : throw new InvalidOperationException("This link can't be reopened");
    }

    public override Task Write(byte[] frame, CancellationToken cancellationToken) => write(frame, cancellationToken);

    public override Task<L2CapChannel> OpenL2Cap(ushort psm, bool secure, CancellationToken cancellationToken)
        => openL2Cap != null ? openL2Cap(psm, secure, cancellationToken) : base.OpenL2Cap(psm, secure, cancellationToken);
}
