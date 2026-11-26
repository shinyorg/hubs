using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.UniversalHubs;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.UniversalHubs.Tests;

[UniversalHubContract]
public interface IDynamicTestHub
{
    Task<Payload> Echo(Payload payload);
    Task Fail();
    Task Rule();
    Task Kick(string reason);
    Task Missing();
    IAsyncEnumerable<int> Count(int to, CancellationToken cancellationToken);

    event Action<string> Message;
}


/// <summary>
/// Dynamic hubs - defined while the app runs, their methods answered by an <see cref="IDynamicHubHandler"/> with the
/// callers' values untouched - served over HTTP beside a compiled hub
/// </summary>
public class DynamicHubTests : IAsyncLifetime
{
    const string CompiledUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string DynamicUuid = "6e400010-b5a3-f393-e0a9-e50e24dcca9e";
    const string ServiceType = "_dyntest._tcp";

    static DynamicHubTests() => Json.AddContext(TestJsonContext.Default);

    readonly List<ServiceProvider> clientContainers = new();
    readonly ScriptedHandler handler = new();
    TestHttpServer app = null!;
    IDynamicHubs dynamicHubs = null!;
    IDynamicHub hub = null!;


    public async ValueTask InitializeAsync()
    {
        this.app = Build(server => server
            .AllowDynamicHubs()
            .Http(o => o.DynamicServiceTypes.Add(ServiceType))
            .AddHub<TestHub>(CompiledUuid, h => h.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CompiledUuid).UseHttp(ServiceType)));

        this.dynamicHubs = this.app.Services.GetRequiredService<IDynamicHubs>();
        this.hub = this.dynamicHubs.Add(new DynamicHubDefinition("game") { MaxClients = 2 }.UseHttp(ServiceType).UseBluetoothLE(DynamicUuid), this.handler);
        TransportAssert.Equal([typeof(HttpHostTransport)], await this.hub.Start([typeof(HttpHostTransport)]));
    }


    static TestHttpServer Build(Action<UniversalServerBuilder> configure)
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.HideExceptionDetails = false;

        builder.Services.AddSingleton(NoBluetoothHosting.Create());
        builder.Services.AddSingleton<HubLog>();
        builder.Services.AddUniversalServer(server =>
        {
            server
                .Configure(o => o.LocalName = "DynHost")
                .Http(o =>
                {
                    o.Advertise = false;
                    o.ManageHttpServer = false;
                })
                .Configure(o =>
                {
                    o.HostId = "dyn-host";
                });
            configure(server);
        });
        return TestHttpServer.For(builder.Build());
    }


    public async ValueTask DisposeAsync()
    {
        foreach (var c in this.clientContainers)
            await c.DisposeAsync();

        await this.app.DisposeAsync();
    }


    async Task<IUniversalHubClient<TContract>> Connect<TContract>(string hubName, string name = "Alice", TestHttpServer? server = null) where TContract : class
    {
        server ??= this.app;
        var services = new ServiceCollection()
            .AddLogging()
            .AddUniversalHubClient<TContract>(hubName, o =>
            {
                o.UseHttp(ServiceType, h => { h.MessageHandlerFactory = () => server.Server.CreateInMemoryHandler(); h.ConnectTimeout = TimeSpan.FromSeconds(5); });
            })
            .BuildServiceProvider();
        this.clientContainers.Add(services);

        var client = services.GetRequiredService<IUniversalHubClient<TContract>>();
        await client.Connect(Host(hubName), new HubConnectOptions(name, "1.0"));
        return client;
    }


    Task<IUniversalHubClient<IDynamicTestHub>> Connect(string name = "Alice") => this.Connect<IDynamicTestHub>("game", name);


    static HubHostInfo Host(string hubName) => new HubHostInfo("DynHost", "DynHost", "dyn-host").With(
        typeof(HttpClientTransport),
        new HttpHubEndpoint(new DiscoveredHttpServer(
            "DynHost",
            new Uri("http://localhost/hubs"),
            [],
            80,
            new Dictionary<string, string> { ["hubs"] = hubName, ["proto"] = UniversalHubProtocol.Version.ToString(), ["path"] = "/hubs" }
        )),
        []
    );


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public async Task CallsReachTheHandlerWithTheCallersValues()
    {
        var client = await this.Connect();

        Assert.Equal(new Payload("hi", 42), await client.Hub.Echo(new Payload("hi", 42)));

        var call = this.handler.Calls.Single(x => x.Method == "Echo");
        Assert.Equal("Http", call.Transport);
        Assert.Equal("Alice", call.Client);
        // the exact JSON the client's serializer wrote - the host never deserialized it
        Assert.Equal(new Payload("hi", 42), JsonSerializer.Deserialize(call.Arguments.Single(), TestJsonContext.Default.Payload));
    }


    [Fact]
    public async Task CompiledHubsKeepTheirOwnRoutes()
    {
        await this.app.Services.GetRequiredService<IUniversalHubHost>().StartHub<TestHub>([typeof(HttpHostTransport)]);
        var compiled = await this.Connect<ITestHub>(CompiledUuid);
        Assert.Equal(new Payload("compiled", 1), await compiled.Hub.Echo(new Payload("compiled", 1)));
        Assert.DoesNotContain(this.handler.Calls, x => x.Method == "Echo");
    }


    [Fact]
    public async Task StreamsErrorsAndUnknownMethods()
    {
        var client = await this.Connect();

        var items = new List<int>();
        await foreach (var i in client.Hub.Count(3, CancellationToken.None))
            items.Add(i);
        Assert.Equal([1, 2, 3], items);

        var failed = await Assert.ThrowsAsync<HubRemoteException>(() => client.Hub.Fail());
        Assert.Equal(nameof(InvalidOperationException), failed.RemoteErrorType);
        Assert.Equal("boom", failed.Message);

        var rule = await Assert.ThrowsAsync<HubRemoteException>(() => client.Hub.Rule());
        Assert.Equal("GameRule", rule.RemoteErrorType);
        Assert.Equal("Not your turn", rule.Message);

        var missing = await Assert.ThrowsAsync<HubRemoteException>(() => client.Hub.Missing());
        Assert.Equal("HubMethodNotFound", missing.RemoteErrorType);
        Assert.Contains("game has no method 'Missing'", missing.Message);
    }


    [Fact]
    public async Task PushesAndGroupsReachClients()
    {
        var alice = await this.Connect("Alice");
        var bob = await this.Connect("Bob");
        var aliceMessages = new List<string>();
        var bobMessages = new List<string>();
        alice.Hub.Message += m => { lock (aliceMessages) aliceMessages.Add(m); };
        bob.Hub.Message += m => { lock (bobMessages) bobMessages.Add(m); };

        await this.hub.Clients.All.Send("Message", [Utf8("\"everyone\"")]);

        var aliceId = this.hub.Connections.Single(x => x.Client.Name == "Alice").Id;
        await this.hub.Groups.AddToGroupAsync(aliceId, "red");
        await this.hub.Clients.Group("red").Send("Message", [Utf8("\"red team\"")]);

        await WaitFor(() => aliceMessages.Count == 2 && bobMessages.Count == 1);
        Assert.Equal(["everyone", "red team"], aliceMessages);
        Assert.Equal(["everyone"], bobMessages);
    }


    [Fact]
    public async Task TheHandlerSeesClientsComeAndGo()
    {
        var client = await this.Connect("Alice");
        Assert.Contains("connected:Alice:Http", this.handler.Log);
        Assert.Single(this.hub.Connections);

        await client.Hub.Kick("kicked");
        await WaitFor(() => this.handler.Log.Contains("disconnected:Alice:ServerDisconnect:kicked"));
        Assert.Empty(this.hub.Connections);
    }


    [Fact]
    public async Task TheHandlerAndTheHubsRulesCanRefuseAClient()
    {
        this.handler.RefuseWith = "No seats";
        var refused = await Assert.ThrowsAsync<HubException>(() => this.Connect("Carol"));
        Assert.Contains("No seats", refused.Message);

        this.handler.RefuseWith = null;
        await this.Connect("A");
        await this.Connect("B");
        var full = await Assert.ThrowsAsync<HubException>(() => this.Connect("C"));
        Assert.Contains("Host is full", full.Message);
    }


    [Fact]
    public async Task AStoppedOrUnknownHubRefusesClients()
    {
        await this.hub.Stop("closed");
        Assert.Empty(this.hub.RunningTransports);
        var stopped = await Assert.ThrowsAsync<HubException>(() => this.Connect());
        Assert.Contains("not running", stopped.Message);

        var unknown = await Assert.ThrowsAsync<HubException>(() => this.Connect<IDynamicTestHub>("6e400099-b5a3-f393-e0a9-e50e24dcca9e"));
        Assert.Contains("404", unknown.Message);   // a hub this host doesn't have isn't there to refuse anyone
    }


    [Fact]
    public async Task RemovingAHubDisconnectsItsClientsAndFreesItsName()
    {
        var client = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        await this.dynamicHubs.Remove("game", "Game over");

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "Game over"), disconnect);
        Assert.Null(this.dynamicHubs.Find("game"));

        // the same definition can come back - after a web app update, say
        var again = this.dynamicHubs.Add(new DynamicHubDefinition("game").UseHttp(ServiceType).UseBluetoothLE(DynamicUuid), this.handler);
        await again.Start([typeof(HttpHostTransport)]);
        var returning = await this.Connect("Again");
        Assert.Equal(new Payload("back", 2), await returning.Hub.Echo(new Payload("back", 2)));
    }


    [Fact]
    public void DefinitionsAreChecked()
    {
        var other = "6e400011-b5a3-f393-e0a9-e50e24dcca9e";

        // the name is taken - by a dynamic hub, or a compiled one
        Assert.Throws<InvalidOperationException>(() => this.dynamicHubs.Add(new DynamicHubDefinition("game").UseBluetoothLE(other), this.handler));
        Assert.Throws<InvalidOperationException>(() => this.dynamicHubs.Add(new DynamicHubDefinition(CompiledUuid), this.handler));
        Assert.Throws<ArgumentException>(() => this.dynamicHubs.Add(new DynamicHubDefinition("not a name"), this.handler));

        // the characteristic is taken
        Assert.Throws<InvalidOperationException>(() => this.dynamicHubs.Add(new DynamicHubDefinition("chat").UseBluetoothLE(DynamicUuid), this.handler));
        Assert.Throws<InvalidOperationException>(() => this.dynamicHubs.Add(new DynamicHubDefinition("chat").UseBluetoothLE(CompiledUuid), this.handler));
        var type = Assert.Throws<InvalidOperationException>(() => this.dynamicHubs.Add(new DynamicHubDefinition("chat").UseHttp("_other._tcp"), this.handler));
        Assert.Contains("_dyntest._tcp", type.Message);
    }


    [Fact]
    public async Task AtMostMaxHubsMayExist()
    {
        await using var server = Build(s => s.AllowDynamicHubs(o => o.MaxHubs = 1));
        var hubs = server.Services.GetRequiredService<IDynamicHubs>();

        hubs.Add(new DynamicHubDefinition("one"), this.handler);
        var ex = Assert.Throws<InvalidOperationException>(() => hubs.Add(new DynamicHubDefinition("two"), this.handler));
        Assert.Contains("at most 1", ex.Message);
    }


    [Fact]
    public async Task AServerCanStartWithOnlyDynamicHubs()
    {
        await using var server = Build(s => s.AllowDynamicHubs().Http(o => o.DynamicServiceTypes.Add(ServiceType)));
        var hubs = server.Services.GetRequiredService<IDynamicHubs>();
        var handler = new ScriptedHandler();
        var added = hubs.Add(new DynamicHubDefinition("solo").UseHttp(ServiceType), handler);
        TransportAssert.Equal([typeof(HttpHostTransport)], added.RunningTransports.Count == 0 ? await added.Start() : added.RunningTransports);

        var client = await this.Connect<IDynamicTestHub>("solo", server: server);
        Assert.Equal(new Payload("solo", 3), await client.Hub.Echo(new Payload("solo", 3)));
        Assert.Contains(handler.Calls, x => x.Method == "Echo");
    }


    [Fact]
    public void DynamicHubsRegistrationRules()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(_ => { }));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUniversalServer(s => s.AllowDynamicHubs().Http(o => o.DynamicServiceTypes.Add("nope"))));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(s => s.AllowDynamicHubs(o => o.MaxHubs = 0)));
    }


    [Fact]
    public void ADynamicHubCanAllowTunnels()
    {
        this.dynamicHubs.Add(new DynamicHubDefinition("shared").UseHttp(ServiceType, allowTunnel: true), this.handler);
        var host = this.app.Services.GetRequiredService<UniversalHubHost>();

        Assert.True(host.FindByKey("shared")!.GetSettings<HttpHubSettings>()!.AllowTunnel);
        Assert.False(host.FindByKey("game")!.GetSettings<HttpHubSettings>()!.AllowTunnel);
        // no characteristic, so not over BLE
        Assert.DoesNotContain(typeof(BleHostTransport), host.FindByKey("shared")!.Transports);
    }


    IUniversalHubClient<IDynamicHubCalls> CreateDynamicClient(string hubName)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddDynamicHubClients(o => o.UseHttp(configure: h =>
            {
                h.MessageHandlerFactory = () => this.app.Server.CreateInMemoryHandler();
                h.ConnectTimeout = TimeSpan.FromSeconds(5);
            }))
            .BuildServiceProvider();
        this.clientContainers.Add(services);

        // the hub's own service type replaces the shared default
        return services.GetRequiredService<IDynamicHubClients>().Create(hubName, o => o.UseHttp(ServiceType, h =>
        {
            h.MessageHandlerFactory = () => this.app.Server.CreateInMemoryHandler();
            h.ConnectTimeout = TimeSpan.FromSeconds(5);
        }));
    }


    [Fact]
    public async Task ADynamicClientCallsADynamicHubByName()
    {
        var client = this.CreateDynamicClient("game");
        await client.Connect(Host("game"), new HubConnectOptions("Dyna", "1.0"));
        Assert.IsType<HttpClientTransport>(client.Transport);

        var pushes = new List<(string Name, string Args)>();
        client.Hub.Pushed += (name, args) => { lock (pushes) pushes.Add((name, String.Join(",", args.Select(x => Encoding.UTF8.GetString(x.Span))))); };

        var echoed = await client.Hub.Invoke("Echo", [Utf8("""{"Text":"raw","Number":9}""")]);
        Assert.Equal(new Payload("raw", 9), JsonSerializer.Deserialize(echoed.Span, TestJsonContext.Default.Payload));

        var items = new List<string>();
        await foreach (var item in client.Hub.Stream("Count", [Utf8("2")]))
            items.Add(Encoding.UTF8.GetString(item.Span));
        Assert.Equal(["1", "2"], items);

        var failed = await Assert.ThrowsAsync<HubRemoteException>(() => client.Hub.Invoke("Rule", []));
        Assert.Equal("GameRule", failed.RemoteErrorType);

        await this.hub.Clients.All.Send("Message", [Utf8("\"hey\"")]);
        await WaitFor(() => { lock (pushes) return pushes.Count == 1; });
        Assert.Equal(("Message", "\"hey\""), pushes[0]);
    }


    [Fact]
    public async Task ADynamicClientCallsACompiledHubToo()
    {
        await this.app.Services.GetRequiredService<IUniversalHubHost>().StartHub<TestHub>([typeof(HttpHostTransport)]);
        var client = this.CreateDynamicClient(CompiledUuid);
        await client.Connect(Host(CompiledUuid), new HubConnectOptions("Dyna", "1.0"));

        var echoed = await client.Hub.Invoke("Echo", [JsonSerializer.SerializeToUtf8Bytes(new Payload("typed", 3), TestJsonContext.Default.Payload)]);
        Assert.Equal(new Payload("typed", 3), JsonSerializer.Deserialize(echoed.Span, TestJsonContext.Default.Payload));

        // a method returning Task: nothing comes back
        Assert.True((await client.Hub.Invoke("Record", [Utf8("\"x\"")])).IsEmpty);
    }


    static ReadOnlyMemory<byte> Utf8(string json) => Encoding.UTF8.GetBytes(json);


    /// <summary>
    /// Answers like a page would: Echo returns its argument's bytes as they came, Count streams, Fail throws, Kick aborts
    /// </summary>
    internal sealed class ScriptedHandler : IDynamicHubHandler
    {
        readonly List<string> log = new();
        readonly List<(string Method, string? Client, string Transport, byte[][] Arguments)> calls = new();

        public string? RefuseWith { get; set; }

        public IReadOnlyList<string> Log
        {
            get { lock (this.log) return this.log.ToList(); }
        }

        public IReadOnlyList<(string Method, string? Client, string Transport, byte[][] Arguments)> Calls
        {
            get { lock (this.calls) return this.calls.ToList(); }
        }

        public DynamicHubMethodKind GetMethodKind(string method) => method switch
        {
            "Echo" or "Fail" or "Rule" or "Kick" => DynamicHubMethodKind.Invoke,
            "Count" => DynamicHubMethodKind.Stream,
            _ => DynamicHubMethodKind.NotFound
        };

        public Task<byte[]?> Invoke(DynamicHubCall call, CancellationToken cancellationToken)
        {
            this.Record(call);
            switch (call.Method)
            {
                case "Echo":
                    return Task.FromResult<byte[]?>(call.Arguments[0].ToArray());

                case "Kick":
                    call.Abort(JsonSerializer.Deserialize(call.Arguments[0].Span, TestJsonContext.Default.String));
                    return Task.FromResult<byte[]?>(null);

                case "Rule":
                    // a handler relaying someone else's error keeps its type
                    throw new HubRemoteException("GameRule", "Not your turn");

                default:
                    throw new InvalidOperationException("boom");
            }
        }

        public async IAsyncEnumerable<byte[]> Stream(DynamicHubCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            this.Record(call);
            var to = JsonSerializer.Deserialize(call.Arguments[0].Span, TestJsonContext.Default.Int32);
            for (var i = 1; i <= to; i++)
            {
                await Task.Yield();
                yield return Encoding.UTF8.GetBytes(i.ToString());
            }
        }

        public Task OnConnectedAsync(HubConnection connection)
        {
            if (this.RefuseWith != null)
                throw new InvalidOperationException(this.RefuseWith);

            this.Add($"connected:{connection.Client.Name}:{TransportAssert.Name(connection.Transport)}");
            return Task.CompletedTask;
        }

        public Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect)
        {
            this.Add($"disconnected:{connection.Client.Name}:{disconnect.Reason}:{disconnect.Message}");
            return Task.CompletedTask;
        }

        void Add(string entry)
        {
            lock (this.log)
                this.log.Add(entry);
        }

        void Record(DynamicHubCall call)
        {
            lock (this.calls)
                this.calls.Add((call.Method, call.Connection.Client.Name, TransportAssert.Name(call.Connection.Transport), call.Arguments.Select(x => x.ToArray()).ToArray()));
        }
    }
}
