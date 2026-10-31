using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Foundation;
using Microsoft.Extensions.Logging;
using MultipeerConnectivity;
using ObjCRuntime;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// The Multipeer radio on iOS, Mac Catalyst and macOS. Hosts advertise with a persisted MCPeerID (a new one each launch
/// shows up twice in browsers), accept every invitation into a session of its own, and both ends require encryption.
/// </summary>
internal sealed class AppleMultipeerLink(ILogger<AppleMultipeerLink>? logger = null) : IMultipeerLink
{
    const string PeerKey = "shiny.universalhubs.multipeer.peer";
    readonly ConcurrentDictionary<string, bool> checkedPlist = new(StringComparer.Ordinal);


    public bool IsSupported => true;


    public IDisposable Advertise(string serviceType, string displayName, IReadOnlyDictionary<string, string> info, Action<IMultipeerConnection> accepted)
    {
        this.CheckPlist(serviceType);

        var me = HostPeer(displayName);
        var advertiser = new MCNearbyServiceAdvertiser(me, ToDictionary(info), serviceType);
        var handler = new AdvertiserDelegate(me, accepted, logger);
        advertiser.Delegate = handler;
        advertiser.StartAdvertisingPeer();

        return Disposable.Create(() =>
        {
            advertiser.StopAdvertisingPeer();
            advertiser.Delegate = null!;
            GC.KeepAlive(handler);
            advertiser.Dispose();
        });
    }


    public IObservable<MultipeerSighting> Browse(string serviceType, string displayName) => Observable.Create<MultipeerSighting>(observer =>
    {
        this.CheckPlist(serviceType);

        var me = new MCPeerID(displayName);
        var browser = new MCNearbyServiceBrowser(me, serviceType);
        var handler = new BrowserDelegate(me, observer, logger);
        browser.Delegate = handler;
        browser.StartBrowsingForPeers();

        // the browser itself stays alive in each sighting: invitations go out through the browser that found the peer
        return () =>
        {
            browser.StopBrowsingForPeers();
            GC.KeepAlive(handler);
        };
    });


    public async Task<IMultipeerConnection> Connect(object peer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var found = peer as BrowsedPeer ?? throw new ArgumentException("Not a peer this link browsed", nameof(peer));

        // no identity - the binding passes a null one through as nil (only the certificate overload rejects it)
        var session = new MCSession(found.Me, null!, MCEncryptionPreference.Required);
        var connection = new AppleMultipeerConnection(session, found.Peer, logger);
        found.Browser.InvitePeer(found.Peer, session, null, timeout.TotalSeconds);
        try
        {
            await connection.Connected.WaitAsync(timeout + TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex)
        {
            connection.Dispose();
            throw ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? ex
                : new BleHubException($"Could not connect to {found.Peer.DisplayName} over Multipeer: {ex.Message}", ex);
        }
    }


    /// <summary>
    /// The host's peer, kept across launches so browsers don't list it twice. A new name is a new peer.
    /// </summary>
    MCPeerID HostPeer(string displayName)
    {
        var defaults = NSUserDefaults.StandardUserDefaults;
        try
        {
            if (defaults.DataForKey(PeerKey) is { } data
                && NSKeyedUnarchiver.GetUnarchivedObject(new Class("MCPeerID"), data, out _) is MCPeerID saved
                && saved.DisplayName == displayName)
            {
                return saved;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not read the saved Multipeer peer");
        }

        var peer = new MCPeerID(displayName);
        try
        {
            if (NSKeyedArchiver.GetArchivedData(peer, true, out _) is { } archived)
                defaults.SetValueForKey(archived, new NSString(PeerKey));
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not save the Multipeer peer");
        }
        return peer;
    }


    /// <summary>
    /// Without NSBonjourServices entries for the type (and a local network usage description) iOS finds nothing and says
    /// nothing - so say it here, once per service type
    /// </summary>
    void CheckPlist(string serviceType)
    {
        if (logger == null || !this.checkedPlist.TryAdd(serviceType, true))
            return;

        var info = NSBundle.MainBundle.InfoDictionary;
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (info?.ObjectForKey(new NSString("NSBonjourServices")) is NSArray services)
        {
            for (nuint i = 0; i < services.Count; i++)
                declared.Add(services.GetItem<NSObject>(i)?.ToString() ?? String.Empty);
        }

        var missing = MultipeerProtocol.BonjourServices(serviceType).Where(x => !declared.Contains(x)).ToList();
        if (missing.Count > 0)
            logger.LogWarning("Info.plist NSBonjourServices is missing {Missing} - Multipeer discovery will find nothing", String.Join(" and ", missing));

        if (info?.ObjectForKey(new NSString("NSLocalNetworkUsageDescription")) == null)
            logger.LogWarning("Info.plist has no NSLocalNetworkUsageDescription - Multipeer needs local network access");
    }


    static NSDictionary ToDictionary(IReadOnlyDictionary<string, string> info)
    {
        var dictionary = new NSMutableDictionary();
        foreach (var pair in info)
            dictionary[new NSString(pair.Key)] = new NSString(pair.Value);
        return dictionary;
    }


    static IReadOnlyDictionary<string, string> FromDictionary(NSDictionary? info)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        if (info != null)
        {
            foreach (var key in info.Keys)
                dictionary[key.ToString()] = info[key]?.ToString() ?? String.Empty;
        }
        return dictionary;
    }


    sealed class AdvertiserDelegate(MCPeerID me, Action<IMultipeerConnection> accepted, ILogger? logger) : MCNearbyServiceAdvertiserDelegate
    {
        public override void DidReceiveInvitationFromPeer(MCNearbyServiceAdvertiser advertiser, MCPeerID peerID, NSData? context, MCNearbyServiceAdvertiserInvitationHandler invitationHandler)
        {
            // a session per client: no 8-peer cap, and clients never mesh with each other
            var session = new MCSession(me, null!, MCEncryptionPreference.Required);
            var connection = new AppleMultipeerConnection(session, peerID, logger);
            invitationHandler(true, session);
            accepted(connection);
        }

        public override void DidNotStartAdvertisingPeer(MCNearbyServiceAdvertiser advertiser, NSError error)
            => logger?.LogError("Multipeer advertising did not start: {Error}", error.LocalizedDescription);
    }


    sealed class BrowserDelegate(MCPeerID me, IObserver<MultipeerSighting> observer, ILogger? logger) : MCNearbyServiceBrowserDelegate
    {
        public override void FoundPeer(MCNearbyServiceBrowser browser, MCPeerID peerID, NSDictionary? info)
            => observer.OnNext(new MultipeerSighting(new BrowsedPeer(browser, me, peerID), peerID.DisplayName, FromDictionary(info)));

        public override void LostPeer(MCNearbyServiceBrowser browser, MCPeerID peerID)
            => observer.OnNext(new MultipeerSighting(new BrowsedPeer(browser, me, peerID), peerID.DisplayName, null));

        public override void DidNotStartBrowsingForPeers(MCNearbyServiceBrowser browser, NSError error)
        {
            logger?.LogError("Multipeer browsing did not start: {Error}", error.LocalizedDescription);
            observer.OnError(new BleHubException($"Multipeer browsing did not start: {error.LocalizedDescription}"));
        }
    }


    /// <summary>
    /// A browsed peer. Equal for the same native peer, so a lost sighting matches its found one.
    /// </summary>
    sealed class BrowsedPeer(MCNearbyServiceBrowser browser, MCPeerID me, MCPeerID peer)
    {
        public MCNearbyServiceBrowser Browser => browser;
        public MCPeerID Me => me;
        public MCPeerID Peer => peer;

        public override bool Equals(object? obj) => obj is BrowsedPeer other && other.Peer.IsEqual(this.Peer);
        public override int GetHashCode() => (int)this.Peer.GetNativeHash();
    }
}


/// <summary>
/// One MCSession with one remote peer
/// </summary>
internal sealed class AppleMultipeerConnection : MultipeerConnectionBase
{
    readonly MCSession session;
    readonly MCPeerID remote;
    readonly ILogger? logger;
    readonly SessionDelegate handler;
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentDictionary<string, NSProgress> receiving = new(StringComparer.Ordinal);
    int disposed;


    public AppleMultipeerConnection(MCSession session, MCPeerID remote, ILogger? logger)
    {
        this.session = session;
        this.remote = remote;
        this.logger = logger;
        this.handler = new SessionDelegate(this);
        session.Delegate = this.handler;
    }


    public override string RemoteName => this.remote.DisplayName;

    /// <summary>
    /// Completes once the remote peer is connected; fails if it never is
    /// </summary>
    public Task Connected => this.connected.Task;


    public override void Send(byte[] message)
    {
        if (this.IsClosed)
            throw new BleHubDisconnectedException("The Multipeer session has ended");

        using var data = NSData.FromArray(message);
        if (!this.session.SendData(data, [this.remote], MCSessionSendDataMode.Reliable, out var error))
            throw new BleHubException($"Multipeer send failed: {error?.LocalizedDescription ?? "unknown error"}");
    }


    public override async Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = this.session.SendResource(NSUrl.FromFilename(path), name, this.remote, error =>
        {
            if (error == null)
                done.TrySetResult();
            else
                done.TrySetException(new BleHubException($"Multipeer file transfer failed: {error.LocalizedDescription}"));
        });
        if (sending == null)
            throw new BleHubException("Multipeer would not send the file");

        using var cancel = cancellationToken.Register(() => sending.Cancel());
        while (!done.Task.IsCompleted)
        {
            progress?.Invoke(sending.FractionCompleted);
            await Task.WhenAny(done.Task, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await done.Task.ConfigureAwait(false);
        progress?.Invoke(1);
    }


    public override void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

        this.session.Disconnect();
        this.Gone();
    }


    void Gone()
    {
        this.connected.TrySetException(new BleHubException("The peer did not connect"));
        foreach (var progress in this.receiving.Values)
            progress.Cancel();
        this.receiving.Clear();
        this.RaiseClosed();
    }


    void StateChanged(MCPeerID peer, MCSessionState state)
    {
        if (!peer.IsEqual(this.remote))
            return;

        this.logger?.LogDebug("Multipeer session with {Peer}: {State}", peer.DisplayName, state);
        switch (state)
        {
            case MCSessionState.Connected:
                this.connected.TrySetResult();
                break;

            case MCSessionState.NotConnected:
                this.session.Delegate = null!;
                this.Gone();
                break;
        }
    }


    void ResourceStarted(string name, NSProgress progress)
    {
        this.receiving[name] = progress;
        _ = Task.Run(async () =>
        {
            while (this.receiving.ContainsKey(name))
            {
                this.RaiseFileProgress(name, progress.FractionCompleted);
                await Task.Delay(250).ConfigureAwait(false);
            }
        });
    }


    void ResourceFinished(string name, NSUrl? localUrl, NSError? error)
    {
        this.receiving.TryRemove(name, out _);
        if (error != null || localUrl?.Path is not { } source)
        {
            this.RaiseFileReceived(name, null, new BleHubException(error?.LocalizedDescription ?? "The file did not arrive"));
            return;
        }

        // Multipeer deletes its copy as soon as this callback returns
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "shiny-multipeer-" + Guid.NewGuid().ToString("N"));
            File.Copy(source, path);
            this.RaiseFileReceived(name, path, null);
        }
        catch (Exception ex)
        {
            this.RaiseFileReceived(name, null, ex);
        }
    }


    sealed class SessionDelegate(AppleMultipeerConnection connection) : MCSessionDelegate
    {
        public override void DidChangeState(MCSession session, MCPeerID peerID, MCSessionState state)
            => connection.StateChanged(peerID, state);

        public override void DidReceiveData(MCSession session, NSData data, MCPeerID peerID)
            => connection.RaiseReceived(data.ToArray());

        public override void DidReceiveStream(MCSession session, NSInputStream stream, string streamName, MCPeerID peerID)
            => stream.Close();   // hubs don't use Multipeer streams

        public override void DidStartReceivingResource(MCSession session, string resourceName, MCPeerID fromPeer, NSProgress progress)
            => connection.ResourceStarted(resourceName, progress);

        public override void DidFinishReceivingResource(MCSession session, string resourceName, MCPeerID fromPeer, NSUrl? localUrl, NSError? error)
            => connection.ResourceFinished(resourceName, localUrl, error);
    }
}
