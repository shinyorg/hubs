namespace Shiny.UniversalHubs;

/// <summary>
/// This device's Multipeer radio. The Apple implementation wraps MultipeerConnectivity; tests use an in-memory one. The
/// transports above it never see an MC type, so everything except the radio runs (and is tested) on plain .NET.
/// </summary>
internal interface IMultipeerLink
{
    /// <summary>
    /// False where there is no MultipeerConnectivity (Android, Windows, Linux)
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Advertises this device as <paramref name="displayName"/> under <paramref name="serviceType"/> and accepts every
    /// invitation into a session of its own, handed to <paramref name="accepted"/>. Dispose to stop advertising; sessions
    /// already accepted carry on.
    /// </summary>
    IDisposable Advertise(string serviceType, string displayName, IReadOnlyDictionary<string, string> info, Action<IMultipeerConnection> accepted);

    /// <summary>
    /// Peers advertising <paramref name="serviceType"/> as they appear and go, browsing as <paramref name="displayName"/>.
    /// Dispose the subscription to stop browsing.
    /// </summary>
    IObservable<MultipeerSighting> Browse(string serviceType, string displayName);

    /// <summary>
    /// Invites <paramref name="peer"/> (a <see cref="MultipeerSighting.Peer"/>) into a new session and waits until it is
    /// connected. The invitation comes from the browser that saw the peer, under that browser's display name.
    /// </summary>
    Task<IMultipeerConnection> Connect(object peer, TimeSpan timeout, CancellationToken cancellationToken);
}


/// <summary>
/// A peer seen while browsing
/// </summary>
/// <param name="Peer">The native peer - pass it back to <see cref="IMultipeerLink.Connect"/></param>
/// <param name="DisplayName">Its display name</param>
/// <param name="Info">What it advertises; null when it has gone</param>
internal sealed record MultipeerSighting(object Peer, string DisplayName, IReadOnlyDictionary<string, string>? Info);


/// <summary>
/// One session with one remote peer. Messages are reliable and ordered.
/// </summary>
internal interface IMultipeerConnection : IDisposable
{
    string RemoteName { get; }

    /// <summary>
    /// Starts delivering to <paramref name="handler"/> - anything that arrived before is delivered first, in order
    /// </summary>
    void Start(IMultipeerConnectionHandler handler);

    /// <summary>
    /// Sends one message. Throws when the session is gone.
    /// </summary>
    void Send(byte[] message);

    /// <summary>
    /// Sends a file as a resource named <paramref name="name"/>, reporting the fraction sent
    /// </summary>
    Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken);
}


/// <summary>
/// What a session reports. Called one at a time, in order, and must not block.
/// </summary>
internal interface IMultipeerConnectionHandler
{
    void Received(ReadOnlyMemory<byte> message);

    /// <summary>
    /// A resource is arriving
    /// </summary>
    void FileProgress(string name, double fraction);

    /// <summary>
    /// A resource arrived - <paramref name="path"/> is a file the handler now owns (and deletes) - or failed
    /// </summary>
    void FileReceived(string name, string? path, Exception? error);

    /// <summary>
    /// The session ended, whoever ended it. Called once.
    /// </summary>
    void Closed();
}


/// <summary>
/// Holds what a session reports until <see cref="IMultipeerConnection.Start"/>, then passes it on in order
/// </summary>
internal abstract class MultipeerConnectionBase : IMultipeerConnection
{
    readonly object gate = new();
    readonly List<Action<IMultipeerConnectionHandler>> pending = new();
    IMultipeerConnectionHandler? handler;
    bool closed;


    public abstract string RemoteName { get; }
    public abstract void Send(byte[] message);
    public abstract Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken);
    public abstract void Dispose();


    public void Start(IMultipeerConnectionHandler handler)
    {
        lock (this.gate)
        {
            if (this.handler != null)
                throw new InvalidOperationException("A Multipeer connection has one handler");

            this.handler = handler;
            foreach (var action in this.pending)
                action(handler);
            this.pending.Clear();
        }
    }


    protected void RaiseReceived(ReadOnlyMemory<byte> message) => this.Raise(x => x.Received(message));
    protected void RaiseFileProgress(string name, double fraction) => this.Raise(x => x.FileProgress(name, fraction));
    protected void RaiseFileReceived(string name, string? path, Exception? error) => this.Raise(x => x.FileReceived(name, path, error));

    protected void RaiseClosed()
    {
        lock (this.gate)
        {
            if (this.closed)
                return;

            this.closed = true;
        }
        this.Raise(x => x.Closed(), force: true);
    }

    protected bool IsClosed
    {
        get
        {
            lock (this.gate)
                return this.closed;
        }
    }


    void Raise(Action<IMultipeerConnectionHandler> action, bool force = false)
    {
        lock (this.gate)
        {
            if (this.closed && !force)
                return;

            if (this.handler == null)
                this.pending.Add(action);
            else
                action(this.handler);
        }
    }
}


/// <summary>
/// Where there is no MultipeerConnectivity
/// </summary>
internal sealed class UnsupportedMultipeerLink : IMultipeerLink
{
    public bool IsSupported => false;

    public IDisposable Advertise(string serviceType, string displayName, IReadOnlyDictionary<string, string> info, Action<IMultipeerConnection> accepted)
        => throw new PlatformNotSupportedException("MultipeerConnectivity is only available on iOS, Mac Catalyst and macOS");

    public IObservable<MultipeerSighting> Browse(string serviceType, string displayName)
        => throw new PlatformNotSupportedException("MultipeerConnectivity is only available on iOS, Mac Catalyst and macOS");

    public Task<IMultipeerConnection> Connect(object peer, TimeSpan timeout, CancellationToken cancellationToken)
        => throw new PlatformNotSupportedException("MultipeerConnectivity is only available on iOS, Mac Catalyst and macOS");
}
