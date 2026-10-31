namespace Shiny.UniversalHubs;

/// <summary>
/// One session with one remote peer over a peer-to-peer radio (Multipeer, Nearby). Messages are reliable and ordered.
/// </summary>
internal interface IPeerConnection : IDisposable
{
    string RemoteName { get; }

    /// <summary>
    /// Starts delivering to <paramref name="handler"/> - anything that arrived before is delivered first, in order
    /// </summary>
    void Start(IPeerConnectionHandler handler);

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
internal interface IPeerConnectionHandler
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
/// Holds what a session reports until <see cref="IPeerConnection.Start"/>, then passes it on in order. The radio links
/// (Apple Multipeer, Nearby) and the in-memory test networks build on it.
/// </summary>
internal abstract class PeerConnectionBase : IPeerConnection
{
    readonly object gate = new();
    readonly List<Action<IPeerConnectionHandler>> pending = new();
    IPeerConnectionHandler? handler;
    bool closed;


    public abstract string RemoteName { get; }
    public abstract void Send(byte[] message);
    public abstract Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken);
    public abstract void Dispose();


    public void Start(IPeerConnectionHandler handler)
    {
        lock (this.gate)
        {
            if (this.handler != null)
                throw new InvalidOperationException("A peer-link connection has one handler");

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


    void Raise(Action<IPeerConnectionHandler> action, bool force = false)
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
