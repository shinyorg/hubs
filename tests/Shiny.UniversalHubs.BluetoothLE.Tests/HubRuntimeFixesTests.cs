using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// HubRuntime fixes: a stream's Context.Abort after its StreamEnd over BLE, group cleanup that only touches its own
/// connection, and clients that leave while OnConnectedAsync runs. A scripted handler hub stands in for a hub class.
/// </summary>
public class HubRuntimeFixesTests : IAsyncLifetime
{
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

    static HubRuntimeFixesTests() => Json.AddContext(TestJsonContext.Default);

    readonly HubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(10) };
    readonly HubOptions hubOptions = new();
    readonly IHubSerializer serializer = new JsonHubSerializer();
    readonly ScriptedHandler handler = new();
    readonly Dictionary<string, HubProxy> clients = new();
    // the kind of every message the host sent, in order
    readonly System.Collections.Concurrent.ConcurrentQueue<FrameKind> sent = new();
    ServiceProvider services = null!;
    HubRuntime runtime = null!;
    FrameHostSession session = null!;
    int connectedEvents;
    int disconnectedEvents;


    public ValueTask InitializeAsync()
    {
        this.services = new ServiceCollection().BuildServiceProvider();
        this.runtime = new HubRuntime(
            new HubDefinition(typeof(HandlerHub), CharacteristicUuid, this.hubOptions),
            new HandlerDispatcher(this.handler),
            this.services,
            this.options,
            this.serializer,
            null,
            "Scripted",
            () => new HandlerHub(this.handler)
        )
        {
            HostName = "TestHost",
            IsRunning = true
        };
        this.runtime.ClientConnected += (_, _) => Interlocked.Increment(ref this.connectedEvents);
        this.runtime.ClientDisconnected += (_, _) => Interlocked.Increment(ref this.disconnectedEvents);
        this.session = new FrameHostSession(this.runtime, this.options, null)
        {
            Notify = (peer, frame, _) =>
            {
                var header = FrameCodec.ReadHeader(frame);
                if (header.Sequence == 0)
                    this.sent.Enqueue(header.Kind);

                this.clients[peer.Id].ReceiveFrame(frame);
                return Task.CompletedTask;
            }
        };
        return ValueTask.CompletedTask;
    }


    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    static HandshakeInfo Info(string name) => new(FrameCodec.ProtocolVersion, name, null, null);


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public async Task AStreamThatAbortsSendsItsStreamEndBeforeTheDisconnect()
    {
        var client = HubProxyFactories.Create<ITestHub>(new HubClientServices(this.options, this.serializer), CharacteristicUuid);
        this.clients["central-1"] = client;
        await client.ConnectTransport(
            (frame, _) =>
            {
                this.session.OnFrame("central-1", 185, null, frame);
                return Task.CompletedTask;
            },
            185,
            new HubHandshakeOptions("Alice"),
            CancellationToken.None
        );

        try
        {
            await foreach (var _ in ((ITestHub)client).Count(3, TestContext.Current.CancellationToken))
            {
            }
        }
        catch (HubDisconnectedException)
        {
            // the client may still see the disconnect before it reads the StreamEnd it already has - a client-side
            // race this test doesn't cover; what the host sent, and in which order, is what it checks
        }

        await WaitFor(() => this.sent.Contains(FrameKind.Disconnect));
        Assert.Equal(
            [FrameKind.StreamItem, FrameKind.StreamItem, FrameKind.StreamItem, FrameKind.StreamEnd, FrameKind.Disconnect],
            this.sent.Where(x => x is FrameKind.StreamItem or FrameKind.StreamEnd or FrameKind.Error or FrameKind.Disconnect)
        );
        Assert.Empty(this.runtime.Clients);
    }


    [Fact]
    public async Task AStreamThatFailsAfterAbortStillReportsTheAbort()
    {
        Assert.Null(await this.runtime.Connect("c1", Info("A"), new StubChannel(), null));

        var stream = this.runtime.StartStream("c1", "Fail", HubArgumentWriter.Empty, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in stream.Items)
            {
            }
        });

        Assert.True(stream.AbortRequested);
        Assert.Equal("failing", stream.AbortReason);
        // the transport disconnects - the runtime didn't on its own
        Assert.Single(this.runtime.Clients);
    }


    [Fact]
    public async Task ALateCleanupLeavesAReconnectedClientsGroupsAlone()
    {
        // BLE reuses the central's UUID as the connection id
        var leaving = new TaskCompletionSource();
        this.handler.OnConnected = context => this.runtime.AddToGroupAsync(context.ConnectionId, "g");
        this.handler.OnDisconnected = (_, call) => call == 1 ? leaving.Task : Task.CompletedTask;

        Assert.Null(await this.runtime.Connect("central-1", Info("A"), new StubChannel(), null));
        this.runtime.Disconnected("central-1", new HubDisconnect(HubDisconnectReason.ClientTimeout));
        Assert.Null(await this.runtime.Connect("central-1", Info("A"), new StubChannel(), null));

        // the first connection's OnDisconnectedAsync finishes after the second joined
        leaving.SetResult();
        await WaitFor(() => Volatile.Read(ref this.disconnectedEvents) == 1);

        Assert.Equal(["central-1"], this.runtime.GetMembers("g"));
        Assert.Single(this.runtime.Clients);
    }


    [Fact]
    public async Task AClientThatLeavesDuringOnConnectedIsRefusedAndLeavesNoGroups()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        this.handler.OnConnected = async context =>
        {
            entered.TrySetResult();
            await release.Task;
            await this.runtime.AddToGroupAsync(context.ConnectionId, "g");
        };

        var connecting = this.runtime.Connect("central-1", Info("A"), new StubChannel(), null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        this.runtime.Disconnected("central-1", new HubDisconnect(HubDisconnectReason.ClientDisconnect));
        release.SetResult();

        Assert.NotNull(await connecting);
        await WaitFor(() => this.handler.Disconnects == 1);
        Assert.Empty(this.runtime.GetMembers("g"));
        Assert.Empty(this.runtime.Clients);
        Assert.Equal(0, Volatile.Read(ref this.connectedEvents));
        Assert.Equal(0, Volatile.Read(ref this.disconnectedEvents));
    }


    [Fact]
    public async Task MaxClientsHoldsUnderConcurrentHandshakes()
    {
        // not deterministic - the check-then-add window was small - but it must never let two in
        this.hubOptions.MaxClients = 1;
        var gate = new TaskCompletionSource();
        this.handler.OnConnected = _ => gate.Task;

        var attempts = Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => this.runtime.Connect($"c{i}", Info($"C{i}"), new StubChannel(), null), TestContext.Current.CancellationToken))
            .ToList();
        await WaitFor(() => attempts.Count(x => x.IsCompleted) >= 15);
        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, x => x == null);
        Assert.Single(this.runtime.Clients);
    }


    sealed class StubChannel : IHubPeerChannel
    {
        public Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken) => Task.CompletedTask;
    }


    sealed class ScriptedHandler : IHubHandler
    {
        int disconnects;

        public Func<HubCallerContext, Task>? OnConnected { get; set; }
        public Func<HubCallerContext, int, Task>? OnDisconnected { get; set; }
        public int Disconnects => Volatile.Read(ref this.disconnects);

        public HubMethodKind GetMethodKind(string method) => method switch
        {
            "Count" or "Fail" => HubMethodKind.Stream,
            _ => HubMethodKind.NotFound
        };

        public Task<byte[]?> Invoke(HubHandlerCall call, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<byte[]> Stream(HubHandlerCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (call.Method == "Fail")
            {
                await Task.Yield();
                call.Context.Abort("failing");
                throw new InvalidOperationException("after abort");
            }

            var to = int.Parse(Encoding.UTF8.GetString(call.Arguments[0].Span));
            for (var i = 1; i <= to; i++)
            {
                await Task.Yield();
                yield return Encoding.UTF8.GetBytes(i.ToString());
            }
            call.Context.Abort("done");
        }

        public Task OnConnectedAsync(HubCallerContext context) => this.OnConnected?.Invoke(context) ?? Task.CompletedTask;

        public async Task OnDisconnectedAsync(HubCallerContext context, HubDisconnect disconnect)
        {
            var call = Interlocked.Increment(ref this.disconnects);
            if (this.OnDisconnected != null)
                await this.OnDisconnected(context, call);
        }

        public Task OnRenamedAsync(HubCallerContext context, string? previousName) => Task.CompletedTask;
    }
}
