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
/// <remarks>
/// The handler never runs under the gate: whoever raises while nobody is delivering delivers everything queued, in order,
/// outside the lock. A handler that sends to another connection (a hub pushing to its other clients) can't deadlock with
/// that connection's handler sending back, and <see cref="IsClosed"/> never waits on a handler.
/// </remarks>
internal abstract class PeerConnectionBase : IPeerConnection
{
    readonly object gate = new();
    readonly Queue<Action<IPeerConnectionHandler>> pending = new();
    IPeerConnectionHandler? handler;
    bool delivering;
    int closed;


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
        }
        this.Deliver();
    }


    protected void RaiseReceived(ReadOnlyMemory<byte> message) => this.Raise(x => x.Received(message));
    protected void RaiseFileProgress(string name, double fraction) => this.Raise(x => x.FileProgress(name, fraction));
    protected void RaiseFileReceived(string name, string? path, Exception? error) => this.Raise(x => x.FileReceived(name, path, error));

    protected void RaiseClosed()
    {
        if (Interlocked.Exchange(ref this.closed, 1) == 1)
            return;

        // queued after everything raised before it, and nothing is queued after it
        this.Raise(x => x.Closed(), force: true);
    }

    protected bool IsClosed => Volatile.Read(ref this.closed) == 1;


    void Raise(Action<IPeerConnectionHandler> action, bool force = false)
    {
        lock (this.gate)
        {
            if (this.IsClosed && !force)
                return;

            this.pending.Enqueue(action);
        }
        this.Deliver();
    }


    /// <summary>
    /// Runs what is queued, in order, outside the gate - unless another thread is delivering already (it runs this too) or
    /// there's no handler yet
    /// </summary>
    void Deliver()
    {
        IPeerConnectionHandler target;
        lock (this.gate)
        {
            if (this.delivering || this.handler == null)
                return;

            this.delivering = true;
            target = this.handler;
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var more = true;
        while (more)
        {
            Action<IPeerConnectionHandler>? next = null;
            lock (this.gate)
            {
                if (this.pending.Count == 0)
                {
                    this.delivering = false;
                    more = false;
                }
                else
                {
                    next = this.pending.Dequeue();
                }
            }

            if (next != null)
            {
                try
                {
                    next(target);
                }
                catch (Exception ex)
                {
                    // the rest still goes out (Closed above all) - the raiser sees the first failure, as it did before
                    failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                }
            }
        }
        failure?.Throw();
    }
}
