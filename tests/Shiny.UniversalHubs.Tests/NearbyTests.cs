using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE;
using Shiny.UniversalHubs;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// The real UniversalHubs host and client over the Nearby transport - generated proxy, generated hub dispatcher, the
/// peer-link wire, Nearby's channels and framing - on an in-memory Nearby network, beside HTTP clients of the same hubs
/// </summary>
public class NearbyTests : IAsyncLifetime
{
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string OtherCharacteristicUuid = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";
    const string MdnsServiceType = "_nbtest._tcp";
    const string ServiceId = "com.shiny.nearbytest";

    static NearbyTests() => Json.AddContext(TestJsonContext.Default);

    readonly string filesDirectory = Path.Combine(Path.GetTempPath(), "universalhubs-nearby-tests", Guid.NewGuid().ToString("N"));
    readonly List<ServiceProvider> clientContainers = new();
    readonly ConcurrentQueue<HubMediaChannelEventArgs> mediaOpened = new();
    readonly FakeNearbyNetwork network = new();
    FakeNearbyRadio hostRadio = null!;
    TestHttpServer app = null!;
    IUniversalHubHost host = null!;
    HubLog log = null!;


    public async ValueTask InitializeAsync()
    {
        this.hostRadio = this.network.CreateDevice();
        this.app = this.BuildHost(this.hostRadio);
        this.host = this.app.Services.GetRequiredService<IUniversalHubHost>();
        this.log = this.app.Services.GetRequiredService<HubLog>();
        this.app.Services.GetRequiredService<IHubContext<TestHub>>().MediaChannelOpened += (_, e) => this.mediaOpened.Enqueue(e);

        // no Bluetooth in tests - Wi-Fi and Nearby start
        TransportAssert.Equal([typeof(HttpHostTransport), typeof(NearbyHostTransport)], await this.host.Start());
    }


    TestHttpServer BuildHost(INearbyRadio? radio, string serviceId = ServiceId, Action<IServiceCollection>? alsoOnThisDevice = null)
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.HideExceptionDetails = false;

        builder.Services.AddSingleton(NoBluetoothHosting.Create());
        if (radio != null)
            builder.Services.AddSingleton(radio);
        builder.Services.AddSingleton<HubLog>();
        builder.Services.AddUniversalServer(server => server
            .Configure(o =>
            {
                o.LocalName = "TestHost";
                o.EnableFileTransfers(this.filesDirectory, ft => ft.MaxUploadSize = 1024 * 1024);
                o.EnableMediaChannels(maxPacketSize: 64 * 1024);
            })
            .Http(o =>
            {
                o.Advertise = false;
                o.ManageHttpServer = false;
            })
            .Configure(o =>
            {
                o.HostId = "host-1";
                o.AuthorizeFile = req => !req.FileName.StartsWith("secret", StringComparison.Ordinal);
            })
            .Nearby(serviceId)
            .AddHub<TestHub>(CharacteristicUuid, h => h.MaxClients(3).UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CharacteristicUuid).UseHttp(MdnsServiceType).UseNearby())
            .AddHub<OtherHub>(OtherCharacteristicUuid, h => h.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, OtherCharacteristicUuid).UseHttp(MdnsServiceType).UseNearby())
        );
        alsoOnThisDevice?.Invoke(builder.Services);

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


    /// <summary>
    /// A hub client on a device of its own
    /// </summary>
    IUniversalHubClient<TContract> CreateClient<TContract>(string characteristicUuid, FakeNearbyRadio? device = null, bool automaticReconnect = true) where TContract : class
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<INearbyRadio>(device ?? this.network.CreateDevice())
            .AddUniversalHubClient<TContract>(characteristicUuid, o =>
            {
                o.UseHttp(MdnsServiceType, h => { h.MessageHandlerFactory = () => this.app.Server.CreateInMemoryHandler(); }).UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, characteristicUuid);
                o.AutomaticReconnect = automaticReconnect;
                o.UseNearby(ServiceId, nb => nb.EndpointName = "Phone");
            })
            .BuildServiceProvider();

        this.clientContainers.Add(services);
        return services.GetRequiredService<IUniversalHubClient<TContract>>();
    }


    static Task<HubHostInfo> Find<TContract>(IUniversalHubClient<TContract> client) where TContract : class
        => client
            .DiscoverAll([typeof(NearbyClientTransport)])
            .Where(x => x.IsSeenOver<NearbyClientTransport>())
            .FirstAsync()
            .ToTask()
            .WaitAsync(TimeSpan.FromSeconds(5));


    async Task<IUniversalHubClient<ITestHub>> Connect(string name = "Alice", FakeNearbyRadio? device = null, bool automaticReconnect = true)
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid, device, automaticReconnect);
        var found = await Find(client);
        await client.Connect(found, new HubConnectOptions(name, "1.0").Only<NearbyClientTransport>());
        return client;
    }


    async Task<IUniversalHubClient<ITestHub>> ConnectHttp(string name)
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        await client.Connect(HttpHubHost.FromUrl(new Uri("http://localhost")), new HubConnectOptions(name).Only<HttpClientTransport>());
        return client;
    }


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    // ---- hosting and discovery ----

    [Fact]
    public void HostsAdvertiseOnceWithEveryRunningHub()
    {
        Assert.Equal(ServiceId, this.hostRadio.AdvertisingService);
        var info = NearbyProtocol.DecodeEndpointInfo(this.hostRadio.AdvertisedInfo!)!;
        Assert.Equal("host-1", info.HostId);
        Assert.Equal("TestHost", info.Name);
        Assert.Equal(new[] { NearbyProtocol.HubTag(CharacteristicUuid), NearbyProtocol.HubTag(OtherCharacteristicUuid) }.Order(), info.Hubs.Order());
    }


    [Fact]
    public async Task ClientsFindTheHostOverNearby()
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        var found = await Find(client);

        Assert.Equal("TestHost", found.Name);
        Assert.Equal("host-1", found.HostId);
        Assert.Equal([typeof(NearbyClientTransport)], found.Transports);
        Assert.Equal("TestHost", found.Endpoint<NearbyHubEndpoint>()!.EndpointName);
    }


    [Fact]
    public async Task OneHubAtATimeIsReadvertised()
    {
        await this.host.Stop();
        Assert.Null(this.hostRadio.AdvertisingService);

        await this.host.StartHub<TestHub>([typeof(NearbyHostTransport)]);
        TransportAssert.Equal([typeof(NearbyHostTransport)], this.host.GetRunningTransports<TestHub>());
        Assert.Equal([NearbyProtocol.HubTag(CharacteristicUuid)], NearbyProtocol.DecodeEndpointInfo(this.hostRadio.AdvertisedInfo!)!.Hubs);

        // a client of the stopped hub doesn't list the host...
        var other = this.CreateClient<IOtherHub>(OtherCharacteristicUuid);
        var seen = new List<HubHostInfo>();
        using var watch = other.DiscoverAll([typeof(NearbyClientTransport)]).Subscribe(x =>
        {
            lock (seen)
                seen.Add(x);
        });
        await Task.Delay(200);
        lock (seen)
            Assert.DoesNotContain(seen, x => x.IsSeenOver<NearbyClientTransport>());

        // ...until it starts
        await this.host.StartHub<OtherHub>([typeof(NearbyHostTransport)]);
        await WaitFor(() =>
        {
            lock (seen)
                return seen.Any(x => x.IsSeenOver<NearbyClientTransport>());
        });

        HubHostInfo found;
        lock (seen)
            found = seen.Last(x => x.IsSeenOver<NearbyClientTransport>());
        await other.Connect(found, new HubConnectOptions("Bob"));
        Assert.Equal("pong", await other.Hub.Ping());
    }


    [Fact]
    public async Task HostsGoneFromNearbyDropOutOfDiscovery()
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        var seen = new List<HubHostInfo>();
        using var watch = client.DiscoverAll([typeof(NearbyClientTransport)]).Subscribe(x =>
        {
            lock (seen)
                seen.Add(x);
        });
        await WaitFor(() =>
        {
            lock (seen)
                return seen.Any(x => x.IsSeenOver<NearbyClientTransport>());
        });

        // OtherHub still runs, so the host still advertises - but no longer this hub
        await this.host.StopHub<TestHub>();
        await WaitFor(() =>
        {
            lock (seen)
                return seen.Last().Transports.Count == 0;
        });
    }


    [Fact]
    public async Task StartingNearbyAloneWithoutTheRadioFailsAndAllSkipsIt()
    {
        // plain .NET: no Nearby Connections
        await using var server = this.BuildHost(null);
        var plain = server.Services.GetRequiredService<IUniversalHubHost>();

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => plain.Start([typeof(NearbyHostTransport)]));
        TransportAssert.Equal([typeof(HttpHostTransport)], await plain.Start());
    }


    // ---- calls ----

    [Fact]
    public async Task ConnectsOverNearbyAndCallsWork()
    {
        var client = await this.Connect();

        Assert.IsType<NearbyClientTransport>(client.Transport);
        Assert.Equal(HubClientStatus.Connected, client.Status);
        Assert.Equal("TestHost", client.HostName);

        Assert.Equal(new Payload("hi", 42), await client.Hub.Echo(new Payload("hi", 42)));
        Assert.Equal("Alice", await client.Hub.WhoAmI());

        await client.Hub.Record("void works");
        Assert.Contains("record:void works", this.log.Entries);
    }


    [Fact]
    public async Task HubsSeeANearbyConnectionAndItsPeer()
    {
        var client = await this.Connect();
        var id = Assert.Single(this.app.Services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients).Id;

        Assert.StartsWith("nb:", id);
        Assert.IsType<NearbyHostTransport>(HubConnection.From(Assert.Single(this.app.Services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients)).Transport);
        Assert.Contains($"connected-via:Nearby:{id}", this.log.Entries);
        Assert.Contains("connected-peer:Phone", this.log.Entries);
        Assert.Equal($"Nearby:{id}", await client.Hub.MyConnection());
    }


    [Fact]
    public async Task HubErrorsKeepTheirTypeAndMessage()
    {
        var client = await this.Connect();
        var ex = await Assert.ThrowsAsync<HubRemoteException>(() => client.Hub.Fail());
        Assert.Equal(nameof(InvalidOperationException), ex.RemoteErrorType);
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
        lock (received)
            Assert.Equal(Enumerable.Range(0, 50), received);
    }


    [Fact]
    public async Task MessagesOverNearbysPayloadLimitArriveWhole()
    {
        var client = await this.Connect();
        var text = new string('x', 100_000);

        var echoed = await client.Hub.Echo(new Payload(text, 1));
        Assert.Equal(text, echoed.Text);

        // the fake refuses anything over 32 KB, so the call above only works in pieces
        lock (this.network.PayloadSizes)
            Assert.All(this.network.PayloadSizes, x => Assert.True(x <= NearbyProtocol.MaxBytesPayloadSize));
    }


    [Fact]
    public async Task GroupsSpanNearbyAndHttpClients()
    {
        var nearby = await this.Connect("Alice");
        var http = await this.ConnectHttp("Bob");
        var nearbyGot = new List<string>();
        var httpGot = new List<string>();
        nearby.Hub.Message += m => { lock (nearbyGot) nearbyGot.Add(m); };
        http.Hub.Message += m => { lock (httpGot) httpGot.Add(m); };

        await nearby.Hub.JoinGroup("table");
        await http.Hub.JoinGroup("table");
        await http.Hub.SayToGroup("table", "deal");

        await WaitFor(() =>
        {
            lock (nearbyGot)
            lock (httpGot)
                return nearbyGot.Count == 1 && httpGot.Count == 1;
        });
    }


    [Fact]
    public async Task MaxClientsCountsEveryTransport()
    {
        await this.Connect("A");
        await this.ConnectHttp("B");
        await this.Connect("C");

        var ex = await Assert.ThrowsAsync<HubException>(() => this.Connect("D"));
        Assert.Contains("Host is full", ex.Message);
    }


    // ---- one connection per device ----

    [Fact]
    public async Task TwoHubsFromOneDeviceShareItsConnection()
    {
        // one app with clients of both hubs - one Nearby link, so one connection
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<INearbyRadio>(this.network.CreateDevice())
            .AddUniversalHubClient<ITestHub>(CharacteristicUuid, o => o.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CharacteristicUuid).UseHttp(MdnsServiceType).UseNearby(ServiceId))
            .AddUniversalHubClient<IOtherHub>(OtherCharacteristicUuid, o => o.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, OtherCharacteristicUuid).UseHttp(MdnsServiceType).UseNearby(ServiceId))
            .BuildServiceProvider();
        this.clientContainers.Add(services);

        var test = services.GetRequiredService<IUniversalHubClient<ITestHub>>();
        await test.Connect(await Find(test), new HubConnectOptions("Alice").Only<NearbyClientTransport>());
        var other = services.GetRequiredService<IUniversalHubClient<IOtherHub>>();
        await other.Connect(await Find(other), new HubConnectOptions("Alice").Only<NearbyClientTransport>());

        Assert.Equal("Alice", await test.Hub.WhoAmI());
        Assert.Equal("pong", await other.Hub.Ping());
        Assert.Single(this.network.Connections);

        // one hub leaving keeps the other's session
        await other.Disconnect();
        await Task.Delay(100);
        Assert.Equal("Alice", await test.Hub.WhoAmI());
        Assert.Single(this.network.Connections);
    }


    [Fact]
    public async Task TheConnectionEndsSoonAfterItsLastChannel()
    {
        var client = await this.Connect();
        Assert.Single(this.network.Connections);

        await client.Disconnect();
        await WaitFor(() => this.network.Connections.Count == 0);
    }


    [Fact]
    public async Task TwoDevicesHostingEachOtherShareOneConnection()
    {
        // the chat sample: both phones host, and each visits the other
        var otherRadio = this.network.CreateDevice();
        await using var otherApp = this.BuildHost(otherRadio, alsoOnThisDevice: s => s.AddUniversalHubClient<ITestHub>(CharacteristicUuid, o => o.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CharacteristicUuid).UseHttp(MdnsServiceType).UseNearby(ServiceId)));
        await otherApp.Services.GetRequiredService<IUniversalHubHost>().Start();

        // the other phone visits TestHost...
        var visitor = otherApp.Services.GetRequiredService<IUniversalHubClient<ITestHub>>();
        await visitor.Connect(await Find(visitor), new HubConnectOptions("Visiting").Only<NearbyClientTransport>());

        // ...and TestHost visits it back, over the same connection
        var back = await this.FindFrom(otherRadio.EndpointId, otherApp);
        await back.Client.Connect(back.Host, new HubConnectOptions("Visiting back").Only<NearbyClientTransport>());

        Assert.Equal("Visiting", await visitor.Hub.WhoAmI());
        Assert.Equal("Visiting back", await back.Client.Hub.WhoAmI());
        Assert.Single(this.network.Connections);
    }


    /// <summary>
    /// A client on TestHost's own device (sharing its radio and link), and the host it finds at <paramref name="endpointId"/>
    /// </summary>
    async Task<(IUniversalHubClient<ITestHub> Client, HubHostInfo Host)> FindFrom(string endpointId, TestHttpServer target)
    {
        // TestHost's container has no hub client, so build one beside it on the same link
        var link = this.app.Services.GetRequiredService<NearbyLink>();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(link)
            .AddUniversalHubClient<ITestHub>(CharacteristicUuid, o => o.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CharacteristicUuid).UseHttp(MdnsServiceType).UseNearby(ServiceId))
            .BuildServiceProvider();
        this.clientContainers.Add(services);

        var client = services.GetRequiredService<IUniversalHubClient<ITestHub>>();
        var host = await client
            .DiscoverAll([typeof(NearbyClientTransport)])
            .Where(x => x.Endpoint<NearbyHubEndpoint>()?.EndpointId == endpointId)
            .FirstAsync()
            .ToTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        return (client, host);
    }


    // ---- disconnects ----

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
        Assert.Contains("disconnected-peer:Phone", this.log.Entries);
        Assert.Empty(this.app.Services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients);
    }


    [Fact]
    public async Task ALostConnectionIsAClientTimeoutOnBothEndsWithoutAutomaticReconnect()
    {
        var client = await this.Connect(automaticReconnect: false);
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        this.hostRadio.DropAll();

        await WaitFor(() => disconnect != null);
        Assert.Equal(HubDisconnectReason.ClientTimeout, disconnect!.Reason);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientTimeout"));
    }


    [Fact]
    public async Task ALostConnectionResumesAsTheSameConnection()
    {
        var client = await this.Connect();
        var context = this.app.Services.GetRequiredService<IHubContext<TestHub>>();
        var id = Assert.Single(context.ConnectedClients).Id;
        var messages = new List<string>();
        HubDisconnect? disconnected = null;
        client.Disconnected += (_, d) => disconnected = d;
        client.Hub.Message += m => { lock (messages) messages.Add(m); };

        this.network.Partitioned = true;
        this.hostRadio.DropAll();
        await WaitFor(() => client.Status == HubClientStatus.Reconnecting);
        await WaitFor(() => this.network.Connections.Count == 0);
        await context.Clients.All.Message("while away");

        this.network.Partitioned = false;
        await WaitFor(() => client.Status == HubClientStatus.Connected && messages.Count == 1);

        Assert.Equal(["while away"], messages);
        Assert.Null(disconnected);
        Assert.Equal(id, Assert.Single(context.ConnectedClients).Id);
        Assert.Equal(new Payload("back", 1), await client.Hub.Echo(new Payload("back", 1)));
        Assert.DoesNotContain(this.log.Entries, x => x.StartsWith("disconnected:", StringComparison.Ordinal));
    }


    [Fact]
    public async Task HostStopDisconnectsClientsAndStopsAdvertising()
    {
        var client = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        await this.host.Stop("Game over");
        Assert.Empty(this.host.RunningTransports);
        Assert.Null(this.hostRadio.AdvertisingService);

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "Game over"), disconnect);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ServerShutdown"));
    }


    [Fact]
    public async Task AStoppedHubRefusesAHandshake()
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        var found = await Find(client);

        // the advertisement is still up for OtherHub, so the channel opens - the handshake is refused
        await this.host.StopHub<TestHub>();
        var ex = await Assert.ThrowsAsync<HubException>(() => client.Connect(found, new HubConnectOptions("Alice").Only<NearbyClientTransport>()));
        Assert.Contains("Hub is not running", ex.Message);
    }


    // ---- files ----

    [Fact]
    public async Task FilesMoveOverNearby()
    {
        var client = await this.Connect();
        Assert.True(client.CanTransferFiles);

        var local = Path.GetTempFileName();
        var back = local + ".back";
        try
        {
            // bigger than one BYTES payload - files are file payloads, not messages
            var bytes = new byte[100_000];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(local, bytes);

            var up = await client.UploadFile(local, "avatar.jpg");
            Assert.Equal(bytes.Length, up.BytesTransferred);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(this.filesDirectory, "avatar.jpg")));

            await client.DownloadFile("avatar.jpg", back);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(back));
        }
        finally
        {
            File.Delete(local);
            File.Delete(back);
        }
    }


    [Fact]
    public async Task FileRulesApply()
    {
        var client = await this.Connect();

        using var secret = new MemoryStream("x"u8.ToArray());
        var refused = await Assert.ThrowsAsync<HubException>(() => client.UploadStream(secret, 1, "secret.txt"));
        Assert.Contains("403", refused.Message);

        var local = Path.GetTempFileName();
        try
        {
            var missing = await Assert.ThrowsAsync<HubException>(() => client.DownloadFile("nope.jpg", local));
            Assert.Contains("404", missing.Message);
        }
        finally
        {
            File.Delete(local);
        }
    }


    // ---- media channels ----

    [Fact]
    public async Task MediaChannelsCarryPacketsBothWays()
    {
        var client = await this.Connect();
        Assert.True(client.CanOpenMediaChannel);

        var channel = await client.OpenMediaChannel();
        await WaitFor(() => !this.mediaOpened.IsEmpty);
        Assert.True(this.mediaOpened.TryDequeue(out var opened));
        Assert.StartsWith("nb:", opened.Channel.ConnectionId);

        await using var hostReader = opened.Channel.Receive().GetAsyncEnumerator();
        await channel.Send(new byte[] { 1, 2, 3 });
        Assert.True(await hostReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[] { 1, 2, 3 }, hostReader.Current.ToArray());

        // a packet over Nearby's payload limit
        var big = new byte[60 * 1024];
        Random.Shared.NextBytes(big);
        await using var clientReader = channel.Receive().GetAsyncEnumerator();
        await opened.Channel.Send(big);
        Assert.True(await clientReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(big, clientReader.Current.ToArray());

        await channel.DisposeAsync();
        await opened.Channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }


    // ---- the wire ----

    [Fact]
    public async Task OldVersionsAreRefusedAtTheHandshake()
    {
        var raw = await RawSession.Open(this.network, this.hostRadio);

        raw.Send(PeerProtocol.Encode(PeerMessageKind.Handshake, 1, CharacteristicUuid, new HubHandshake(99, "Future", null, null), PeerJsonContext.Default.HubHandshake));
        var refused = (await raw.Next()).Read(PeerJsonContext.Default.PeerHandshakeReply);
        Assert.False(refused.Accepted);
        Assert.Contains("protocol version 99", refused.Reason);
    }


    [Fact]
    public void EndpointInfoRoundTripsAndFitsNearbysLimit()
    {
        var hubs = Enumerable.Range(0, 5).Select(x => NearbyProtocol.HubTag($"6e4000{x:00}-b5a3-f393-e0a9-e50e24dcca9e")).ToList();
        var bytes = NearbyProtocol.EncodeEndpointInfo(new NearbyEndpointInfo(Guid.NewGuid().ToString("N"), new string('n', 500), hubs));

        Assert.Equal(NearbyProtocol.MaxEndpointInfoLength, bytes.Length);
        var info = NearbyProtocol.DecodeEndpointInfo(bytes)!;
        Assert.Equal(hubs, info.Hubs);
        Assert.StartsWith("nnnn", info.Name);

        // other apps advertising under the same service id, and other versions, are not hosts
        Assert.Null(NearbyProtocol.DecodeEndpointInfo("hello"u8));
        Assert.Null(NearbyProtocol.DecodeEndpointInfo([99, 0, 0]));
    }


    [Fact]
    public void TooManyHubsForTheEndpointInfoIsAClearError()
    {
        var hubs = Enumerable.Range(0, 40).Select(x => (uint)x).ToList();
        Assert.Throws<InvalidOperationException>(() => NearbyProtocol.EncodeEndpointInfo(new NearbyEndpointInfo("host", "name", hubs)));
    }


    [Fact]
    public void LongMessagesSplitIntoFramesUnderTheLimit()
    {
        var message = new byte[NearbyProtocol.MaxBytesPayloadSize * 2 + 10];
        var frames = NearbyProtocol.Split(true, 3, message).ToList();

        Assert.Equal(3, frames.Count);
        Assert.All(frames, x => Assert.True(x.Length <= NearbyProtocol.MaxBytesPayloadSize));
        Assert.Equal(NearbyFrameKind.Part, NearbyProtocol.ReadFrame(frames[0]).Kind);
        Assert.Equal(NearbyFrameKind.Last, NearbyProtocol.ReadFrame(frames[2]).Kind);
        Assert.Equal(message.Length, frames.Sum(x => x.Length - NearbyProtocol.FrameHeaderSize));

        var single = NearbyProtocol.ReadFrame(NearbyProtocol.Split(false, 9, [1, 2]).Single());
        Assert.Equal((NearbyFrameKind.Message, false, 9u), (single.Kind, single.SenderOpened, single.Channel));
    }


    [Fact]
    public void BonjourServicesMatchNearbysServiceType()
    {
        // pinned in Google's own tests: GenerateServiceType("A") == "_559AEAD08264._tcp."
        Assert.Equal(["_559AEAD08264._tcp"], NearbyProtocol.BonjourServices("A"));
    }


    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("ünicode")]
    public void BadServiceIdsAreRejected(string serviceId)
        => Assert.Throws<ArgumentException>(() => NearbyProtocol.ValidateServiceId(serviceId));


    [Fact]
    public void NearbyIsSetUpOncePerServerAndOncePerClient()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddUniversalServer(server => server.Nearby(ServiceId).Nearby(ServiceId).AddHub<TestHub>(CharacteristicUuid, h => h.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CharacteristicUuid).UseHttp(MdnsServiceType).UseNearby())));

        // Multipeer and Nearby side by side is fine - the same one twice is not
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalHubClient<ITestHub>(CharacteristicUuid, o => o.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CharacteristicUuid).UseHttp(MdnsServiceType).UseMultipeer("x-mc").UseNearby(ServiceId).UseNearby(ServiceId)));
    }


    [Fact]
    public async Task HostAndClientsMustShareTheServiceId()
    {
        var device = this.network.CreateDevice();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<INearbyRadio>(device)
            .AddUniversalHubClient<ITestHub>(CharacteristicUuid, o => o.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CharacteristicUuid).UseHttp(MdnsServiceType).UseNearby(ServiceId))
            .AddUniversalHubClient<IOtherHub>(OtherCharacteristicUuid, o => o.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, OtherCharacteristicUuid).UseHttp(MdnsServiceType).UseNearby("com.someone.else"))
            .BuildServiceProvider();
        this.clientContainers.Add(services);

        using var first = services.GetRequiredService<IUniversalHubClient<ITestHub>>().DiscoverAll([typeof(NearbyClientTransport)]).Subscribe(_ => { });
        var link = services.GetRequiredService<NearbyLink>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => link.Discover("com.someone.else", NearbyStrategy.Cluster).FirstAsync().ToTask());
        Assert.Contains(ServiceId, ex.Message);
    }


    /// <summary>
    /// A channel to the host with no hub client on top - what a client on another protocol version would look like
    /// </summary>
    sealed class RawSession : IPeerConnectionHandler
    {
        readonly Channel<PeerMessage> received = Channel.CreateUnbounded<PeerMessage>();
        IPeerConnection connection = null!;

        public static async Task<RawSession> Open(FakeNearbyNetwork network, FakeNearbyRadio host)
        {
            var link = new NearbyLink(network.CreateDevice());
            var session = new RawSession();
            session.connection = await link.Connect(host.EndpointId, "Raw", TimeSpan.FromSeconds(5), CancellationToken.None);
            session.connection.Start(session);
            return session;
        }

        public void Send(byte[] message) => this.connection.Send(message);

        public async Task<PeerMessage> Next() => await this.received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public void Received(ReadOnlyMemory<byte> message) => this.received.Writer.TryWrite(PeerProtocol.Decode(message));
        public void FileProgress(string name, double fraction) { }
        public void FileReceived(string name, string? path, Exception? error) { }
        public void Closed() => this.received.Writer.TryComplete();
    }
}
