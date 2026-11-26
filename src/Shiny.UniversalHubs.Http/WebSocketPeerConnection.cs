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

    ValueTask Close();
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
/// </summary>
internal sealed class WebSocketPeerConnection : PeerConnectionBase
{
    internal const int FileChunkSize = 32 * 1024;

    const byte KindMessage = 1;
    const byte KindFileStart = 2;
    const byte KindFileChunk = 3;
    const byte KindFileEnd = 4;
    const byte KindFileFailed = 5;

    readonly IWebSocketFrames socket;
    readonly ILogger? logger;
    readonly Channel<(byte[] Frame, TaskCompletionSource? Sent)> outgoing = Channel.CreateUnbounded<(byte[], TaskCompletionSource?)>(new UnboundedChannelOptions { SingleReader = true });
    readonly CancellationTokenSource lifetime = new();
    readonly Dictionary<string, IncomingFile> incoming = new(StringComparer.Ordinal);
    int disposed;


    public WebSocketPeerConnection(string remoteName, IWebSocketFrames socket, ILogger? logger)
    {
        this.RemoteName = remoteName;
        this.socket = socket;
        this.logger = logger;
        this.Completion = Task.WhenAll(Task.Run(this.SendLoop), Task.Run(this.ReceiveLoop));
    }


    public override string RemoteName { get; }

    /// <summary>
    /// Completes once both directions have stopped - the host keeps its request open until then
    /// </summary>
    public Task Completion { get; }


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
        await this.SendAndWait(start, cancellationToken).ConfigureAwait(false);

        try
        {
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
            await foreach (var (frame, sent) in this.outgoing.Reader.ReadAllAsync(this.lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await this.socket.Send(frame, this.lifetime.Token).ConfigureAwait(false);
                    sent?.TrySetResult();
                }
                catch (Exception ex)
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
            while (this.outgoing.Reader.TryRead(out var left))
                left.Sent?.TrySetException(new HubDisconnectedException("The connection has closed"));
            this.Dispose();
        }
    }


    async Task ReceiveLoop()
    {
        try
        {
            while (!this.lifetime.IsCancellationRequested)
            {
                var frame = await this.socket.Receive(this.lifetime.Token).ConfigureAwait(false);
                if (frame == null || frame.Length == 0)
                    break;

                this.Dispatch(frame);
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


    void Dispatch(byte[] frame)
    {
        if (frame[0] == KindMessage)
        {
            this.RaiseReceived(frame.AsMemory(1));
            return;
        }

        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(1));
        var name = Encoding.UTF8.GetString(frame, 3, nameLength);
        var body = frame.AsMemory(3 + nameLength);

        switch (frame[0])
        {
            case KindFileStart:
                var size = BinaryPrimitives.ReadInt64BigEndian(body.Span);
                var path = Path.Combine(Path.GetTempPath(), "uhubs-ws-" + Guid.NewGuid().ToString("N"));
                lock (this.incoming)
                {
                    if (this.incoming.Remove(name, out var replaced))
                        replaced.Abandon();
                    this.incoming[name] = new IncomingFile(path, size, File.Create(path));
                }
                this.RaiseFileProgress(name, 0);
                break;

            case KindFileChunk:
                IncomingFile? file;
                lock (this.incoming)
                    this.incoming.TryGetValue(name, out file);

                if (file != null)
                {
                    file.Stream.Write(body.Span);
                    file.Received += body.Length;
                    this.RaiseFileProgress(name, file.Size == 0 ? 1 : (double)file.Received / file.Size);
                }
                break;

            case KindFileEnd:
                IncomingFile? done;
                lock (this.incoming)
                    this.incoming.Remove(name, out done);

                if (done != null)
                {
                    done.Stream.Dispose();
                    this.RaiseFileReceived(name, done.Path, null);
                }
                break;

            case KindFileFailed:
                IncomingFile? failed;
                lock (this.incoming)
                    this.incoming.Remove(name, out failed);

                failed?.Abandon();
                this.RaiseFileReceived(name, null, new HubException($"The file transfer failed: {Encoding.UTF8.GetString(body.Span)}"));
                break;
        }
    }


    public override void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

        this.outgoing.Writer.TryComplete();
        this.lifetime.Cancel();
        _ = this.socket.Close().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);

        List<IncomingFile> partial;
        lock (this.incoming)
        {
            partial = this.incoming.Values.ToList();
            this.incoming.Clear();
        }
        foreach (var file in partial)
            file.Abandon();

        this.RaiseClosed();
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
