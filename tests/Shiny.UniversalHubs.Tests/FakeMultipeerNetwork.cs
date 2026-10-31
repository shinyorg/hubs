using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Channels;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// MultipeerConnectivity in memory: advertisements, browsing and sessions within one process. Each end of a session
/// delivers on its own queue, one thing at a time and in order, the way MC's delegate queue does.
/// </summary>
internal sealed class FakeMultipeerNetwork : IMultipeerLink
{
    readonly object gate = new();
    readonly List<FakeAdvertisement> advertisements = new();
    readonly List<(string ServiceType, IObserver<MultipeerSighting> Observer)> browsers = new();
    readonly List<(FakeMultipeerConnection Client, FakeMultipeerConnection Host)> sessions = new();


    public bool IsSupported => true;

    public IReadOnlyList<FakeAdvertisement> Advertisements
    {
        get
        {
            lock (this.gate)
                return this.advertisements.ToList();
        }
    }

    public IReadOnlyList<(FakeMultipeerConnection Client, FakeMultipeerConnection Host)> Sessions
    {
        get
        {
            lock (this.gate)
                return this.sessions.ToList();
        }
    }


    public IDisposable Advertise(string serviceType, string displayName, IReadOnlyDictionary<string, string> info, Action<IMultipeerConnection> accepted)
    {
        var ad = new FakeAdvertisement(serviceType, displayName, info, accepted);
        List<IObserver<MultipeerSighting>> watching;
        lock (this.gate)
        {
            this.advertisements.Add(ad);
            watching = this.browsers.Where(x => x.ServiceType == serviceType).Select(x => x.Observer).ToList();
        }
        foreach (var observer in watching)
            observer.OnNext(new MultipeerSighting(new FakePeer(displayName), displayName, info));

        return Disposable.Create(() =>
        {
            List<IObserver<MultipeerSighting>> still;
            lock (this.gate)
            {
                this.advertisements.Remove(ad);
                still = this.browsers.Where(x => x.ServiceType == serviceType).Select(x => x.Observer).ToList();
            }
            foreach (var observer in still)
                observer.OnNext(new MultipeerSighting(new FakePeer(displayName), displayName, null));
        });
    }


    public IObservable<MultipeerSighting> Browse(string serviceType, string displayName) => Observable.Create<MultipeerSighting>(observer =>
    {
        var entry = (serviceType, observer);
        List<FakeAdvertisement> existing;
        lock (this.gate)
        {
            this.browsers.Add(entry);
            existing = this.advertisements.Where(x => x.ServiceType == serviceType).ToList();
        }
        foreach (var ad in existing)
            observer.OnNext(new MultipeerSighting(new FakePeer(ad.DisplayName), ad.DisplayName, ad.Info));

        return () =>
        {
            lock (this.gate)
                this.browsers.Remove(entry);
        };
    });


    public Task<IMultipeerConnection> Connect(object peer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // like a persisted MCPeerID, a host is the same peer across re-advertisements
        var name = ((FakePeer)peer).DisplayName;
        FakeAdvertisement ad;
        lock (this.gate)
            ad = this.advertisements.LastOrDefault(x => x.DisplayName == name) ?? throw new BleHubException($"{name} did not answer the invitation");

        var client = new FakeMultipeerConnection(ad.DisplayName);
        var host = new FakeMultipeerConnection("Phone");
        client.Other = host;
        host.Other = client;
        lock (this.gate)
            this.sessions.Add((client, host));

        ad.Accepted(host);
        return Task.FromResult<IMultipeerConnection>(client);
    }
}


internal sealed record FakePeer(string DisplayName);


internal sealed record FakeAdvertisement(string ServiceType, string DisplayName, IReadOnlyDictionary<string, string> Info, Action<IMultipeerConnection> Accepted);


internal sealed class FakeMultipeerConnection : MultipeerConnectionBase
{
    readonly Channel<Action> inbox = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    readonly string remoteName;


    public FakeMultipeerConnection(string remoteName)
    {
        this.remoteName = remoteName;
        _ = Task.Run(async () =>
        {
            await foreach (var action in this.inbox.Reader.ReadAllAsync())
                action();
        });
    }


    public FakeMultipeerConnection Other { get; set; } = null!;
    public override string RemoteName => this.remoteName;


    public override void Send(byte[] message)
    {
        if (this.IsClosed)
            throw new BleHubDisconnectedException("The fake session has ended");

        var copy = message.ToArray();
        this.Other.Post(() => this.Other.RaiseReceived(copy));
    }


    public override Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken)
    {
        if (this.IsClosed)
            throw new BleHubDisconnectedException("The fake session has ended");

        var arrived = Path.Combine(Path.GetTempPath(), "fake-multipeer-" + Guid.NewGuid().ToString("N"));
        File.Copy(path, arrived);
        progress?.Invoke(1);
        this.Other.Post(() => this.Other.RaiseFileProgress(name, 0.5));
        this.Other.Post(() => this.Other.RaiseFileReceived(name, arrived, null));
        return Task.CompletedTask;
    }


    public override void Dispose() => this.Drop();


    /// <summary>
    /// The session ends for both ends, after whatever was already on its way
    /// </summary>
    public void Drop()
    {
        this.Post(this.RaiseClosed);
        this.Other.Post(this.Other.RaiseClosed);
    }


    void Post(Action action) => this.inbox.Writer.TryWrite(action);
}
