using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Testing;
using Shiny.UniversalHubs;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs.Tests;


[UniversalHubContract]
public interface IAbortingHub
{
    IAsyncEnumerable<int> CountThenAbort(int to, CancellationToken cancellationToken);
    IAsyncEnumerable<int> AbortThenFail(CancellationToken cancellationToken);
}


public class AbortingHub : UniversalHub<IAbortingHub>
{
    public async IAsyncEnumerable<int> CountThenAbort(int to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= to; i++)
        {
            await Task.Yield();
            yield return i;
        }
        this.Context.Abort("done");
    }


    public async IAsyncEnumerable<int> AbortThenFail([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return 1;
        this.Context.Abort("failed");
        throw new InvalidOperationException("after abort");
    }
}


/// <summary>
/// Host-side fixes: the peer link's delivery (no handler under the gate), a stream's Context.Abort after its StreamEnd,
/// peer-session edge cases (a resume that lands on a closed session, a malformed goodbye) and media channel bounds
/// </summary>
public class HostFixesTests : IAsyncLifetime
{
    const string HubName = "aborting";
    const string ServiceType = "fixes-mc";

    static HostFixesTests() => Json.AddContext(TestJsonContext.Default);

    readonly FakeMultipeerNetwork network = new();
    TestHttpServer app = null!;
    UniversalHubHost host = null!;


    public async ValueTask InitializeAsync()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Services.AddSingleton(NoBluetoothHosting.Create());
        builder.Services.AddSingleton<IMultipeerLink>(this.network);
        builder.Services.AddUniversalServer(server => server
            .Configure(o => o.LocalName = "TestHost")
            .Multipeer(ServiceType)
            .AddHub<AbortingHub>(HubName, h => h.UseMultipeer())
        );
        this.app = TestHttpServer.For(builder.Build());
        this.host = this.app.Services.GetRequiredService<UniversalHubHost>();
        await this.host.Start();
    }


    public async ValueTask DisposeAsync() => await this.app.DisposeAsync();


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    // ---- the peer link's delivery ----

    [Fact]
    public async Task HandlersSendingToEachOtherDoNotDeadlock()
    {
        // the hub path: A's Received pushes to B (B.Send checks B.IsClosed) while B's Received pushes to A
        var a = new TestConnection();
        var b = new TestConnection();
        using var bothInside = new Barrier(2);
        var aSawB = false;
        var bSawA = false;
        a.Start(new CallbackHandler(_ =>
        {
            bothInside.SignalAndWait(TimeSpan.FromSeconds(5));
            aSawB = !b.Closed;
        }));
        b.Start(new CallbackHandler(_ =>
        {
            bothInside.SignalAndWait(TimeSpan.FromSeconds(5));
            bSawA = !a.Closed;
        }));

        var one = Task.Run(() => a.Deliver([1]));
        var two = Task.Run(() => b.Deliver([2]));
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(aSawB);
        Assert.True(bSawA);
    }


    [Fact]
    public async Task DeliveryStaysInOrderOneAtATimeWithClosedLastAndOnce()
    {
        var connection = new TestConnection();
        var seen = new List<string>();
        var inside = 0;
        var overlapped = false;
        var handler = new CallbackHandler(
            message =>
            {
                if (Interlocked.Increment(ref inside) > 1)
                    overlapped = true;

                var text = System.Text.Encoding.UTF8.GetString(message.Span);
                lock (seen)
                    seen.Add(text);

                // raised from inside the handler - runs after this one returns, not nested in it
                if (text == "early-1")
                    connection.Deliver("nested"u8.ToArray());

                Interlocked.Decrement(ref inside);
            },
            () =>
            {
                lock (seen)
                    seen.Add("closed");
            }
        );

        // before Start: held, then delivered first and in order
        connection.Deliver("early-0"u8.ToArray());
        connection.Deliver("early-1"u8.ToArray());
        connection.Start(handler);

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                connection.Deliver(System.Text.Encoding.UTF8.GetBytes($"w{w}-{i}"));
        })).ToArray();
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        connection.Dispose();
        connection.Dispose();
        connection.Deliver("late"u8.ToArray());

        List<string> result;
        lock (seen)
            result = seen.ToList();

        Assert.False(overlapped);
        Assert.Equal(["early-0", "early-1", "nested"], result.Take(3));
        Assert.Equal("closed", result[^1]);
        Assert.Single(result, x => x == "closed");
        Assert.DoesNotContain("late", result);
        for (var w = 0; w < 4; w++)
        {
            var prefix = $"w{w}-";
            Assert.Equal(Enumerable.Range(0, 200).Select(i => prefix + i), result.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }


    // ---- a stream's Context.Abort ----

    [Fact]
    public async Task AStreamThatAbortsEndsBeforeTheDisconnect()
    {
        var raw = await RawSession.Open(this.network);
        await raw.Handshake();

        raw.Send(PeerProtocol.Encode(PeerMessageKind.Stream, 2, nameof(IAbortingHub.CountThenAbort), new HubArgumentWriter(new JsonHubSerializer()).Write(3).ToArray()));

        var kinds = new List<PeerMessageKind>();
        for (var i = 0; i < 5; i++)
            kinds.Add((await raw.Next()).Kind);

        Assert.Equal(
            [PeerMessageKind.StreamItem, PeerMessageKind.StreamItem, PeerMessageKind.StreamItem, PeerMessageKind.StreamEnd, PeerMessageKind.Disconnect],
            kinds
        );
    }


    [Fact]
    public async Task AClientSeesAStreamThatAbortsCompleteBeforeItIsDisconnected()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IMultipeerLink>(this.network)
            .AddUniversalHubClient<IAbortingHub>(HubName, o => o.UseMultipeer(ServiceType, mc => mc.DisplayName = "Phone"))
            .BuildServiceProvider();
        var client = services.GetRequiredService<IUniversalHubClient<IAbortingHub>>();
        var found = await client
            .DiscoverAll([typeof(MultipeerClientTransport)])
            .Where(x => x.IsSeenOver<MultipeerClientTransport>())
            .FirstAsync()
            .ToTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await client.Connect(found, new HubConnectOptions("Mia").Only<MultipeerClientTransport>(), TestContext.Current.CancellationToken);

        // the last item is held until the Disconnect has torn the connection down, so the StreamEnd is waiting in the
        // queue by then - the stream still completes rather than failing as disconnected
        var items = new List<int>();
        await foreach (var item in client.Hub.CountThenAbort(3, TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item == 3)
                await WaitFor(() => client.Status == HubClientStatus.Disconnected);
        }

        Assert.Equal([1, 2, 3], items);
        await WaitFor(() => client.Status == HubClientStatus.Disconnected);
    }


    [Fact]
    public async Task AStreamThatFailsAfterAbortStillDisconnectsAfterItsError()
    {
        var raw = await RawSession.Open(this.network);
        await raw.Handshake();

        raw.Send(PeerProtocol.Encode(PeerMessageKind.Stream, 2, nameof(IAbortingHub.AbortThenFail), HubArgumentWriter.Empty));

        Assert.Equal(PeerMessageKind.StreamItem, (await raw.Next()).Kind);
        var error = await raw.Next();
        Assert.Equal(PeerMessageKind.Error, error.Kind);
        Assert.Equal("after abort", error.Read(PeerJsonContext.Default.PeerError).Message);

        var disconnect = await raw.Next();
        Assert.Equal(PeerMessageKind.Disconnect, disconnect.Kind);
        Assert.Equal("failed", disconnect.Read(PeerJsonContext.Default.HubHangUp).Message);
    }


    // ---- peer sessions ----

    [Fact]
    public async Task AResumeThatLandsOnAClosedSessionTellsTheHub()
    {
        var endpoint = new FakeEndpoint();
        var transport = new FakeTransport(this.host, endpoint);
        var connection = new TestConnection();
        var session = new PeerHostSession(transport, connection);
        connection.Start(session);

        // the session closes while the hub waits to move the client onto it (Reattach waiting for the send lock)
        endpoint.OnResume = async accepted =>
        {
            connection.Dispose();
            await WaitFor(() => transport.Removed);
            await accepted("t:held");
            return null;
        };
        connection.Deliver(PeerProtocol.Encode(PeerMessageKind.Resume, 1, HubName, new PeerResume("token"), PeerJsonContext.Default.PeerResume));

        await WaitFor(() => !endpoint.Gone.IsEmpty);
        var (id, disconnect) = Assert.Single(endpoint.Gone);
        Assert.Equal("t:held", id);
        Assert.Equal(HubDisconnectReason.ClientTimeout, disconnect.Reason);
    }


    [Fact]
    public async Task AMalformedGoodbyeStillClosesTheSession()
    {
        var endpoint = new FakeEndpoint();
        var transport = new FakeTransport(this.host, endpoint);
        var connection = new TestConnection();
        var session = new PeerHostSession(transport, connection);
        connection.Start(session);

        connection.Deliver(PeerProtocol.Encode(PeerMessageKind.Handshake, 1, HubName, new HubHandshake(PeerProtocol.Version, "Raw", null, null), PeerJsonContext.Default.HubHandshake));
        await WaitFor(() => session.Binding != null && connection.Sent.Count > 0);

        // not JSON - the client is leaving all the same
        connection.Deliver(PeerProtocol.Encode(PeerMessageKind.Disconnect, 0, null, "\u0001not json"u8));

        await WaitFor(() => transport.Removed);
        Assert.True(connection.Closed);
        var (_, disconnect) = Assert.Single(endpoint.Gone);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ClientDisconnect), disconnect);
    }


    // ---- media channels ----

    [Fact]
    public async Task MediaInboxDropsOversizedPacketsAndTheOldestWhenFull()
    {
        var channel = new PeerMediaChannel((_, _) => { }, "c", maxPacketSize: 4, inboxCapacity: 3);

        channel.Deliver(new byte[5]);
        for (byte i = 0; i < 6; i++)
            channel.Deliver(new[] { i });
        channel.Closed();

        var received = new List<byte>();
        await foreach (var packet in channel.Receive(TestContext.Current.CancellationToken))
            received.Add(packet.Span[0]);

        Assert.Equal([3, 4, 5], received);
    }


    // ---- harness ----

    internal sealed class TestConnection : PeerConnectionBase
    {
        public ConcurrentQueue<byte[]> Sent { get; } = new();
        public bool Closed => this.IsClosed;
        public override string RemoteName => "Test";

        public override void Send(byte[] message)
        {
            if (this.IsClosed)
                throw new HubDisconnectedException("closed");

            this.Sent.Enqueue(message);
        }

        public override Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override void Dispose() => this.RaiseClosed();
        public void Deliver(byte[] message) => this.RaiseReceived(message);
    }


    sealed class CallbackHandler(Action<ReadOnlyMemory<byte>> received, Action? closed = null) : IPeerConnectionHandler
    {
        public void Received(ReadOnlyMemory<byte> message) => received(message);
        public void FileProgress(string name, double fraction) { }
        public void FileReceived(string name, string? path, Exception? error) { }
        public void Closed() => closed?.Invoke();
    }


    sealed class FakeTransport(UniversalHubHost host, FakeEndpoint endpoint) : IPeerHostTransport
    {
        readonly FakeBinding binding = new(endpoint);

        public bool Removed { get; private set; }
        public IHubTransport HubTransport => host.Transports[0];
        public string ConnectionPrefix => "t:";
        public UniversalHubHost Host => host;
        public UniversalServerOptions ServerOptions => host.Options;
        public ILogger? Logger => null;
        public IHubBinding? FindRunning(string? key) => this.binding;
        public object CreatePeer(IPeerConnection connection) => connection;
        public void Remove(PeerHostSession session) => this.Removed = true;
        public IEnumerable<PeerHostSession> Sessions => [];
    }


    sealed class FakeBinding(IHubEndpoint endpoint) : IHubBinding
    {
        public string Key => HubName;
        public string Name => HubName;
        public Type? HubType => null;
        public IReadOnlySet<Type> Transports { get; } = new HashSet<Type>();
        public IHubEndpoint Endpoint => endpoint;
        public T? GetSettings<T>() where T : class => null;
    }


    sealed class FakeEndpoint : IHubEndpoint
    {
        public Func<Func<string, Task>, Task<string?>>? OnResume { get; set; }
        public ConcurrentQueue<(string Id, HubDisconnect Disconnect)> Gone { get; } = new();

        public HubDefinition Registration => throw new NotSupportedException();
        public Type ContractType => typeof(IAbortingHub);
        public IHubSerializer Serializer { get; } = new JsonHubSerializer();
        public Task<string?> Connect(string connectionId, HandshakeInfo info, IHubPeerChannel channel, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public HubMethodKind GetMethodKind(string method) => HubMethodKind.NotFound;
        public Task<HubInvocationResult> Invoke(string connectionId, string method, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<byte[]> Stream(string connectionId, string method, ReadOnlyMemory<byte> arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Disconnected(string connectionId, HubDisconnect disconnect) => this.Gone.Enqueue((connectionId, disconnect));
        public void Disconnected(string connectionId, IHubPeerChannel channel, HubDisconnect disconnect) => this.Gone.Enqueue((connectionId, disconnect));
        public Task<string?> Resume(string resumeToken, string connectionIdPrefix, IHubPeerChannel channel, Func<string, Task> accepted, CancellationToken cancellationToken)
            => this.OnResume!(accepted);
        public Task<string?> Rename(string connectionId, string? name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task Disconnect(string connectionId, HubDisconnect disconnect) => Task.CompletedTask;
        public HubConnectedClient? FindClient(string connectionId) => null;
        public bool MediaChannelOpened(string connectionId, IHubMediaChannel channel) => false;
    }


    sealed class RawSession : IPeerConnectionHandler
    {
        readonly Channel<PeerMessage> received = Channel.CreateUnbounded<PeerMessage>();
        IPeerConnection connection = null!;

        public static async Task<RawSession> Open(FakeMultipeerNetwork network)
        {
            var session = new RawSession();
            session.connection = await network.Connect(new FakePeer(network.Advertisements.Single().DisplayName), TimeSpan.FromSeconds(5), CancellationToken.None);
            session.connection.Start(session);
            return session;
        }

        public async Task Handshake()
        {
            this.Send(PeerProtocol.Encode(PeerMessageKind.Handshake, 1, HubName, new HubHandshake(PeerProtocol.Version, "Raw", null, null), PeerJsonContext.Default.HubHandshake));
            var reply = (await this.Next()).Read(PeerJsonContext.Default.PeerHandshakeReply);
            Assert.True(reply.Accepted, reply.Reason);
        }

        public void Send(byte[] message) => this.connection.Send(message);

        public async Task<PeerMessage> Next() => await this.received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public void Received(ReadOnlyMemory<byte> message) => this.received.Writer.TryWrite(PeerProtocol.Decode(message));
        public void FileProgress(string name, double fraction) { }
        public void FileReceived(string name, string? path, Exception? error) { }
        public void Closed() => this.received.Writer.TryComplete();
    }
}
