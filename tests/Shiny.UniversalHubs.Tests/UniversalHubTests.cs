using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.Switchboard;
using Shiny.Net.HttpServer.Switchboard.Client;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// The real UniversalHubs host and client - generated proxy, generated hub dispatcher, Switchboard, file routes - talking
/// over an in-memory HTTP connection
/// </summary>
public class UniversalHubTests : IAsyncLifetime
{
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string OtherCharacteristicUuid = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";
    const string MdnsServiceType = "_switchtest._tcp";

    static UniversalHubTests() => Json.AddContext(TestJsonContext.Default);

    readonly string filesDirectory = Path.Combine(Path.GetTempPath(), "universalhubs-tests", Guid.NewGuid().ToString("N"));
    readonly List<ServiceProvider> clientContainers = new();
    TestHttpServer app = null!;
    IUniversalHubHost host = null!;
    HubLog log = null!;


    public async ValueTask InitializeAsync()
    {
        this.app = this.BuildHost();
        this.host = this.app.Services.GetRequiredService<IUniversalHubHost>();
        this.log = this.app.Services.GetRequiredService<HubLog>();

        Assert.Equal(HubTransports.Http, await this.host.Start(HubTransports.Http));
    }


    TestHttpServer BuildHost(string? endpoint = null)
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.HideExceptionDetails = false;

        builder.Services.AddSingleton(NoBluetoothHosting.Create());
        builder.Services.AddSingleton<HubLog>();
        builder.Services.AddUniversalServer(server =>
        {
            if (endpoint != null)
                server.Endpoint = endpoint;

            server
                .Ble(o =>
                {
                    o.LocalName = "TestHost";
                    o.EnableFileTransfers(this.filesDirectory, ft => ft.MaxUploadSize = 1024);
                })
                .Http(o =>
                {
                    o.Advertise = false;
                    o.ManageHttpServer = false;
                    o.HostId = "host-1";
                    o.AuthorizeFile = req => !req.FileName.StartsWith("secret", StringComparison.Ordinal);
                })
                .AddHub<TestHub>(CharacteristicUuid, MdnsServiceType, o => o.MaxClients = 3)
                .AddHub<OtherHub>(OtherCharacteristicUuid, MdnsServiceType, o => o.AllowTunnel = true);
        });

        return TestHttpServer.For(builder.Build());
    }


    public async ValueTask DisposeAsync()
    {
        foreach (var c in this.clientContainers)
            await c.DisposeAsync();

        await this.app.DisposeAsync();
        if (Directory.Exists(this.filesDirectory))
            Directory.Delete(this.filesDirectory, true);
    }


    IUniversalHubClient<ITestHub> CreateClient(TestHttpServer? server = null) => this.CreateClient<ITestHub>(CharacteristicUuid, server);


    IUniversalHubClient<TContract> CreateClient<TContract>(string characteristicUuid, TestHttpServer? server = null) where TContract : class
    {
        server ??= this.app;
        var services = new ServiceCollection()
            .AddLogging()
            .AddUniversalHubClient<TContract>(characteristicUuid, MdnsServiceType, o =>
            {
                o.HttpMessageHandlerFactory = () => server.Server.CreateInMemoryHandler();
                o.HttpConnectTimeout = TimeSpan.FromSeconds(5);
            })
            .BuildServiceProvider();

        this.clientContainers.Add(services);
        return services.GetRequiredService<IUniversalHubClient<TContract>>();
    }


    static HubHostInfo HttpHost(string path = "/hubs") => new(
        "TestHost",
        "TestHost",
        "host-1",
        null,
        new DiscoveredHttpServer(
            "TestHost",
            new Uri("http://localhost" + path),
            [],
            80,
            new Dictionary<string, string> { ["hubs"] = CharacteristicUuid, ["proto"] = UniversalHubProtocol.Version.ToString(), ["path"] = path }
        )
    );


    async Task<IUniversalHubClient<ITestHub>> Connect(string name = "Alice")
    {
        var client = this.CreateClient();
        await client.Connect(HttpHost(), new HubConnectOptions(name, "1.0"));
        return client;
    }


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    sealed class FakeLocator(DiscoveredHttpServer? current) : IHttpServerLocator
    {
        public int Lookups;

        public IAsyncEnumerable<HttpServerDiscoveryEvent> WatchAsync(string serviceType = "_http._tcp", CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<HttpServerDiscoveryEvent>();

        public Task<DiscoveredHttpServer?> FindFirstAsync(string serviceType = "_http._tcp", TimeSpan? timeout = null, Func<DiscoveredHttpServer, bool>? filter = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.Lookups);
            return Task.FromResult(current != null && (filter == null || filter(current)) ? current : null);
        }

        public Task<IReadOnlyList<DiscoveredHttpServer>> FindAllAsync(string serviceType = "_http._tcp", TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DiscoveredHttpServer>>(current == null ? [] : [current]);
    }


    IUniversalHubClient<ITestHub> CreateClientWithLocator(IHttpServerLocator locator)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddUniversalHubClient<ITestHub>(CharacteristicUuid, MdnsServiceType, o =>
            {
                o.HttpMessageHandlerFactory = () => this.app.Server.CreateInMemoryHandler();
                o.HttpConnectTimeout = TimeSpan.FromSeconds(2);
            })
            .AddSingleton(locator)
            .BuildServiceProvider();

        this.clientContainers.Add(services);
        return services.GetRequiredService<IUniversalHubClient<ITestHub>>();
    }


    [Fact]
    public async Task AHostThatMovedIsLookedUpAgain()
    {
        // what the scan last saw (a host that has since restarted somewhere else), and where it is now
        var locator = new FakeLocator(HttpHost().Http);
        var client = this.CreateClientWithLocator(locator);

        await client.Connect(HttpHost("/moved-away"), new HubConnectOptions("Alice") { Allowed = HubTransports.Http });

        Assert.Equal(HubTransports.Http, client.Transport);
        Assert.Equal(1, locator.Lookups);
        Assert.Equal("Alice", await client.Hub.WhoAmI());
    }


    [Fact]
    public async Task AHostStillAtItsStaleAddressIsNotRetried()
    {
        var stale = HttpHost("/moved-away");
        var locator = new FakeLocator(stale.Http);   // mDNS still says the same thing
        var client = this.CreateClientWithLocator(locator);

        await Assert.ThrowsAnyAsync<Exception>(() => client.Connect(stale, new HubConnectOptions("Alice") { Allowed = HubTransports.Http }));
        Assert.Equal(1, locator.Lookups);
        Assert.Equal(BleHubClientStatus.Disconnected, client.Status);
    }


    [Fact]
    public async Task AnAddressFromOutsideMdnsIsNotLookedUp()
    {
        var locator = new FakeLocator(HttpHost().Http);
        var client = this.CreateClientWithLocator(locator);

        await Assert.ThrowsAnyAsync<Exception>(() => client.Connect(HubHostInfo.FromUrl(new Uri("http://localhost"), "/moved-away"), new HubConnectOptions("Alice")));
        Assert.Equal(0, locator.Lookups);
    }


    [Fact]
    public async Task ConnectsOverHttpAndCallsWork()
    {
        var client = await this.Connect();

        Assert.Equal(HubTransports.Http, client.Transport);
        Assert.Equal(BleHubClientStatus.Connected, client.Status);
        Assert.Equal("TestHost", client.HostName);
        Assert.Contains("connected:Alice", this.log.Entries);

        Assert.Equal(new Payload("hi", 42), await client.Hub.Echo(new Payload("hi", 42)));
        Assert.Equal("Alice", await client.Hub.WhoAmI());

        await client.Hub.Record("void works");
        Assert.Contains("record:void works", this.log.Entries);
    }


    [Fact]
    public async Task UniversalHubReportsHttpConnectionsOnConnectAndDisconnect()
    {
        var client = await this.Connect();
        var id = Assert.Single(this.app.Services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients).Id;

        Assert.StartsWith("sb:", id);
        Assert.Contains($"connected-via:Http:{id}", this.log.Entries);
        Assert.Equal($"Http:{id}", await client.Hub.MyConnection());

        await client.Disconnect();
        await WaitFor(() => this.log.Entries.Contains($"disconnected-via:Http:{id}"));
    }


    [Fact]
    public async Task HubsSeeTheConnectionsPropertiesAndItsHttpSide()
    {
        var client = await this.Connect();

        // OnConnectedAsync ran inside the handshake request and stored a property; a later call sees both
        Assert.Contains("connected-request:POST", this.log.Entries);
        Assert.Equal("hello Alice|line:True|request:POST|ble:none", await client.Hub.Inspect());

        await client.Disconnect();
        await WaitFor(() => this.log.Entries.Contains("disconnected-http:none"));
    }


    [Theory]
    [InlineData("sb:f8bf8a8b3ec3252c", HubTransports.Http)]
    [InlineData("mc:5f1e2d3c4b5a69788796a5b4c3d2e1f0", HubTransports.Multipeer)]
    [InlineData("6E400001-B5A3-F393-E0A9-E50E24DCCA9E", HubTransports.Ble)]
    [InlineData("AA:BB:CC:DD:EE:FF", HubTransports.Ble)]
    public void ConnectionIdsMapToTheirTransport(string connectionId, HubTransports expected)
        => Assert.Equal(expected, HubConnection.GetTransport(connectionId));


    [Fact]
    public void UniversalServerAppliesEachHubsServiceTypeAndTheEndpoint()
    {
        var options = this.app.Services.GetRequiredService<UniversalHubHostOptions>();
        Assert.Equal(UniversalHubProtocol.DefaultBasePath, options.BasePath);
        Assert.Equal("host-1", options.HostId);

        var bindings = this.app.Services.GetServices<IHubBinding>().ToList();
        Assert.All(bindings, x => Assert.Equal(MdnsServiceType, x.ServiceType));
        Assert.False(bindings.Single(x => x.HubType == typeof(TestHub)).AllowTunnel);
        Assert.True(bindings.Single(x => x.HubType == typeof(OtherHub)).AllowTunnel);

        var registration = this.app.Services.GetServices<BleHubRegistration>().Single(x => x.HubType == typeof(TestHub));
        Assert.Equal(3, registration.Options.MaxClients);
    }


    [Fact]
    public async Task UniversalServerServesHubsAtItsEndpoint()
    {
        await using var server = this.BuildHost("/game");
        await server.Services.GetRequiredService<IUniversalHubHost>().Start(HubTransports.Http);

        var client = this.CreateClient(server);
        await client.Connect(HttpHost("/game"), new HubConnectOptions("Alice", "1.0"));
        Assert.Equal("Alice", await client.Hub.WhoAmI());
    }


    [Theory]
    [InlineData("")]
    [InlineData("shinyhub")]
    [InlineData("_shinyhub")]
    [InlineData("_shinyhub._udp")]
    [InlineData("_a-name-that-is-far-too-long._tcp")]
    public void BadServiceTypesAreRejected(string serviceType)
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUniversalServer(
            s => s.AddHub<TestHub>(CharacteristicUuid, serviceType)
        ));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUniversalHubClient<ITestHub>(CharacteristicUuid, serviceType));
    }


    [Fact]
    public void HubClientsUseTheirServiceUuid()
    {
        // hubs are characteristics in a single BLE service - two advertised 128-bit UUIDs overflow the advertisement
        const string custom = "11111111-2222-3333-4444-555555555555";
        using var sp = new ServiceCollection()
            .AddUniversalHubClient<ITestHub>(CharacteristicUuid, MdnsServiceType, o => o.ServiceUuid = custom)
            .AddUniversalHubClient<IOtherHub>(OtherCharacteristicUuid, MdnsServiceType, o => o.ServiceUuid = custom)
            .BuildServiceProvider();

        var test = (BleHubClient)(object)sp.GetRequiredService<IBleHubClient<ITestHub>>();
        var other = (BleHubClient)(object)sp.GetRequiredService<IBleHubClient<IOtherHub>>();
        Assert.Equal(custom, test.ServiceUuid);
        Assert.Equal(custom, other.ServiceUuid);
        Assert.NotEqual(test.CharacteristicUuid, other.CharacteristicUuid);
    }


    [Fact]
    public void UniversalServerHostsEveryHubUnderItsServiceUuid()
    {
        const string custom = "11111111-2222-3333-4444-555555555555";
        using var sp = new ServiceCollection()
            .AddUniversalServer(s => s
                .ServiceUuid(custom)
                .Protocol(o => o.RequestTimeout = TimeSpan.FromSeconds(7))
                .AddHub<TestHub>(CharacteristicUuid, MdnsServiceType)
                .AddHub<OtherHub>(OtherCharacteristicUuid, MdnsServiceType))
            .BuildServiceProvider();

        Assert.Equal(custom, sp.GetRequiredService<BleHubHostOptions>().ServiceUuid);
        Assert.Equal(TimeSpan.FromSeconds(7), sp.GetRequiredService<BleHubProtocolOptions>().RequestTimeout);
    }


    [Fact]
    public void UniversalServerIsRegisteredOnce()
    {
        var services = new ServiceCollection().AddUniversalServer(s => s.AddHub<TestHub>(CharacteristicUuid, MdnsServiceType));
        Assert.Throws<InvalidOperationException>(() => services.AddUniversalServer(s => s.AddHub<TestHub>(CharacteristicUuid, MdnsServiceType)));
    }


    [Fact]
    public void HubsAreAdvertisedOncePerServiceType()
    {
        var ads = UniversalHubHost.GetAdvertisements(
            [
                new FakeBinding("aaaa", "_chess._tcp"),
                new FakeBinding("BBBB", "_unigame._tcp"),
                new FakeBinding("cccc", "_Chess._tcp"),
                new FakeBinding("dddd", null)
            ],
            "_shinyhub._tcp"
        );

        Assert.Equal(3, ads.Count);
        Assert.Contains(("_chess._tcp", "aaaa,cccc"), ads);
        Assert.Contains(("_unigame._tcp", "bbbb"), ads);
        Assert.Contains(("_shinyhub._tcp", "dddd"), ads);
    }


    sealed record FakeBinding(string CharacteristicUuid, string? ServiceType, bool AllowTunnel = false) : IHubBinding
    {
        public Type HubType => typeof(TestHub);
        public IBleHubTransportEndpoint Endpoint => throw new NotSupportedException();
        public bool IsHttpRunning { get; set; }
        public bool IsBleRunning => false;
        public Task StartBle(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopBle(string? reason) => Task.CompletedTask;
        public Task HangUpAll(HubDisconnect disconnect) => Task.CompletedTask;
        public bool OpenMedia(string connectionId, IBleHubMediaChannel channel) => false;
    }


    [Theory]
    [InlineData("/hubs/aaaa", true)]
    [InlineData("/hubs/AAAA/connect", true)]
    [InlineData("/hubs/bbbb", false)]
    [InlineData("/hubs/bbbb/connect", false)]
    [InlineData("/hubs/bbbbx", true)]        // not under a hub - someone else's route
    [InlineData("/hubs/files/avatar.jpg", true)]
    [InlineData("/hubs/media", true)]         // any hub allows it; the media token decides which hub
    [InlineData("/api/status", true)]
    public void TunnelsReachOnlyTheHubsThatAllowThem(string path, bool allowed)
        => Assert.Equal(allowed, TunnelGate.IsAllowed(path, "/hubs", [new FakeBinding("aaaa", null, true), new FakeBinding("bbbb", null)]));


    [Fact]
    public void TunnelsReachNoFilesWhenNoHubAllowsThem()
        => Assert.False(TunnelGate.IsAllowed("/hubs/files/avatar.jpg", "/hubs", [new FakeBinding("aaaa", null), new FakeBinding("bbbb", null)]));


    [Fact]
    public async Task StartingOneHubLeavesTheOtherRefusingClients()
    {
        await this.host.Stop();
        Assert.Equal(HubTransports.Http, await this.host.StartHub<TestHub>(HubTransports.Http));

        Assert.Equal(HubTransports.Http, this.host.GetRunningTransports<TestHub>());
        Assert.Equal(HubTransports.None, this.host.GetRunningTransports<OtherHub>());
        Assert.Equal(HubTransports.Http, this.host.RunningTransports);

        var test = await this.Connect();
        Assert.Equal("Alice", await test.Hub.WhoAmI());

        var other = this.CreateClient<IOtherHub>(OtherCharacteristicUuid);
        var ex = await Assert.ThrowsAsync<BleHubException>(() => other.Connect(HttpHost(), new HubConnectOptions("Bob")));
        Assert.Contains("Hub is not running", ex.Message);
    }


    [Fact]
    public async Task StoppingOneHubLeavesTheOtherRunning()
    {
        var test = await this.Connect();
        var other = this.CreateClient<IOtherHub>(OtherCharacteristicUuid);
        await other.Connect(HttpHost(), new HubConnectOptions("Bob"));
        HubDisconnect? otherLeft = null;
        other.Disconnected += (_, d) => otherLeft = d;

        await this.host.StopHub<OtherHub>("Chat closed");

        await WaitFor(() => otherLeft != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "Chat closed"), otherLeft);
        Assert.Equal(HubTransports.None, this.host.GetRunningTransports<OtherHub>());
        Assert.Equal(HubTransports.Http, this.host.RunningTransports);
        Assert.Equal(new Payload("still", 1), await test.Hub.Echo(new Payload("still", 1)));

        // and back again
        Assert.Equal(HubTransports.Http, await this.host.StartHub<OtherHub>(HubTransports.Http));
        var again = this.CreateClient<IOtherHub>(OtherCharacteristicUuid);
        await again.Connect(HttpHost(), new HubConnectOptions("Bob"));
        Assert.Equal("pong", await again.Hub.Ping());
    }


    [Fact]
    public void HostsCanBeReachedByAddress()
    {
        var host = HubHostInfo.FromUrl(new Uri("https://abc.a.pinggy.link/ignored?x=1"), "game/");

        Assert.Equal(HubTransports.Http, host.Transports);
        Assert.Equal("abc.a.pinggy.link", host.Name);
        Assert.Equal(new Uri("https://abc.a.pinggy.link/game"), host.Http!.BaseAddress);
        Assert.Throws<ArgumentException>(() => HubHostInfo.FromUrl(new Uri("ftp://example.com")));
    }


    [Fact]
    public async Task ConnectsToAHostByAddress()
    {
        var client = this.CreateClient();
        await client.Connect(HubHostInfo.FromUrl(new Uri("http://localhost")), new HubConnectOptions("Alice", "1.0"));

        Assert.Equal(HubTransports.Http, client.Transport);
        Assert.Equal("Alice", await client.Hub.WhoAmI());
    }


    [Fact]
    public void UniversalServerNeedsAHub()
        => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(_ => { }));


    [Fact]
    public void UniversalServerBindsTheNetworkWhenItCreatesTheServer()
    {
        var services = new ServiceCollection().AddUniversalServer(s => s.AddHub<TestHub>(CharacteristicUuid, MdnsServiceType));
        var options = (HttpServerOptions)services.Single(x => x.ServiceType == typeof(HttpServerOptions)).ImplementationInstance!;

        Assert.Equal(IPAddress.Any, options.Address);
        Assert.Equal(0, options.Port);
    }


    [Fact]
    public async Task HttpClientsShowUpInTheHubContext()
    {
        await this.Connect("Alice");
        var context = this.app.Services.GetRequiredService<IHubContext<TestHub>>();

        var client = Assert.Single(context.ConnectedClients);
        Assert.Equal("Alice", client.Name);
        Assert.StartsWith("sb:", client.Id);
    }


    [Fact]
    public async Task HubErrorsKeepTheirTypeAndMessage()
    {
        var client = await this.Connect();
        var ex = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.Hub.Fail());
        Assert.Equal(nameof(InvalidOperationException), ex.RemoteErrorType);
        Assert.Equal("boom", ex.Message);
    }


    [Fact]
    public async Task Streaming()
    {
        var client = await this.Connect();
        var items = new List<int>();
        await foreach (var i in client.Hub.Count(5, CancellationToken.None))
            items.Add(i);

        Assert.Equal([1, 2, 3, 4, 5], items);
    }


    [Fact]
    public async Task StreamErrorsArriveAfterTheItems()
    {
        var client = await this.Connect();
        var items = new List<int>();
        var ex = await Assert.ThrowsAsync<BleHubRemoteException>(async () =>
        {
            await foreach (var i in client.Hub.CountThenFail(3, CancellationToken.None))
                items.Add(i);
        });

        Assert.Equal([1, 2, 3], items);
        Assert.Equal(nameof(ArgumentException), ex.RemoteErrorType);
    }


    [Fact]
    public async Task PushesArriveInOrder()
    {
        var client = await this.Connect();
        var received = new List<int>();
        client.Hub.Numbered += (_, n) =>
        {
            lock (received)
                received.Add(n);
        };

        await client.Hub.PushSequence(50);
        await WaitFor(() =>
        {
            lock (received)
                return received.Count == 50;
        });
        Assert.Equal(Enumerable.Range(0, 50), received);
    }


    [Fact]
    public async Task GroupsAndHubContextPushesReachHttpClients()
    {
        var alice = await this.Connect("Alice");
        var bob = await this.Connect("Bob");
        var aliceMessages = new List<string>();
        var bobMessages = new List<string>();
        alice.Hub.Message += m => aliceMessages.Add(m);
        bob.Hub.Message += m => bobMessages.Add(m);

        await alice.Hub.JoinGroup("room");
        await bob.Hub.SayToGroup("room", "for the room");
        await WaitFor(() => aliceMessages.Count == 1);
        Assert.Empty(bobMessages);

        var context = this.app.Services.GetRequiredService<IHubContext<TestHub>>();
        await context.Clients.All.Message("everyone");
        await WaitFor(() => aliceMessages.Count == 2 && bobMessages.Count == 1);
    }


    [Fact]
    public async Task AbortTakesEffectAfterTheReply()
    {
        var client = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        await client.Hub.Kick("kicked");

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerDisconnect, "kicked"), disconnect);
        await WaitFor(() => this.log.Entries.Contains("disconnected:Alice:kicked"));
        Assert.Contains("reason:Alice:ServerDisconnect", this.log.Entries);
    }


    [Fact]
    public async Task HubContextDisconnectReachesTheClient()
    {
        var client = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        var context = this.app.Services.GetRequiredService<IHubContext<TestHub>>();
        await context.Disconnect(context.ConnectedClients.Single().Id, "Removed by host");

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerDisconnect, "Removed by host"), disconnect);
        Assert.Equal(HubTransports.None, client.Transport);
    }


    [Fact]
    public async Task ClientDisconnectRunsOnDisconnected()
    {
        var client = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;
        await client.Disconnect();

        Assert.Equal(new HubDisconnect(HubDisconnectReason.ClientDisconnect), disconnect);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientDisconnect"));
        Assert.Empty(this.app.Services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients);
    }


    [Fact]
    public async Task HostStopDisconnectsClientsAndRefusesNewOnes()
    {
        var client = await this.Connect();
        HubDisconnect? disconnect = null;
        BleHubStatusChangedEventArgs? disconnected = null;
        client.Disconnected += (_, d) => disconnect = d;
        client.StatusChanged += (_, e) =>
        {
            if (e.Status == BleHubClientStatus.Disconnected)
                disconnected = e;
        };

        await this.host.Stop("Game over");
        Assert.Equal(HubTransports.None, this.host.RunningTransports);

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "Game over"), disconnect);
        Assert.Equal(disconnect, disconnected?.Disconnect);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ServerShutdown"));

        var late = this.CreateClient();
        var ex = await Assert.ThrowsAsync<BleHubException>(() => late.Connect(HttpHost(), new HubConnectOptions("Late")));
        Assert.Contains("Hub is not running", ex.Message);
    }


    [Theory]
    [InlineData(DisconnectReason.ClientClosed, HubDisconnectReason.ClientDisconnect)]
    [InlineData(DisconnectReason.Dropped, HubDisconnectReason.ClientTimeout)]
    [InlineData(DisconnectReason.HungUp, HubDisconnectReason.ServerDisconnect)]
    [InlineData(DisconnectReason.ServerShutdown, HubDisconnectReason.ServerShutdown)]
    [InlineData(DisconnectReason.Faulted, HubDisconnectReason.ConnectionFailed)]
    public void HostMapsHowALineClosed(DisconnectReason line, HubDisconnectReason expected)
        => Assert.Equal(new HubDisconnect(expected, "why"), HubBoard<TestHub>.ToHubDisconnect(new DisconnectContext(line, "why", null)));


    [Theory]
    [InlineData(LineCloseReason.ClientClosed, HubDisconnectReason.ClientDisconnect)]
    [InlineData(LineCloseReason.Dropped, HubDisconnectReason.ClientTimeout)]
    [InlineData(LineCloseReason.RetriesExhausted, HubDisconnectReason.ClientTimeout)]
    [InlineData(LineCloseReason.HungUp, HubDisconnectReason.ServerDisconnect)]
    [InlineData(LineCloseReason.ServerClosed, HubDisconnectReason.ServerShutdown)]
    [InlineData(LineCloseReason.Faulted, HubDisconnectReason.ConnectionFailed)]
    public void ClientMapsHowALineClosed(LineCloseReason line, HubDisconnectReason expected)
        => Assert.Equal(new HubDisconnect(expected), SwitchboardTransport.ToHubDisconnect(line, null));


    [Fact]
    public void HangUpMessagesCarryTheReason()
    {
        var disconnect = new HubDisconnect(HubDisconnectReason.ServerShutdown, "Game over");
        var message = HubJson.HangUpMessage(disconnect);

        Assert.Equal(disconnect, HubJson.ReadHangUp(message));
        // whichever way the line reports the close, the host's own reason wins
        Assert.Equal(disconnect, SwitchboardTransport.ToHubDisconnect(LineCloseReason.HungUp, message));
        Assert.Equal(disconnect, HubBoard<TestHub>.ToHubDisconnect(new DisconnectContext(DisconnectReason.HungUp, message, null)));
        Assert.Null(HubJson.ReadHangUp("plain text"));
        Assert.Null(HubJson.ReadHangUp("""{"reason":99}"""));
    }


    [Fact]
    public async Task MaxClientsRefusesWithoutFallingBack()
    {
        await this.Connect("A");
        await this.Connect("B");
        await this.Connect("C");

        var ex = await Assert.ThrowsAsync<BleHubException>(() => this.Connect("D"));
        Assert.Contains("Host is full", ex.Message);
    }


    [Fact]
    public async Task UnreachableHostFailsCleanly()
    {
        var client = this.CreateClient();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.Connect(HttpHost("/nowhere"), new HubConnectOptions("Alice")));
        Assert.IsNotType<BleHubRemoteException>(ex);
        Assert.Equal(BleHubClientStatus.Disconnected, client.Status);
    }


    [Fact]
    public async Task FilesMoveOverHttp()
    {
        var client = await this.Connect();
        Assert.True(client.CanTransferFiles);

        var transferred = new List<BleHubFileTransferredEventArgs>();
        this.host.FileTransferred += (_, e) => transferred.Add(e);

        var local = Path.GetTempFileName();
        var back = local + ".back";
        try
        {
            await File.WriteAllTextAsync(local, "avatar bytes");
            var up = await client.UploadFile(local, "avatar.jpg");
            Assert.Equal(12, up.BytesTransferred);
            Assert.Equal("avatar bytes", await File.ReadAllTextAsync(Path.Combine(this.filesDirectory, "avatar.jpg")));

            await client.DownloadFile("avatar.jpg", back);
            Assert.Equal("avatar bytes", await File.ReadAllTextAsync(back));

            Assert.Equal(2, transferred.Count);
            Assert.All(transferred, x => Assert.Equal("Alice", x.Client?.Name));
        }
        finally
        {
            File.Delete(local);
            File.Delete(back);
        }
    }


    [Fact]
    public void TransportHttpClientHasNoOverallTimeout()
    {
        // the line's event stream is open for the whole connection - HttpClient's default 100s would cut it
        using var http = SwitchboardTransport.CreateHttpClient(new UniversalHubClientOptions());
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }


    [Fact]
    public async Task FileRulesApply()
    {
        var client = await this.Connect();

        // AuthorizeFile
        using var secret = new MemoryStream("x"u8.ToArray());
        var refused = await Assert.ThrowsAsync<BleHubException>(() => client.UploadStream(secret, 1, "secret.txt"));
        Assert.Contains("403", refused.Message);

        // MaxUploadSize
        using var big = new MemoryStream(new byte[2048]);
        var tooBig = await Assert.ThrowsAsync<BleHubException>(() => client.UploadStream(big, 2048, "big.bin"));
        Assert.Contains("413", tooBig.Message);

        // path traversal never reaches the disk
        using var sneaky = new MemoryStream("x"u8.ToArray());
        await Assert.ThrowsAsync<BleHubException>(() => client.UploadStream(sneaky, 1, "../escape.txt"));
        Assert.False(File.Exists(Path.Combine(this.filesDirectory, "..", "escape.txt")));
    }


    [Fact]
    public async Task FilesNeedAToken()
    {
        using var http = new HttpClient(this.app.Server.CreateInMemoryHandler());
        var response = await http.GetAsync("http://localhost/hubs/files/avatar.jpg");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    [Fact]
    public async Task UnknownMethodsAreReportedLikeBle()
    {
        // a raw Switchboard line, as a client on a different contract version would look
        var line = new SwitchboardLineBuilder()
            .WithUrl($"http://localhost/hubs/{CharacteristicUuid}", o => o.HttpClient = new HttpClient(this.app.Server.CreateInMemoryHandler()))
            .Build();
        await using var _ = line;
        await line.StartAsync();

        var reply = HubJson.ReadHandshakeReply(await line.InvokeCoreAsync<JsonElement>(
            UniversalHubProtocol.HandshakeMethod,
            [HubJson.Serialize(new HubHandshake(UniversalHubProtocol.Version, "Raw", null, null))],
            CancellationToken.None
        ));
        Assert.True(reply.Accepted);
        Assert.Equal("host-1", reply.HostId);

        var envelope = await line.InvokeCoreAsync<JsonElement>(
            UniversalHubProtocol.InvokeMethod,
            ["NoSuchMethod", JsonDocument.Parse("[]").RootElement],
            CancellationToken.None
        );
        var ex = Assert.Throws<BleHubRemoteException>(() => HubJson.ReadResult(envelope));
        Assert.Equal("HubMethodNotFound", ex.RemoteErrorType);
    }


    [Fact]
    public async Task OldProtocolVersionIsRefused()
    {
        var line = new SwitchboardLineBuilder()
            .WithUrl($"http://localhost/hubs/{CharacteristicUuid}", o => o.HttpClient = new HttpClient(this.app.Server.CreateInMemoryHandler()))
            .Build();
        await using var _ = line;
        await line.StartAsync();

        var reply = HubJson.ReadHandshakeReply(await line.InvokeCoreAsync<JsonElement>(
            UniversalHubProtocol.HandshakeMethod,
            [HubJson.Serialize(new HubHandshake(99, "Future", null, null))],
            CancellationToken.None
        ));
        Assert.False(reply.Accepted);
        Assert.Contains("protocol version 99", reply.Reason);
    }
}
