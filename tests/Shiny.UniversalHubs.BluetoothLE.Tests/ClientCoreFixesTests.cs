using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// HubProxy's connection lifecycle against a scripted transport (connect while connected, a link closing or the app
/// disconnecting during the handshake, push ordering across a reconnect), and the core codecs' bounds
/// </summary>
public class ClientCoreFixesTests
{
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

    static ClientCoreFixesTests() => Json.AddContext(TestJsonContext.Default);

    readonly IHubSerializer serializer = new JsonHubSerializer();
    readonly HubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(10) };


    HubProxy CreateClient() => HubProxyFactories.Create<ITestHub>(new HubClientServices(this.options, this.serializer), CharacteristicUuid);


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Condition was not met in time");
            await Task.Delay(20);
        }
    }


    static async Task<ScriptedTransport> Connect(HubProxy client, Func<ScriptedTransport, Task<HandshakeAck>>? handshake = null)
    {
        ScriptedTransport? transport = null;
        await client.ConnectExternal(events => transport = new ScriptedTransport(events, handshake));
        return transport!;
    }


    [Fact]
    public async Task ConnectingWhileConnectedLeavesTheLiveConnectionAlone()
    {
        var client = this.CreateClient();
        var disconnects = 0;
        client.Disconnected += (_, _) => disconnects++;
        var live = await Connect(client);

        ScriptedTransport? second = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectExternal(events => second = new ScriptedTransport(events)));

        Assert.Null(second);
        Assert.Equal(HubClientStatus.Connected, client.Status);
        Assert.Same(live, client.ConnectionTransport);
        Assert.Equal(0, live.CloseCalls);
        Assert.Equal(0, disconnects);

        await ((ITestHub)client).Record("still here");
        Assert.Equal(["Record"], live.Invoked);
    }


    [Fact]
    public async Task ALinkClosingBeforeTheHandshakeReplyIsSeenFailsTheConnect()
    {
        var client = this.CreateClient();
        var connected = 0;
        var disconnected = 0;
        client.Connected += (_, _) => connected++;
        client.Disconnected += (_, _) => disconnected++;

        // the reply is in, but the link closes before the connect carries on with it
        var ex = await Assert.ThrowsAsync<HubDisconnectedException>(() => Connect(client, t =>
        {
            t.Events.Closed(new HubDisconnect(HubDisconnectReason.ClientTimeout, "link lost"));
            return Task.FromResult(new HandshakeAck(true, null, "Host", 0, false));
        }));

        Assert.Equal(HubDisconnectReason.ClientTimeout, ex.Disconnect?.Reason);
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
        Assert.Null(client.ConnectionTransport);
        Assert.Equal(0, connected);
        Assert.Equal(0, disconnected);

        // not wedged: it connects again, and disconnects
        var next = await Connect(client);
        Assert.Equal(HubClientStatus.Connected, client.Status);
        await client.Disconnect();
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
        Assert.Equal(1, next.CloseCalls);
        Assert.Equal(1, connected);
        Assert.Equal(1, disconnected);
    }


    [Fact]
    public async Task DisconnectingDuringTheHandshakeFailsTheConnect()
    {
        var client = this.CreateClient();
        var connected = 0;
        var disconnected = 0;
        client.Connected += (_, _) => connected++;
        client.Disconnected += (_, _) => disconnected++;
        ScriptedTransport? transport = null;

        var ex = await Assert.ThrowsAsync<HubDisconnectedException>(() => client.ConnectExternal(events => transport = new ScriptedTransport(events, async _ =>
        {
            await client.Disconnect();
            return new HandshakeAck(true, null, "Host", 0, false);
        })));

        Assert.Equal(HubDisconnectReason.ClientDisconnect, ex.Disconnect?.Reason);
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
        Assert.Equal(1, transport!.CloseCalls);
        Assert.Equal(0, connected);
        Assert.Equal(0, disconnected);
    }


    [Fact]
    public async Task AHandshakeThatFailsBecauseTheAppDisconnectedSaysSo()
    {
        var client = this.CreateClient();

        var ex = await Assert.ThrowsAsync<HubDisconnectedException>(() => Connect(client, async _ =>
        {
            await client.Disconnect();
            throw new IOException("link torn down");
        }));

        Assert.Equal(HubDisconnectReason.ClientDisconnect, ex.Disconnect?.Reason);
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
    }


    [Fact]
    public async Task PushesFromAnOldConnectionFinishBeforeTheNextConnectionsStart()
    {
        var client = this.CreateClient();
        var hub = (ITestHub)client;
        var gate = new TaskCompletionSource();
        var raised = new List<string>();
        var running = 0;
        var overlapped = false;
        hub.Message += message =>
        {
            if (Interlocked.Increment(ref running) > 1)
                overlapped = true;

            lock (raised)
                raised.Add(message);

            // the first push holds the pump, so the old connection still has a backlog when the new one starts
            if (message == "a1")
                gate.Task.Wait(TestContext.Current.CancellationToken);

            Interlocked.Decrement(ref running);
        };

        var first = await Connect(client);
        first.Push(this.serializer, "a1");
        first.Push(this.serializer, "a2");
        await WaitFor(() => Volatile.Read(ref running) == 1);

        first.Events.Closed(new HubDisconnect(HubDisconnectReason.ClientTimeout));
        await WaitFor(() => client.Status == HubClientStatus.Disconnected);

        var second = await Connect(client);
        second.Push(this.serializer, "b1");
        gate.SetResult();

        await WaitFor(() =>
        {
            lock (raised)
                return raised.Count == 3;
        });
        Assert.Equal(["a1", "a2", "b1"], raised);
        Assert.False(overlapped);
    }


    [Theory]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0x7FFFFFFCu)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x80000000u)]
    public void AHugeArgumentLengthIsAProtocolError(uint length)
    {
        var data = new byte[1 + 4 + 3];
        data[0] = 1;
        BitConverter.TryWriteBytes(data.AsSpan(1), length);

        var reader = new HubArgumentReader(this.serializer, data);
        Assert.Throws<HubProtocolException>(() => reader.ReadSerialized());
    }


    [Fact]
    public void AHugeSecondArgumentLengthIsAProtocolError()
    {
        // a valid first argument moves the offset along, so offset + length overflows sooner
        var first = new byte[10];
        var data = new byte[1 + 4 + first.Length + 4];
        data[0] = 2;
        BitConverter.TryWriteBytes(data.AsSpan(1), (uint)first.Length);
        BitConverter.TryWriteBytes(data.AsSpan(1 + 4 + first.Length), int.MaxValue - 10u);

        var reader = new HubArgumentReader(this.serializer, data);
        Assert.Equal(first.Length, reader.ReadSerialized().Length);
        Assert.Throws<HubProtocolException>(() => reader.ReadSerialized());
    }


    [Fact]
    public void ATruncatedArgumentHeaderIsAProtocolError()
    {
        var reader = new HubArgumentReader(this.serializer, new byte[] { 1, 0, 0 });
        Assert.Throws<HubProtocolException>(() => reader.ReadSerialized());
    }


    [Fact]
    public async Task UnreadMediaPacketsAreBoundedKeepingTheNewest()
    {
        var channel = new FramedMediaChannel(16, (_, _) => Task.CompletedTask, null);
        const int sent = FramedMediaChannel.InboundCapacity * 4;
        for (var i = 0; i < sent; i++)
        {
            foreach (var write in MediaFrameCodec.Encode(BitConverter.GetBytes(i)))
                channel.OnData(write);
        }
        channel.Close();

        var received = new List<int>();
        await foreach (var packet in channel.Receive(TestContext.Current.CancellationToken))
            received.Add(BitConverter.ToInt32(packet.Span));

        Assert.Equal(Enumerable.Range(sent - FramedMediaChannel.InboundCapacity, FramedMediaChannel.InboundCapacity), received);
    }


    sealed class ScriptedTransport(IHubConnectionTransportEvents events, Func<ScriptedTransport, Task<HandshakeAck>>? handshake = null) : IHubConnectionTransport
    {
        public IHubConnectionTransportEvents Events => events;
        public List<string> Invoked { get; } = new();
        public int CloseCalls;

        public void Push(IHubSerializer serializer, string message)
            => events.Pushed("Message", new HubArgumentWriter(serializer).Write(message).ToArray());

        public Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken)
            => handshake?.Invoke(this) ?? Task.FromResult(new HandshakeAck(true, null, "Host", 0, false));

        public Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken)
        {
            lock (this.Invoked)
                this.Invoked.Add(method);
            return Task.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public bool CanTransferFiles => false;

        public Task<HubFileTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<HubTransferProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<HubFileTransferResult> Download(string remoteFileName, Stream destination, IProgress<HubTransferProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task Close(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref this.CloseCalls);
            return Task.CompletedTask;
        }
    }
}
