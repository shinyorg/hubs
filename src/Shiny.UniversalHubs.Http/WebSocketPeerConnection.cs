using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Shiny.UniversalHubs;

/// <summary>
/// One WebSocket, as either side sees it - the host's server socket or the client's ClientWebSocket
/// </summary>
internal interface IWebSocketFrames
{
    /// <summary>
    /// Sends one binary message
    /// </summary>
    ValueTask Send(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    /// <summary>
    /// The next binary message, or null once the socket has closed
    /// </summary>
    ValueTask<byte[]?> Receive(CancellationToken cancellationToken);

    /// <summary>
    /// Sends the close frame and lets the socket go
    /// </summary>
    ValueTask Close(CancellationToken cancellationToken);
}


/// <summary>
/// What a connection takes in as files from the other side
/// </summary>
/// <param name="MaxFileSize">The largest file accepted, by its declared size - null for no limit</param>
/// <param name="MaxOpenFiles">How many files may be arriving at once</param>
/// <param name="Accept">Whether a file (name, declared size) is wanted at all - null takes any</param>
internal sealed record WebSocketFileLimits(long? MaxFileSize, int MaxOpenFiles, Func<string, long, bool>? Accept)
{
    public const int DefaultMaxOpenFiles = 16;

    public static WebSocketFileLimits Default { get; } = new(null, DefaultMaxOpenFiles, null);
}


/// <summary>
/// The peer link over one WebSocket: the same hub session as Multipeer and Nearby (<see cref="PeerHostSession"/> on the
/// host, PeerHubTransport on the client), with files streamed in chunks beside the messages.
/// <code>
/// [1][message]                                   a peer-link message
/// [2][u16 name length][name][i64 size]           a file starts
/// [3][u16 name length][name][bytes]              the next part of it
/// [4][u16 name length][name]                     it is complete
/// [5][u16 name length][name][reason]             the sender gave up on it
/// </code>
/// Everything one side sends goes out in order, from one loop; a file's parts are sent one at a time, so messages
/// interleave with them rather than waiting for the whole file.
/// <para>
/// A frame that can't be read closes the connection. A file that isn't wanted, is larger than allowed, is one too many
/// at once, or runs past its declared size is refused: its temp file is dropped, the receiving side hears it failed and
/// the rest of its parts are ignored.
/// </para>
/// </summary>
internal sealed class WebSocketPeerConnection : PeerConnectionBase
{
    internal const int FileChunkSize = 32 * 1024;

    /// <summary>
    /// How long a closing connection gets to send what it has queued, and then its close frame
    /// </summary>
    internal static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    const byte KindMessage = 1;
    const byte KindFileStart = 2;
    const byte KindFileChunk = 3;
    const byte KindFileEnd = 4;
    const byte KindFileFailed = 5;

    readonly IWebSocketFrames socket;
    readonly ILogger? logger;
    readonly WebSocketFileLimits fileLimits;
    readonly Channel<(byte[] Frame, TaskCompletionSource? Sent)> outgoing = Channel.CreateUnbounded<(byte[], TaskCompletionSource?)>(new UnboundedChannelOptions { SingleReader = true });
    // sending and receiving stop separately: a closing connection sends what it has queued and its close frame before it
    // stops reading - cancelling a ClientWebSocket's receive aborts the socket
    readonly CancellationTokenSource sending = new();
    readonly CancellationTokenSource receiving = new();
    readonly TaskCompletionSource sendStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource shutDown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<string, IncomingFile> incoming = new(StringComparer.Ordinal);
    int disposed;


    public WebSocketPeerConnection(string remoteName, IWebSocketFrames socket, ILogger? logger, WebSocketFileLimits? fileLimits = null)
    {
        this.RemoteName = remoteName;
        this.socket = socket;
        this.logger = logger;
        this.fileLimits = fileLimits ?? WebSocketFileLimits.Default;
        this.Completion = Task.WhenAll(Task.Run(this.SendLoop), Task.Run(this.ReceiveLoop), this.shutDown.Task);
    }


    public override string RemoteName { get; }

    /// <summary>
    /// Completes once both directions have stopped and the socket is closed - the host keeps its request open until then
    /// </summary>
    public Task Completion { get; }

    /// <summary>
    /// Files arriving right now
    /// </summary>
    internal int IncomingFileCount
    {
        get
        {
            lock (this.incoming)
                return this.incoming.Count;
        }
    }


    public override void Send(byte[] message)
    {
        if (this.IsClosed)
            throw new HubDisconnectedException("The connection has closed");

        var frame = new byte[message.Length + 1];
        frame[0] = KindMessage;
        message.CopyTo(frame, 1);
        if (!this.outgoing.Writer.TryWrite((frame, null)))
            throw new HubDisconnectedException("The connection has closed");
    }


    public override async Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        var size = file.Length;
        var start = Header(KindFileStart, name, 8);
        BinaryPrimitives.WriteInt64BigEndian(start.AsSpan(start.Length - 8), size);

        try
        {
            // inside: a start that is queued when the caller gives up still goes out, so the other side must hear it failed
            await this.SendAndWait(start, cancellationToken).ConfigureAwait(false);

            var buffer = new byte[FileChunkSize];
            long sent = 0;
            int read;
            while ((read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var chunk = Header(KindFileChunk, name, read);
                buffer.AsSpan(0, read).CopyTo(chunk.AsSpan(chunk.Length - read));
                await this.SendAndWait(chunk, cancellationToken).ConfigureAwait(false);
                sent += read;
                progress?.Invoke(size == 0 ? 1 : (double)sent / size);
            }
            await this.SendAndWait(Header(KindFileEnd, name, 0), CancellationToken.None).ConfigureAwait(false);
            progress?.Invoke(1);
        }
        catch (Exception ex) when (!this.IsClosed)
        {
            var reason = Encoding.UTF8.GetBytes(ex.Message);
            var failed = Header(KindFileFailed, name, reason.Length);
            reason.CopyTo(failed.AsSpan(failed.Length - reason.Length));
            this.outgoing.Writer.TryWrite((failed, null));
            throw;
        }
    }


    async Task SendAndWait(byte[] frame, CancellationToken cancellationToken)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!this.outgoing.Writer.TryWrite((frame, sent)))
            throw new HubDisconnectedException("The connection has closed");

        await sent.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }


    static byte[] Header(byte kind, string name, int extra)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var frame = new byte[1 + 2 + nameBytes.Length + extra];
        frame[0] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)nameBytes.Length);
        nameBytes.CopyTo(frame, 3);
        return frame;
    }


    async Task SendLoop()
    {
        try
        {
            // runs until the queue is complete and empty - a closing connection still sends what was queued before it
            await foreach (var (frame, sent) in this.outgoing.Reader.ReadAllAsync(this.sending.Token).ConfigureAwait(false))
            {
                try
                {
                    await this.socket.Send(frame, this.sending.Token).ConfigureAwait(false);
                    sent?.TrySetResult();
                }
                catch
                {
                    sent?.TrySetException(new HubDisconnectedException("The connection has closed"));
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex, "WebSocket send to {Peer} stopped", this.RemoteName);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // anything still waiting will never go out
            this.outgoing.Writer.TryComplete();
            while (this.outgoing.Reader.TryRead(out var left))
                left.Sent?.TrySetException(new HubDisconnectedException("The connection has closed"));

            this.sendStopped.TrySetResult();
            this.Dispose();
        }
    }


    async Task ReceiveLoop()
    {
        try
        {
            var open = true;
            while (open && !this.receiving.IsCancellationRequested)
            {
                var frame = await this.socket.Receive(this.receiving.Token).ConfigureAwait(false);
                open = frame is { Length: > 0 } && this.Dispatch(frame);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex, "WebSocket receive from {Peer} stopped", this.RemoteName);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            this.Dispose();
        }
    }


    /// <summary>
    /// Takes in one frame - false when it can't be read, which ends the connection
    /// </summary>
    bool Dispatch(byte[] frame)
    {
        if (frame[0] == KindMessage)
        {
            this.RaiseReceived(frame.AsMemory(1));
            return true;
        }

        if (frame.Length < 3)
            return this.Malformed(frame[0], "is too short for a file name");

        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1));
        if (frame.Length < 3 + nameLength)
            return this.Malformed(frame[0], "is shorter than its file name");

        var name = Encoding.UTF8.GetString(frame, 3, nameLength);
        var body = frame.AsMemory(3 + nameLength);

        switch (frame[0])
        {
            case KindFileStart:
                if (body.Length != 8)
                    return this.Malformed(frame[0], "has no file size");

                var size = BinaryPrimitives.ReadInt64BigEndian(body.Span);
                if (size < 0)
                    return this.Malformed(frame[0], "has a negative file size");

                this.StartFile(name, size);
                return true;

            case KindFileChunk:
                this.WriteFile(name, body);
                return true;

            case KindFileEnd:
                this.EndFile(name);
                return true;

            case KindFileFailed:
                IncomingFile? failed;
                lock (this.incoming)
                    this.incoming.Remove(name, out failed);

                failed?.Abandon();
                this.RaiseFileReceived(name, null, new HubException($"The file transfer failed: {Encoding.UTF8.GetString(body.Span)}"));
                return true;

            default:
                this.logger?.LogDebug("Ignoring WebSocket frame kind {Kind} from {Peer}", frame[0], this.RemoteName);
                return true;
        }
    }


    bool Malformed(byte kind, string problem)
    {
        this.logger?.LogWarning("Closing the WebSocket from {Peer}: a frame of kind {Kind} {Problem}", this.RemoteName, kind, problem);
        return false;
    }


    void StartFile(string name, long size)
    {
        string? refusal = null;
        if (this.fileLimits.Accept is { } accept && !accept(name, size))
            refusal = "The file was not expected";
        else if (this.fileLimits.MaxFileSize is { } max && size > max)
            refusal = $"Files are limited to {max} bytes";

        lock (this.incoming)
        {
            // the same name again starts it over
            if (this.incoming.Remove(name, out var replaced))
                replaced.Abandon();

            if (refusal == null && this.incoming.Count >= this.fileLimits.MaxOpenFiles)
                refusal = $"At most {this.fileLimits.MaxOpenFiles} files may arrive at once";

            if (refusal == null)
                refusal = this.CreateFile(name, size);
        }

        if (refusal == null)
        {
            this.RaiseFileProgress(name, 0);
        }
        else
        {
            this.logger?.LogInformation("Refused file {Name} ({Size} bytes) from {Peer}: {Reason}", name, size, this.RemoteName, refusal);
            this.RaiseFileReceived(name, null, new HubException(refusal));
        }
    }


    // under the incoming lock
    string? CreateFile(string name, long size)
    {
        var path = Path.Combine(Path.GetTempPath(), "uhubs-ws-" + Guid.NewGuid().ToString("N"));
        try
        {
            this.incoming[name] = new IncomingFile(path, size, File.Create(path));
            return null;
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Could not create a temp file for {Name}", name);
            return $"The file could not be stored: {ex.Message}";
        }
    }


    void WriteFile(string name, ReadOnlyMemory<byte> body)
    {
        IncomingFile? file;
        lock (this.incoming)
            this.incoming.TryGetValue(name, out file);

        // refused, failed or finished - its late parts go nowhere
        if (file == null)
            return;

        string? refusal = null;
        if (file.Received + body.Length > file.Size)
        {
            refusal = $"The file ran past its declared {file.Size} bytes";
        }
        else
        {
            try
            {
                file.Stream.Write(body.Span);
                file.Received += body.Length;
            }
            catch (Exception ex)
            {
                refusal = $"The file could not be stored: {ex.Message}";
            }
        }

        if (refusal == null)
        {
            this.RaiseFileProgress(name, file.Size == 0 ? 1 : (double)file.Received / file.Size);
            return;
        }

        this.Drop(name, file, refusal);
    }


    void EndFile(string name)
    {
        IncomingFile? done;
        lock (this.incoming)
        {
            this.incoming.TryGetValue(name, out done);
            if (done != null && done.Received == done.Size)
                this.incoming.Remove(name);
        }

        if (done == null)
            return;

        if (done.Received != done.Size)
        {
            this.Drop(name, done, $"Expected {done.Size} bytes but received {done.Received}");
            return;
        }

        try
        {
            done.Stream.Dispose();
            this.RaiseFileReceived(name, done.Path, null);
        }
        catch (Exception ex)
        {
            done.Abandon();
            this.RaiseFileReceived(name, null, new HubException($"The file could not be stored: {ex.Message}"));
        }
    }


    void Drop(string name, IncomingFile file, string reason)
    {
        lock (this.incoming)
        {
            // only this transfer - a start under the same name since then is another one
            if (this.incoming.TryGetValue(name, out var current) && ReferenceEquals(current, file))
                this.incoming.Remove(name);
        }

        file.Abandon();
        this.logger?.LogInformation("Dropped file {Name} from {Peer}: {Reason}", name, this.RemoteName, reason);
        this.RaiseFileReceived(name, null, new HubException(reason));
    }


    public override void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

        // nothing more is queued - what already is still goes out, ahead of the close frame
        this.outgoing.Writer.TryComplete();

        List<IncomingFile> partial;
        lock (this.incoming)
        {
            partial = this.incoming.Values.ToList();
            this.incoming.Clear();
        }
        foreach (var file in partial)
            file.Abandon();

        this.RaiseClosed();
        _ = Task.Run(this.Shutdown);
    }


    async Task Shutdown()
    {
        try
        {
            await this.sendStopped.Task.WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            this.logger?.LogDebug("WebSocket to {Peer} did not drain in time", this.RemoteName);
        }
        this.sending.Cancel();

        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            await this.socket.Close(timeout.Token).AsTask().WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "WebSocket to {Peer} did not close cleanly", this.RemoteName);
        }
        finally
        {
            this.receiving.Cancel();
            this.shutDown.TrySetResult();
        }
    }


    sealed class IncomingFile(string path, long size, FileStream stream)
    {
        public string Path => path;
        public long Size => size;
        public FileStream Stream => stream;
        public long Received { get; set; }

        public void Abandon()
        {
            try
            {
                stream.Dispose();
                File.Delete(path);
            }
            catch
            {
                // a temp file - nothing more to do
            }
        }
    }
}
