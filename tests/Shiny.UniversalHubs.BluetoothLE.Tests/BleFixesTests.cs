using Microsoft.Extensions.DependencyInjection;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs.Tests;

[UniversalHubContract]
public interface IGatedHub
{
    Task<string> Ping();
}


/// <summary>
/// Holds OnConnectedAsync until the test lets it go
/// </summary>
public class ConnectGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Log { get; } = new();

    public void Add(string entry)
    {
        lock (this.Log)
            this.Log.Add(entry);
    }

    public bool Has(string entry)
    {
        lock (this.Log)
            return this.Log.Contains(entry);
    }
}


public class GatedHub(ConnectGate gate) : UniversalHub<IGatedHub>
{
    public override async Task OnConnectedAsync(HubConnection connection)
    {
        gate.Entered.TrySetResult();
        await gate.Released.Task;
        gate.Add($"connected:{this.Context.ConnectionId}");
    }

    public override Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect)
    {
        gate.Add($"disconnected:{this.Context.ConnectionId}:{disconnect.Reason}");
        return Task.CompletedTask;
    }

    public Task<string> Ping() => Task.FromResult("pong");
}


/// <summary>
/// The frame host session's races: a link that goes mid-handshake, a Cancel right behind its call
/// </summary>
public class BleFixesTests : IAsyncLifetime
{
    const string GatedUuid = "6e400010-b5a3-f393-e0a9-e50e24dcca9e";
    const string TestUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const int Mtu = 185;

    static BleFixesTests() => Json.AddContext(TestJsonContext.Default);

    readonly HubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(10) };
    readonly IHubSerializer serializer = new JsonHubSerializer();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, HubProxy> clients = new();
    ServiceProvider services = null!;


    public ValueTask InitializeAsync()
    {
        this.services = new ServiceCollection()
            .AddSingleton<ConnectGate>()
            .AddSingleton<HubLog>()
            .AddTransient<GatedHub>()
            .AddTransient<TestHub>()
            .BuildServiceProvider();
        return ValueTask.CompletedTask;
    }


    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    (HubRuntime Runtime, FrameHostSession Session) CreateHost(Type hubType, string key)
    {
        var runtime = new HubRuntime(
            new HubDefinition(hubType, key, new HubOptions()),
            HubDispatchers.Get(hubType),
            this.services,
            this.options,
            this.serializer,
            null
        )
        {
            HostName = "TestHost",
            IsRunning = true
        };
        var session = new FrameHostSession(runtime, this.options, null)
        {
            Notify = (link, frame, _) =>
            {
                if (this.clients.TryGetValue(link.LinkId, out var client))
                    client.ReceiveFrame(frame);
                return Task.CompletedTask;
            }
        };
        return (runtime, session);
    }


    Task ConnectClient(FrameHostSession session, HubProxy client, string peerId, CancellationToken cancellationToken)
    {
        this.clients[peerId] = client;
        return client.ConnectTransport(
            (frame, _) =>
            {
                if (!session.OnFrame(peerId, Mtu, null, frame))
                    throw new InvalidOperationException("GATT write failed");
                return Task.CompletedTask;
            },
            Mtu,
            new HubHandshakeOptions("Alice"),
            cancellationToken
        );
    }


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public async Task LinkGoneDuringOnConnectedLetsTheClientGo()
    {
        var gate = this.services.GetRequiredService<ConnectGate>();
        var (runtime, session) = this.CreateHost(typeof(GatedHub), GatedUuid);

        // the first client gives up waiting for its ack
        var quick = new HubProtocolOptions { RequestTimeout = TimeSpan.FromSeconds(1) };
        var first = HubProxyFactories.Create<IGatedHub>(new HubClientServices(quick, this.serializer), GatedUuid);
        var firstConnect = this.ConnectClient(session, first, "central-1", TestContext.Current.CancellationToken);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // the central drops (unsubscribe / sweep) while OnConnectedAsync still runs
        session.OnPeerGone("central-1", new HubDisconnect(HubDisconnectReason.ClientTimeout, "Unsubscribed"));
        this.clients.TryRemove("central-1", out _);
        gate.Released.TrySetResult();

        await WaitFor(() => gate.Has("disconnected:central-1:ClientTimeout"));
        Assert.Empty(runtime.Clients);
        Assert.Empty(session.Links);
        await Assert.ThrowsAnyAsync<Exception>(() => firstConnect);

        // the same central comes back - its id isn't stuck as "in use"
        var second = HubProxyFactories.Create<IGatedHub>(new HubClientServices(this.options, this.serializer), GatedUuid);
        await this.ConnectClient(session, second, "central-1", TestContext.Current.CancellationToken);
        Assert.Equal(HubClientStatus.Connected, second.Status);
        Assert.Equal("pong", await ((IGatedHub)second).Ping());
        Assert.Single(runtime.Clients);
    }


    [Fact]
    public void CancelBeforeTheCallRunsCancelsItAsItStarts()
    {
        var link = new FrameHostLink(null!, "central-1", new MessageReassembler(1024, TimeSpan.FromSeconds(5), 4));
        link.Expect(7);
        link.Cancel(7);

        using var cts = new CancellationTokenSource();
        link.Track(7, cts);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(0, link.InFlightCount);
    }


    [Fact]
    public void CancelAfterTheCallEndedChangesNothingLater()
    {
        var link = new FrameHostLink(null!, "central-1", new MessageReassembler(1024, TimeSpan.FromSeconds(5), 4));
        link.Expect(7);
        using var first = new CancellationTokenSource();
        link.Track(7, first);
        link.Untrack(7);
        link.Unexpect(7);

        // a Cancel that crossed the reply
        link.Cancel(7);

        link.Expect(7);
        using var next = new CancellationTokenSource();
        link.Track(7, next);
        Assert.False(next.IsCancellationRequested);
        Assert.Equal(1, link.InFlightCount);
    }


    [Fact]
    public async Task CancelRightBehindItsCallIsNeverLost()
    {
        var (_, session) = this.CreateHost(typeof(TestHub), TestUuid);
        var client = HubProxyFactories.Create<ITestHub>(new HubClientServices(this.options, this.serializer), TestUuid);
        await this.ConnectClient(session, client, "central-1", TestContext.Current.CancellationToken);
        var link = Assert.Single(session.Links);

        var arguments = new HubArgumentWriter(this.serializer).Write(30_000).ToArray();
        var frameSize = FrameCodec.GetFrameSize(Mtu);
        for (var i = 0; i < 50; i++)
        {
            // ids the proxy never uses
            var id = (ushort)(0x4000 + i);
            var invoke = FrameCodec.Encode(FrameKind.Invoke, id, nameof(ITestHub.Slow), arguments, frameSize);
            var cancel = FrameCodec.Encode(FrameKind.Cancel, id, null, ReadOnlySpan<byte>.Empty, frameSize);
            foreach (var frame in invoke.Concat(cancel))
                Assert.True(session.OnFrame("central-1", Mtu, null, frame));

            // a lost cancel leaves the 30 second call running
            await WaitFor(() => link.InFlightCount == 0, 3000);
        }
    }
}
