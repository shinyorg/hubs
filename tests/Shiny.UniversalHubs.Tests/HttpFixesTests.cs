using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// The WebSocket peer link on its own: what it takes in as files, malformed frames, how it closes, and the client's
/// message limit
/// </summary>
public class HttpFixesTests
{
    const byte KindMessage = 1;
    const byte KindFileStart = 2;
    const byte KindFileChunk = 3;
    const byte KindFileEnd = 4;
    const byte KindFileFailed = 5;


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    static byte[] Frame(byte kind, string name, ReadOnlySpan<byte> body)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var frame = new byte[3 + nameBytes.Length + body.Length];
        frame[0] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)nameBytes.Length);
        nameBytes.CopyTo(frame, 3);
        body.CopyTo(frame.AsSpan(3 + nameBytes.Length));
        return frame;
    }

    static byte[] Start(string name, long size)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(body, size);
        return Frame(KindFileStart, name, body);
    }

    static byte[] Chunk(string name, int length) => Frame(KindFileChunk, name, new byte[length]);
    static byte[] End(string name) => Frame(KindFileEnd, name, ReadOnlySpan<byte>.Empty);


    static (WebSocketPeerConnection Connection, FakeFrames Frames, RecordingHandler Handler) Open(WebSocketFileLimits? limits = null)
    {
        var frames = new FakeFrames();
        var connection = new WebSocketPeerConnection("peer", frames, null, limits);
        var handler = new RecordingHandler();
        connection.Start(handler);
        return (connection, frames, handler);
    }


    [Fact]
    public async Task FileArrivesWhole()
    {
        var (connection, frames, handler) = Open();
        frames.Deliver(Start("f:1", 10));
        frames.Deliver(Chunk("f:1", 6));
        frames.Deliver(Chunk("f:1", 4));
        frames.Deliver(End("f:1"));

        await WaitFor(() => handler.Files.Count == 1);
        var (name, path, error) = handler.Files[0];
        Assert.Equal("f:1", name);
        Assert.Null(error);
        Assert.Equal(10, new FileInfo(path!).Length);
        File.Delete(path!);
        Assert.Equal(0, connection.IncomingFileCount);
        connection.Dispose();
    }


    [Fact]
    public async Task FileRunningPastItsSizeIsDropped()
    {
        var (connection, frames, handler) = Open();
        frames.Deliver(Start("f:1", 4));
        frames.Deliver(Chunk("f:1", 8));
        frames.Deliver(End("f:1"));

        await WaitFor(() => handler.Files.Count == 1);
        Assert.Null(handler.Files[0].Path);
        Assert.Contains("declared", handler.Files[0].Error!.Message);
        Assert.Equal(0, connection.IncomingFileCount);
        Assert.False(handler.IsClosed);
        connection.Dispose();
    }


    [Fact]
    public async Task FileEndingShortOfItsSizeFails()
    {
        var (connection, frames, handler) = Open();
        frames.Deliver(Start("f:1", 10));
        frames.Deliver(Chunk("f:1", 4));
        frames.Deliver(End("f:1"));

        await WaitFor(() => handler.Files.Count == 1);
        Assert.Null(handler.Files[0].Path);
        Assert.Contains("Expected 10 bytes but received 4", handler.Files[0].Error!.Message);
        Assert.Equal(0, connection.IncomingFileCount);
        connection.Dispose();
    }


    [Fact]
    public async Task FileOverTheLimitIsRefused()
    {
        var (connection, frames, handler) = Open(new WebSocketFileLimits(100, 4, null));
        frames.Deliver(Start("f:1", 101));
        frames.Deliver(Chunk("f:1", 101));

        await WaitFor(() => handler.Files.Count == 1);
        Assert.Contains("limited to 100 bytes", handler.Files[0].Error!.Message);
        Assert.Equal(0, connection.IncomingFileCount);
        Assert.False(handler.IsClosed);
        connection.Dispose();
    }


    [Fact]
    public async Task UnwantedFileIsRefused()
    {
        var (connection, frames, handler) = Open(new WebSocketFileLimits(null, 4, (_, _) => false));
        frames.Deliver(Start("f:1", 10));
        frames.Deliver(Chunk("f:1", 10));
        frames.Deliver(End("f:1"));

        await WaitFor(() => handler.Files.Count == 1);
        Assert.Contains("not expected", handler.Files[0].Error!.Message);
        Assert.Equal(0, connection.IncomingFileCount);
        connection.Dispose();
    }


    [Fact]
    public async Task OneFileTooManyAtOnceIsRefused()
    {
        var (connection, frames, handler) = Open(new WebSocketFileLimits(null, 2, null));
        frames.Deliver(Start("f:1", 10));
        frames.Deliver(Start("f:2", 10));
        frames.Deliver(Start("f:3", 10));

        await WaitFor(() => handler.Files.Count == 1);
        Assert.Equal("f:3", handler.Files[0].Name);
        Assert.Contains("At most 2", handler.Files[0].Error!.Message);
        Assert.Equal(2, connection.IncomingFileCount);

        // the open ones go when the connection does
        connection.Dispose();
        Assert.Equal(0, connection.IncomingFileCount);
    }


    public static TheoryData<byte[]> MalformedFrames => new()
    {
        new byte[] { KindFileStart },
        new byte[] { KindFileChunk, 0, 10, (byte)'a' },
        new byte[] { KindFileStart, 0, 1, (byte)'a', 0, 0, 0, 1 },
        Start("f:1", -1)
    };


    [Theory]
    [MemberData(nameof(MalformedFrames))]
    public async Task MalformedFrameClosesTheConnection(byte[] frame)
    {
        var (connection, frames, handler) = Open();
        frames.Deliver(frame);

        await WaitFor(() => handler.IsClosed);
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(handler.Files);
        Assert.Equal(0, connection.IncomingFileCount);
    }


    [Fact]
    public async Task ClosingSendsWhatIsQueuedThenTheCloseFrame()
    {
        var (connection, frames, _) = Open();
        frames.SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Send([1, 2, 3]);
        connection.Send([4, 5, 6]);
        connection.Dispose();
        frames.SendGate.SetResult();

        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, frames.Sent.Count);
        Assert.Equal([KindMessage, 4, 5, 6], frames.Sent[1]);

        // the close frame goes out before the receive is cancelled (which aborts a ClientWebSocket)
        Assert.Equal(["sent", "sent", "close", "receive-cancelled"], frames.Events);
    }


    [Fact]
    public async Task FileGivenUpWhileItsStartIsQueuedStillFails()
    {
        var (connection, frames, _) = Open();
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, new byte[10], TestContext.Current.CancellationToken);
        try
        {
            frames.SendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource();
            var sending = connection.SendFile(path, "f:1", null, cts.Token);
            await WaitFor(() => frames.Waiting > 0);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

            frames.SendGate.SetResult();
            await WaitFor(() => frames.Sent.Count == 2);
            Assert.Equal(KindFileStart, frames.Sent[0][0]);
            Assert.Equal(KindFileFailed, frames.Sent[1][0]);
        }
        finally
        {
            connection.Dispose();
            File.Delete(path);
        }
    }


    [Fact]
    public async Task ClientClosesOnAMessageOverTheLimit()
    {
        var socket = new FloodingWebSocket();
        var frames = new HttpClientTransport.ClientFrames(socket, null, 100 * 1024, null);

        var message = await frames.Receive(TestContext.Current.CancellationToken);

        Assert.Null(message);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWith);
        Assert.True(socket.Received <= 100 * 1024 + 16 * 1024);
    }


    [Fact]
    public async Task ClientReceivesWholeAndFragmentedMessages()
    {
        var socket = new FloodingWebSocket();
        socket.Script.Enqueue(([1, 2, 3], true));
        socket.Script.Enqueue(([4, 5], false));
        socket.Script.Enqueue(([6], true));
        var frames = new HttpClientTransport.ClientFrames(socket, null, 1024, null);

        Assert.Equal([1, 2, 3], await frames.Receive(TestContext.Current.CancellationToken));
        Assert.Equal([4, 5, 6], await frames.Receive(TestContext.Current.CancellationToken));
    }


    sealed class RecordingHandler : IPeerConnectionHandler
    {
        readonly List<(string Name, string? Path, Exception? Error)> files = new();
        volatile bool closed;

        public List<(string Name, string? Path, Exception? Error)> Files
        {
            get
            {
                lock (this.files)
                    return this.files.ToList();
            }
        }

        public bool IsClosed => this.closed;

        public void Received(ReadOnlyMemory<byte> message) { }
        public void FileProgress(string name, double fraction) { }

        public void FileReceived(string name, string? path, Exception? error)
        {
            lock (this.files)
                this.files.Add((name, path, error));
        }

        public void Closed() => this.closed = true;
    }


    sealed class FakeFrames : IWebSocketFrames
    {
        readonly Channel<byte[]> inbound = Channel.CreateUnbounded<byte[]>();
        readonly List<byte[]> sent = new();
        readonly List<string> events = new();
        int waiting;

        public TaskCompletionSource? SendGate { get; set; }
        public int Waiting => Volatile.Read(ref this.waiting);

        public List<byte[]> Sent
        {
            get
            {
                lock (this.sent)
                    return this.sent.ToList();
            }
        }

        public List<string> Events
        {
            get
            {
                lock (this.events)
                    return this.events.ToList();
            }
        }

        public void Deliver(byte[] frame) => this.inbound.Writer.TryWrite(frame);

        void Log(string entry)
        {
            lock (this.events)
                this.events.Add(entry);
        }

        public async ValueTask Send(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            if (this.SendGate is { } gate)
            {
                Interlocked.Increment(ref this.waiting);
                await gate.Task.WaitAsync(cancellationToken);
            }
            lock (this.sent)
                this.sent.Add(message.ToArray());
            this.Log("sent");
        }

        public async ValueTask<byte[]?> Receive(CancellationToken cancellationToken)
        {
            try
            {
                return await this.inbound.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                this.Log("receive-cancelled");
                throw;
            }
        }

        public ValueTask Close(CancellationToken cancellationToken)
        {
            this.Log("close");
            return ValueTask.CompletedTask;
        }
    }


    /// <summary>
    /// Plays its script, then sends one message that never ends, 16 KB at a time
    /// </summary>
    sealed class FloodingWebSocket : WebSocket
    {
        WebSocketState state = WebSocketState.Open;

        public Queue<(byte[] Data, bool EndOfMessage)> Script { get; } = new();
        public WebSocketCloseStatus? ClosedWith { get; private set; }
        public long Received { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => this.ClosedWith;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => this.state;
        public override string? SubProtocol => null;

        public override void Abort() => this.state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => this.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.ClosedWith = closeStatus;
            this.state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => this.state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (this.Script.TryDequeue(out var next))
            {
                next.Data.CopyTo(buffer.AsSpan());
                return Task.FromResult(new WebSocketReceiveResult(next.Data.Length, WebSocketMessageType.Binary, next.EndOfMessage));
            }

            var count = Math.Min(16 * 1024, buffer.Count);
            this.Received += count;
            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Binary, false));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
