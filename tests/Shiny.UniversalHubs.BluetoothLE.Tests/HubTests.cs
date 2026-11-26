using Microsoft.Extensions.DependencyInjection;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// Runs the generated hub dispatcher and generated client proxy against each other through an in-memory "radio"
/// </summary>
public class HubTests : IAsyncLifetime
{
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

    static HubTests() => Json.AddContext(TestJsonContext.Default);

    readonly HubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(10) };
    readonly HubOptions hubOptions = new() { MaxClients = 4 };
    readonly Dictionary<string, HubProxy> clients = new();
    // a client that resumed is reached on its new link, like a real central
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, HubProxy> links = new();
    readonly IHubSerializer serializer = new JsonHubSerializer();
    ServiceProvider services = null!;
    HubRuntime runtime = null!;
    FrameHostSession session = null!;
    HubLog log = null!;
    int mtu = 23;


    public ValueTask InitializeAsync()
    {
        this.services = new ServiceCollection()
            .AddSingleton<HubLog>()
            .AddTransient<TestHub>()
            .BuildServiceProvider();
        this.log = this.services.GetRequiredService<HubLog>();

        this.runtime = new HubRuntime(
            new HubDefinition(typeof(TestHub), CharacteristicUuid, this.hubOptions),
            HubDispatchers.Get(typeof(TestHub)),
            this.services,
            this.options,
            this.serializer,
            null
        )
        {
            HostName = "TestHost",
            IsRunning = true
        };
        this.session = new FrameHostSession(this.runtime, this.options, null)
        {
            Notify = (peer, frame, _) =>
            {
                Assert.True(frame.Length <= FrameCodec.GetFrameSize(this.mtu));
                (this.links.TryGetValue(peer.LinkId, out var onLink) ? onLink : this.clients[peer.Id]).ReceiveFrame(frame);
                return Task.CompletedTask;
            }
        };
        return ValueTask.CompletedTask;
    }


    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    ITestHub CreateProxy(string peerId)
    {
        var client = HubProxyFactories.Create<ITestHub>(new HubClientServices(this.options, this.serializer), CharacteristicUuid);
        this.clients[peerId] = client;
        return (ITestHub)client;
    }


    async Task<ITestHub> Connect(string peerId = "peer-1", string name = "Alice")
    {
        var hub = this.CreateProxy(peerId);
        await this.ConnectExisting(peerId, name);
        return hub;
    }


    Task ConnectExisting(string peerId, string name) => this.clients[peerId].ConnectTransport(
        (frame, _) =>
        {
            Assert.True(frame.Length <= FrameCodec.GetFrameSize(this.mtu));
            if (!this.session.OnFrame(peerId, this.mtu, null, frame))
                throw new InvalidOperationException("GATT write failed");
            return Task.CompletedTask;
        },
        this.mtu,
        new HubHandshakeOptions(name, "1.0"),
        CancellationToken.None
    );


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public void ProxyIsGeneratedForTheContract()
    {
        var hub = this.CreateProxy("peer-1");
        Assert.Equal("TestHubClient", hub.GetType().Name);
        Assert.IsAssignableFrom<IHubProxy<ITestHub>>(hub);
        Assert.Same(hub, ((IHubProxy<ITestHub>)hub).Hub);
    }


    [Fact]
    public async Task ConnectRunsOnConnectedBeforeTheClientIsTold()
    {
        await this.Connect();

        var client = this.clients["peer-1"];
        Assert.Equal(HubClientStatus.Connected, client.Status);
        Assert.Equal("TestHost", client.HostName);
        Assert.Contains("connected:Alice", this.log.Entries);
        Assert.Equal(["peer-1"], this.runtime.GetMembers("everyone"));
    }


    [Fact]
    public async Task InvokeWithResult()
    {
        var hub = await this.Connect();
        var result = await hub.Echo(new Payload("hi", 42));
        Assert.Equal(new Payload("hi", 42), result);
    }


    [Fact]
    public async Task MultipleArguments()
    {
        var hub = await this.Connect();
        Assert.Equal("a7b9", await hub.Concat("a", 7, new Payload("b", 9)));
    }


    [Fact]
    public async Task HubSeesTheCaller()
    {
        var hub = await this.Connect(name: "Bob");
        Assert.Equal("Bob", await hub.WhoAmI());
    }


    [Fact]
    public async Task VoidMethodCompletesAfterTheHubRuns()
    {
        var hub = await this.Connect();
        await hub.Record("one");
        Assert.Contains("record:one", this.log.Entries);
    }


    [Fact]
    public async Task HubExceptionBecomesRemoteException()
    {
        var hub = await this.Connect();
        var ex = await Assert.ThrowsAsync<HubRemoteException>(() => hub.Fail());
        Assert.Equal(nameof(InvalidOperationException), ex.RemoteErrorType);
        Assert.Equal("boom", ex.Message);
    }


    [Fact]
    public async Task Streaming()
    {
        var hub = await this.Connect();
        var items = new List<int>();
        await foreach (var i in hub.Count(20, CancellationToken.None))
            items.Add(i);

        Assert.Equal(Enumerable.Range(1, 20), items);
    }


    [Fact]
    public async Task CancellationReachesTheHub()
    {
        var hub = await this.Connect();
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hub.Slow(30_000, cts.Token));
        Assert.True(await this.log.SlowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }


    [Fact]
    public async Task InvocationTimesOut()
    {
        this.options.RequestTimeout = TimeSpan.FromMilliseconds(300);
        var hub = await this.Connect();
        await Assert.ThrowsAsync<TimeoutException>(() => hub.Slow(30_000, CancellationToken.None));
    }


    [Theory]
    [InlineData(23)]
    [InlineData(185)]
    [InlineData(517)]
    public async Task LargePayloadsAreChunked(int mtu)
    {
        this.mtu = mtu;
        var hub = await this.Connect();
        var big = new Payload(new string('q', 30_000), 1);
        Assert.Equal(big, await hub.Echo(big));
    }


    [Fact]
    public async Task ConcurrentCallsAreCorrelated()
    {
        var hub = await this.Connect();
        var calls = Enumerable.Range(0, 30).Select(i => hub.Echo(new Payload(new string('x', i * 7), i))).ToList();
        var results = await Task.WhenAll(calls);
        for (var i = 0; i < results.Length; i++)
            Assert.Equal(i, results[i].Number);
    }


    [Fact]
    public async Task PushesToCallerRaiseTypedEvents()
    {
        var hub = await this.Connect();
        var messages = new List<string>();
        (string, int)? numbered = null;
        var pinged = false;
        hub.Message += m => messages.Add(m);
        hub.Numbered += (s, n) => numbered = (s, n);
        hub.Pinged += () => pinged = true;

        await hub.SayToCaller("hello");
        await WaitFor(() => pinged);

        Assert.Equal(["hello"], messages);
        Assert.Equal(("hello", 5), numbered);
    }


    [Fact]
    public async Task PushesArriveInOrder()
    {
        var hub = await this.Connect();
        var received = new List<int>();
        hub.Numbered += (_, n) =>
        {
            lock (received)
                received.Add(n);
        };

        await hub.PushSequence(40);
        await WaitFor(() => received.Count == 40);
        Assert.Equal(Enumerable.Range(0, 40), received);
    }


    [Fact]
    public async Task OthersAndGroups()
    {
        var alice = await this.Connect("peer-a", "Alice");
        var bob = await this.Connect("peer-b", "Bob");
        var carol = await this.Connect("peer-c", "Carol");

        var got = new Dictionary<string, List<string>> { ["alice"] = new(), ["bob"] = new(), ["carol"] = new() };
        alice.Message += m => { lock (got) got["alice"].Add(m); };
        bob.Message += m => { lock (got) got["bob"].Add(m); };
        carol.Message += m => { lock (got) got["carol"].Add(m); };

        await bob.JoinGroup("team");
        await carol.JoinGroup("team");
        await alice.SayToGroup("team", "team-only");
        await alice.SayToOthers("not-alice");

        await WaitFor(() => got["bob"].Count == 2 && got["carol"].Count == 2);
        await Task.Delay(100);
        Assert.Empty(got["alice"]);
        Assert.Equal(["team-only", "not-alice"], got["bob"]);
    }


    [Fact]
    public async Task HubContextPushesFromOutsideTheHub()
    {
        var hub = await this.Connect();
        string? received = null;
        hub.Message += m => received = m;

        // Clients is the generated C# 14 extension property on IHubContext<TestHub>
        IHubContext<TestHub> context = new HubContext<TestHub>(() => this.runtime);
        await context.Clients.All.Message("from outside");

        await WaitFor(() => received != null);
        Assert.Equal("from outside", received);
        Assert.Single(context.ConnectedClients);
    }


    [Fact]
    public async Task HostDisconnectIsCooperative()
    {
        var hub = await this.Connect();
        var client = this.clients["peer-1"];
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        await hub.Kick("bye now");

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerDisconnect, "bye now"), disconnect);
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
        await WaitFor(() => this.log.Entries.Contains("disconnected:Alice:bye now"));
        Assert.Empty(this.runtime.Clients);
        Assert.Empty(this.runtime.GetMembers("everyone"));
    }


    [Fact]
    public async Task HubContextDisconnect()
    {
        await this.Connect();
        IHubContext<TestHub> context = new HubContext<TestHub>(() => this.runtime);
        HubDisconnect? disconnect = null;
        this.clients["peer-1"].Disconnected += (_, d) => disconnect = d;
        await context.Disconnect("peer-1", "kicked");

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerDisconnect, "kicked"), disconnect);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ServerDisconnect"));
    }


    [Fact]
    public async Task ConnectionLossFailsPendingCalls()
    {
        var hub = await this.Connect();
        var call = hub.Slow(30_000, CancellationToken.None);
        await Task.Delay(100);

        await this.clients["peer-1"].LoseConnection("radio silence");
        var ex = await Assert.ThrowsAsync<HubDisconnectedException>(() => call);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ClientTimeout, "radio silence"), ex.Disconnect);
    }


    [Fact]
    public async Task ClientDisconnectTellsTheHostItLeft()
    {
        await this.Connect();
        var client = this.clients["peer-1"];
        var statuses = new List<HubStatusChangedEventArgs>();
        HubDisconnect? disconnect = null;
        client.StatusChanged += (_, e) => statuses.Add(e);
        client.Disconnected += (_, d) => disconnect = d;

        await client.Disconnect();

        Assert.Equal(new HubDisconnect(HubDisconnectReason.ClientDisconnect), disconnect);
        Assert.Equal(
            [
                new HubStatusChangedEventArgs(HubClientStatus.Disconnecting, disconnect),
                new HubStatusChangedEventArgs(HubClientStatus.Disconnected, disconnect)
            ],
            statuses
        );
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientDisconnect"));
        Assert.Empty(this.runtime.Clients);
    }


    [Fact]
    public async Task StoppingTheHubIsAShutdownOnTheClient()
    {
        await this.Connect();
        HubDisconnect? disconnect = null;
        this.clients["peer-1"].Disconnected += (_, d) => disconnect = d;

        await this.runtime.DisconnectAll(new HubDisconnect(HubDisconnectReason.ServerShutdown, "closing time"));

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "closing time"), disconnect);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ServerShutdown"));
    }


    [Fact]
    public async Task ADisconnectFromAnOlderHostIsAServerDisconnect()
    {
        await this.Connect();
        HubDisconnect? disconnect = null;
        this.clients["peer-1"].Disconnected += (_, d) => disconnect = d;

        // hosts before the reason code sent only {"Reason": ...}
        var payload = System.Text.Encoding.UTF8.GetBytes("""{"Reason":"legacy"}""");
        foreach (var frame in FrameCodec.Encode(FrameKind.Disconnect, 0x8001, null, payload, FrameCodec.GetFrameSize(this.mtu)))
            this.clients["peer-1"].ReceiveFrame(frame);

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerDisconnect, "legacy"), disconnect);
    }


    [Fact]
    public async Task CallingWhileDisconnectedFailsFast()
    {
        var hub = this.CreateProxy("peer-1");
        await Assert.ThrowsAsync<HubDisconnectedException>(() => hub.Record("x"));
    }


    [Fact]
    public async Task MaxClientsRejects()
    {
        this.hubOptions.MaxClients = 1;
        await this.Connect("peer-1");

        this.CreateProxy("peer-2");
        var ex = await Assert.ThrowsAsync<HubException>(() => this.ConnectExisting("peer-2", "late"));
        Assert.Contains("Host is full", ex.Message);
        Assert.Equal(HubClientStatus.Disconnected, this.clients["peer-2"].Status);
    }


    [Fact]
    public async Task StoppedHubRefusesHandshakes()
    {
        this.runtime.IsRunning = false;
        this.CreateProxy("peer-1");
        var ex = await Assert.ThrowsAsync<HubException>(() => this.ConnectExisting("peer-1", "Alice"));
        Assert.Contains("Hub is not running", ex.Message);
    }


    [Fact]
    public async Task ValidateClientRejects()
    {
        this.hubOptions.ValidateClient = info => info.Name == "mallory" ? "Not you" : null;
        this.CreateProxy("peer-1");
        var ex = await Assert.ThrowsAsync<HubException>(() => this.ConnectExisting("peer-1", "mallory"));
        Assert.Contains("Not you", ex.Message);
    }


    [Fact]
    public async Task RenameGivesTheClientANewNameWithoutReconnecting()
    {
        var hub = await this.Connect();
        var client = this.clients["peer-1"];
        HubClientRenamedEventArgs? renamed = null;
        this.runtime.ClientRenamed += (_, e) => renamed = e;

        Assert.Equal("Alice", client.ClientName);
        await client.Rename("Alicia");

        Assert.Equal("Alicia", client.ClientName);
        Assert.Equal("Alicia", await hub.WhoAmI());
        Assert.Contains("renamed:Alice->Alicia", this.log.Entries);
        Assert.NotNull(renamed);
        Assert.Equal("Alice", renamed!.PreviousName);
        Assert.Equal("Alicia", renamed.Client.Name);
        Assert.Equal(HubClientStatus.Connected, client.Status);
        Assert.DoesNotContain(this.log.Entries, x => x.StartsWith("disconnected:"));
    }


    [Fact]
    public async Task RenameToTheSameNameIsANoOp()
    {
        await this.Connect();
        await this.clients["peer-1"].Rename("Alice");
        Assert.DoesNotContain(this.log.Entries, x => x.StartsWith("renamed:"));
    }


    [Fact]
    public async Task ValidateClientCanRefuseARename()
    {
        this.hubOptions.ValidateClient = info => info.Name == "mallory" ? "Not you" : null;
        var hub = await this.Connect();
        var client = this.clients["peer-1"];

        var ex = await Assert.ThrowsAsync<HubRemoteException>(() => client.Rename("mallory"));
        Assert.Equal(HubRemoteException.RenameRefused, ex.RemoteErrorType);
        Assert.Equal("Not you", ex.Message);
        Assert.Equal("Alice", client.ClientName);
        Assert.Equal("Alice", await hub.WhoAmI());
    }


    [Fact]
    public async Task OnRenamedThrowingRefusesTheRenameAndKeepsTheOldName()
    {
        var hub = await this.Connect();
        var client = this.clients["peer-1"];

        var ex = await Assert.ThrowsAsync<HubRemoteException>(() => client.Rename("Boom"));
        Assert.Equal(HubRemoteException.RenameRefused, ex.RemoteErrorType);
        Assert.Equal("Not that name", ex.Message);
        Assert.Equal("Alice", await hub.WhoAmI());
    }


    [Fact]
    public async Task RenameNeedsAConnection()
    {
        this.CreateProxy("peer-1");
        await Assert.ThrowsAsync<HubDisconnectedException>(() => this.clients["peer-1"].Rename("x"));
    }


    [Fact]
    public async Task HostRenameReachesEveryClientInOrderWithPushes()
    {
        var hub = await this.Connect();
        await this.Connect("peer-2", "Bob");
        var client = this.clients["peer-1"];
        var seen = new List<string>();
        hub.Numbered += (_, i) => { lock (seen) seen.Add($"n{i}"); };
        client.HostRenamed += (_, name) => { lock (seen) seen.Add($"host:{name}"); };

        await hub.PushSequence(2);
        await this.runtime.SetHostName("NewHost", CancellationToken.None);
        await hub.PushSequence(2);

        await WaitFor(() => { lock (seen) return seen.Count == 5; });
        Assert.Equal(["n0", "n1", "host:NewHost", "n0", "n1"], seen);
        Assert.Equal("NewHost", client.HostName);
        await WaitFor(() => this.clients["peer-2"].HostName == "NewHost");
    }


    // ---- resume after a dropped link ----

    sealed class Link(string id)
    {
        public string Id { get; set; } = id;
        public int Relinks { get; set; }
    }


    /// <summary>
    /// Connects like a BLE client whose every relink comes back on a new central - the way Android's random addresses do
    /// </summary>
    async Task<(ITestHub Hub, HubProxy Client, Link Link)> ConnectResumable(string connectionId = "central-1", bool automaticReconnect = true)
    {
        var hub = this.CreateProxy(connectionId);
        var client = this.clients[connectionId];
        var link = new Link(connectionId);
        await client.ConnectTransport(
            (frame, _) =>
            {
                if (!this.session.OnFrame(link.Id, this.mtu, null, frame))
                    throw new InvalidOperationException("GATT write failed");
                return Task.CompletedTask;
            },
            this.mtu,
            new HubHandshakeOptions("Alice", "1.0") { AutomaticReconnect = automaticReconnect },
            CancellationToken.None,
            relink: _ =>
            {
                link.Relinks++;
                link.Id = $"{connectionId}-relink-{link.Relinks}";
                this.links[link.Id] = client;
                return Task.FromResult(this.mtu);
            }
        );
        return (hub, client, link);
    }


    [Fact]
    public async Task HeldClientResumesOnANewLinkAsTheSameConnection()
    {
        this.runtime.ResumeWindow = TimeSpan.FromSeconds(5);
        var (hub, client, link) = await this.ConnectResumable();
        var statuses = new List<HubClientStatus>();
        var messages = new List<string>();
        HubDisconnect? disconnected = null;
        client.StatusChanged += (_, e) => { lock (statuses) statuses.Add(e.Status); };
        client.Disconnected += (_, d) => disconnected = d;
        hub.Message += m => { lock (messages) messages.Add(m); };
        IHubContext<TestHub> context = new HubContext<TestHub>(() => this.runtime);

        // the host sees the central go first, and keeps the client
        this.session.OnPeerGone("central-1", new HubDisconnect(HubDisconnectReason.ClientTimeout, "Unsubscribed"));
        Assert.Single(context.ConnectedClients);
        Assert.Equal(["central-1"], this.runtime.GetMembers("everyone"));
        await context.Clients.All.Message("while you were away");
        await context.Clients.Group("everyone").Message("and this");

        client.DropLink("out of range");
        await WaitFor(() => client.Status == HubClientStatus.Connected && messages.Count == 2);

        Assert.Equal(["while you were away", "and this"], messages);
        Assert.Equal([HubClientStatus.Reconnecting, HubClientStatus.Connected], statuses);
        Assert.Null(disconnected);
        Assert.Equal("central-1-relink-1", link.Id);

        // same connection - id, groups - on the new central
        Assert.Equal("central-1", Assert.Single(context.ConnectedClients).Id);
        Assert.Same(this.runtime.FindClient("central-1"), this.session.FindClientOnLink("central-1-relink-1"));
        Assert.Equal(["central-1"], this.runtime.GetMembers("everyone"));
        Assert.Equal("Alice", await hub.WhoAmI());
        Assert.DoesNotContain(this.log.Entries, x => x.StartsWith("disconnected:", StringComparison.Ordinal));

        // and later pushes follow on the new link
        await context.Clients.All.Message("welcome back");
        await WaitFor(() => messages.Count == 3);
    }


    [Fact]
    public async Task ClientResumesBeforeTheHostNoticesTheDrop()
    {
        this.runtime.ResumeWindow = TimeSpan.FromSeconds(5);
        var (hub, client, _) = await this.ConnectResumable();

        client.DropLink("radio silence");
        await WaitFor(() => client.Status == HubClientStatus.Connected && this.session.FindClientOnLink("central-1-relink-1") != null);

        // the old central's unsubscribe arrives late - it no longer speaks for the client
        this.session.OnPeerGone("central-1", new HubDisconnect(HubDisconnectReason.ClientTimeout, "Unsubscribed"));
        await Task.Delay(100);

        Assert.Equal("Alice", await hub.WhoAmI());
        Assert.Single(this.runtime.Clients);
        Assert.DoesNotContain(this.log.Entries, x => x.StartsWith("disconnected:", StringComparison.Ordinal));
    }


    [Fact]
    public async Task HeldClientIsLetGoWhenTheWindowRunsOut()
    {
        this.runtime.ResumeWindow = TimeSpan.FromMilliseconds(300);
        await this.ConnectResumable();

        this.session.OnPeerGone("central-1", new HubDisconnect(HubDisconnectReason.ClientTimeout, "Unsubscribed"));
        Assert.Single(this.runtime.Clients);
        Assert.DoesNotContain(this.log.Entries, x => x.StartsWith("disconnected:", StringComparison.Ordinal));

        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientTimeout"));
        Assert.Empty(this.runtime.Clients);
        Assert.Empty(this.runtime.GetMembers("everyone"));
    }


    [Fact]
    public async Task ClientDisconnectsWhenTheHostCannotResume()
    {
        this.runtime.ResumeWindow = TimeSpan.FromSeconds(5);
        var (_, client, _) = await this.ConnectResumable();
        var disconnected = new TaskCompletionSource<HubDisconnect>();
        client.Disconnected += (_, d) => disconnected.TrySetResult(d);

        // the host let it go already (its window ran out while the client was away)
        this.session.OnPeerGone("central-1", new HubDisconnect(HubDisconnectReason.ClientTimeout));
        await this.runtime.Disconnect("central-1", new HubDisconnect(HubDisconnectReason.ClientTimeout, "gone"));

        client.DropLink("out of range");
        var disconnect = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HubDisconnectReason.ClientTimeout, disconnect.Reason);
        Assert.Contains("can't be resumed", disconnect.Message);
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
    }


    [Fact]
    public async Task ClientThatLeavesIsNotHeld()
    {
        this.runtime.ResumeWindow = TimeSpan.FromSeconds(30);
        var (_, client, _) = await this.ConnectResumable();

        await client.Disconnect();

        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientDisconnect"));
        Assert.Empty(this.runtime.Clients);
    }


    [Fact]
    public async Task HeldClientThatMissesTooMuchIsLetGo()
    {
        this.runtime.ResumeWindow = TimeSpan.FromSeconds(30);
        this.runtime.ResumeBufferSize = 2;
        await this.ConnectResumable();
        IHubContext<TestHub> context = new HubContext<TestHub>(() => this.runtime);

        this.session.OnPeerGone("central-1", new HubDisconnect(HubDisconnectReason.ClientTimeout));
        for (var i = 0; i < 3; i++)
            await context.Clients.All.Message($"m{i}");

        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientTimeout"));
        Assert.Empty(this.runtime.Clients);
    }


    [Fact]
    public async Task ClientWithoutAutomaticReconnectDisconnectsOnADrop()
    {
        this.runtime.ResumeWindow = TimeSpan.FromSeconds(5);
        var (_, client, link) = await this.ConnectResumable(automaticReconnect: false);
        var disconnected = new TaskCompletionSource<HubDisconnect>();
        client.Disconnected += (_, d) => disconnected.TrySetResult(d);

        client.DropLink("out of range");

        Assert.Equal(HubDisconnectReason.ClientTimeout, (await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
        Assert.Equal(0, link.Relinks);
    }


    [Fact]
    public async Task NoResumeTokenWhenTheHostDoesNotHoldClients()
    {
        // ResumeWindow defaults to zero on a bare runtime
        var (_, client, link) = await this.ConnectResumable();
        var disconnected = new TaskCompletionSource<HubDisconnect>();
        client.Disconnected += (_, d) => disconnected.TrySetResult(d);

        client.DropLink("out of range");

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, link.Relinks);
    }
}
