using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// The real UniversalHubs host and client over the Multipeer transport - generated proxy, generated hub dispatcher, the
/// Multipeer wire - on an in-memory Multipeer network, beside HTTP clients of the same hubs
/// </summary>
public class MultipeerTests : IAsyncLifetime
{
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string OtherCharacteristicUuid = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";
    const string MdnsServiceType = "_mctest._tcp";
    const string ServiceType = "mctest-mc";

    static MultipeerTests() => Json.AddContext(TestJsonContext.Default);

    readonly string filesDirectory = Path.Combine(Path.GetTempPath(), "universalhubs-multipeer-tests", Guid.NewGuid().ToString("N"));
    readonly List<ServiceProvider> clientContainers = new();
    readonly ConcurrentQueue<BleHubMediaChannelEventArgs> mediaOpened = new();
    readonly FakeMultipeerNetwork network = new();
    TestHttpServer app = null!;
    IUniversalHubHost host = null!;
    HubLog log = null!;


    public async ValueTask InitializeAsync()
    {
        this.app = this.BuildHost(this.network);
        this.host = this.app.Services.GetRequiredService<IUniversalHubHost>();
        this.log = this.app.Services.GetRequiredService<HubLog>();
        this.app.Services.GetRequiredService<IHubContext<TestHub>>().MediaChannelOpened += (_, e) => this.mediaOpened.Enqueue(e);

        // no Bluetooth in tests - Wi-Fi and Multipeer start
        Assert.Equal(HubTransports.Http | HubTransports.Multipeer, await this.host.Start());
    }


    TestHttpServer BuildHost(IMultipeerLink? link)
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.HideExceptionDetails = false;

        builder.Services.AddSingleton(NoBluetoothHosting.Create());
        if (link != null)
            builder.Services.AddSingleton(link);
        builder.Services.AddSingleton<HubLog>();
        builder.Services.AddUniversalServer(server => server
            .Ble(o =>
            {
                o.LocalName = "TestHost";
                o.EnableFileTransfers(this.filesDirectory, ft => ft.MaxUploadSize = 1024);
                o.EnableMediaChannels(maxPacketSize: 64 * 1024);
            })
            .Http(o =>
            {
                o.Advertise = false;
                o.ManageHttpServer = false;
                o.HostId = "host-1";
                o.AuthorizeFile = req => !req.FileName.StartsWith("secret", StringComparison.Ordinal);
            })
            .Multipeer(ServiceType)
            .AddHub<TestHub>(CharacteristicUuid, MdnsServiceType, o => o.MaxClients = 3)
            .AddHub<OtherHub>(OtherCharacteristicUuid, MdnsServiceType)
        );

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


    IUniversalHubClient<TContract> CreateClient<TContract>(string characteristicUuid) where TContract : class
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IMultipeerLink>(this.network)
            .AddUniversalHubClient<TContract>(characteristicUuid, MdnsServiceType, o =>
            {
                o.HttpMessageHandlerFactory = () => this.app.Server.CreateInMemoryHandler();
                o.Multipeer(ServiceType, mc => mc.DisplayName = "Phone");
            })
            .BuildServiceProvider();

        this.clientContainers.Add(services);
        return services.GetRequiredService<IUniversalHubClient<TContract>>();
    }


    static Task<HubHostInfo> Find<TContract>(IUniversalHubClient<TContract> client) where TContract : class
        => client
            .DiscoverAll(HubTransports.Multipeer)
            .Where(x => x.Multipeer != null)
            .FirstAsync()
            .ToTask()
            .WaitAsync(TimeSpan.FromSeconds(5));


    async Task<IUniversalHubClient<ITestHub>> Connect(string name = "Alice")
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        var found = await Find(client);
        await client.Connect(found, new HubConnectOptions(name, "1.0") { Allowed = HubTransports.Multipeer });
        return client;
    }


    async Task<IUniversalHubClient<ITestHub>> ConnectHttp(string name)
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        await client.Connect(HubHostInfo.FromUrl(new Uri("http://localhost")), new HubConnectOptions(name) { Allowed = HubTransports.Http });
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
        var ad = Assert.Single(this.network.Advertisements);
        Assert.Equal(ServiceType, ad.ServiceType);
        Assert.Equal("TestHost", ad.DisplayName);
        Assert.Equal("host-1", ad.Info[MultipeerProtocol.InfoHostId]);
        Assert.Equal("TestHost", ad.Info[MultipeerProtocol.InfoName]);
        Assert.Equal(MultipeerProtocol.Version.ToString(), ad.Info[MultipeerProtocol.InfoProtocol]);
        Assert.Equal(new[] { CharacteristicUuid, OtherCharacteristicUuid }.Order(), ad.Info[MultipeerProtocol.InfoHubs].Split(',').Order());
    }


    [Fact]
    public async Task ClientsFindTheHostOverMultipeer()
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        var found = await Find(client);

        Assert.Equal("TestHost", found.Name);
        Assert.Equal("host-1", found.HostId);
        Assert.Equal(HubTransports.Multipeer, found.Transports);
        Assert.Equal("TestHost", found.Multipeer!.DisplayName);
    }


    [Fact]
    public async Task OneHubAtATimeIsReadvertised()
    {
        await this.host.Stop();
        Assert.Empty(this.network.Advertisements);

        await this.host.StartHub<TestHub>(HubTransports.Multipeer);
        Assert.Equal(HubTransports.Multipeer, this.host.GetRunningTransports<TestHub>());
        Assert.Equal(HubTransports.None, this.host.GetRunningTransports<OtherHub>());
        Assert.Equal(CharacteristicUuid, Assert.Single(this.network.Advertisements).Info[MultipeerProtocol.InfoHubs]);

        // a client of the stopped hub doesn't list the host...
        var other = this.CreateClient<IOtherHub>(OtherCharacteristicUuid);
        var seen = new List<HubHostInfo>();
        using var watch = other.DiscoverAll(HubTransports.Multipeer).Subscribe(seen.Add);
        await Task.Delay(200);
        Assert.DoesNotContain(seen, x => x.Multipeer != null);

        // ...until it starts
        await this.host.StartHub<OtherHub>(HubTransports.Multipeer);
        await WaitFor(() => seen.Any(x => x.Multipeer != null));

        var found = seen.Last(x => x.Multipeer != null);
        await other.Connect(found, new HubConnectOptions("Bob"));
        Assert.Equal("pong", await other.Hub.Ping());
    }


    [Fact]
    public async Task HostsGoneFromMultipeerDropOutOfDiscovery()
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        var seen = new List<HubHostInfo>();
        using var watch = client.DiscoverAll(HubTransports.Multipeer).Subscribe(x =>
        {
            lock (seen)
                seen.Add(x);
        });
        await WaitFor(() =>
        {
            lock (seen)
                return seen.Any(x => x.Multipeer != null);
        });

        await this.host.StopHub<TestHub>();
        await WaitFor(() =>
        {
            lock (seen)
                return seen.Last().Transports == HubTransports.None;
        });
    }


    [Fact]
    public async Task StartingMultipeerAloneWithoutTheLinkFailsAndAllSkipsIt()
    {
        // plain .NET: no MultipeerConnectivity
        await using var server = this.BuildHost(null);
        var plain = server.Services.GetRequiredService<IUniversalHubHost>();

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => plain.Start(HubTransports.Multipeer));
        Assert.Equal(HubTransports.Http, await plain.Start());
    }


    // ---- calls ----

    [Fact]
    public async Task ConnectsOverMultipeerAndCallsWork()
    {
        var client = await this.Connect();

        Assert.Equal(HubTransports.Multipeer, client.Transport);
        Assert.Equal(BleHubClientStatus.Connected, client.Status);
        Assert.Equal("TestHost", client.HostName);

        Assert.Equal(new Payload("hi", 42), await client.Hub.Echo(new Payload("hi", 42)));
        Assert.Equal("Alice", await client.Hub.WhoAmI());

        await client.Hub.Record("void works");
        Assert.Contains("record:void works", this.log.Entries);
    }


    [Fact]
    public async Task HubsSeeAMultipeerConnectionAndItsPeer()
    {
        var client = await this.Connect();
        var id = Assert.Single(this.app.Services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients).Id;

        Assert.StartsWith("mc:", id);
        Assert.Equal(HubTransports.Multipeer, HubConnection.GetTransport(id));
        Assert.Contains($"connected-via:Multipeer:{id}", this.log.Entries);
        Assert.Contains("connected-peer:Phone", this.log.Entries);
        Assert.Equal($"Multipeer:{id}", await client.Hub.MyConnection());
        Assert.Equal("hello Alice|line:False|request:|ble:none", await client.Hub.Inspect());
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
    public async Task GroupsSpanMultipeerAndHttpClients()
    {
        var alice = await this.Connect("Alice");
        var bob = await this.ConnectHttp("Bob");
        Assert.Equal(HubTransports.Http, bob.Transport);

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
    public async Task MaxClientsCountsEveryTransport()
    {
        await this.Connect("A");
        await this.ConnectHttp("B");
        await this.Connect("C");

        var ex = await Assert.ThrowsAsync<BleHubException>(() => this.Connect("D"));
        Assert.Contains("Host is full", ex.Message);
    }


    [Fact]
    public async Task ClientsCanRenameThemselves()
    {
        // something the Wi-Fi transport can't do yet
        var client = await this.Connect();
        await client.Rename("Alicia");

        Assert.Equal("Alicia", await client.Hub.WhoAmI());
        Assert.Equal("Alicia", client.ClientName);
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
        Assert.Contains("disconnected-peer:Phone", this.log.Entries);   // the peer is still known in OnDisconnectedAsync
        Assert.Empty(this.app.Services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients);
    }


    [Fact]
    public async Task ALostSessionIsAClientTimeoutOnBothEnds()
    {
        var client = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        this.network.Sessions.Single().Host.Drop();

        await WaitFor(() => disconnect != null);
        Assert.Equal(HubDisconnectReason.ClientTimeout, disconnect!.Reason);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientTimeout"));
    }


    [Fact]
    public async Task HostStopDisconnectsClientsAndRefusesNewOnes()
    {
        var client = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        // a host found before the stop, joined after it
        var late = this.CreateClient<ITestHub>(CharacteristicUuid);
        var stale = await Find(late);

        await this.host.Stop("Game over");
        Assert.Equal(HubTransports.None, this.host.RunningTransports);
        Assert.Empty(this.network.Advertisements);

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "Game over"), disconnect);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ServerShutdown"));

        await Assert.ThrowsAnyAsync<Exception>(() => late.Connect(stale, new HubConnectOptions("Late") { Allowed = HubTransports.Multipeer }));
        Assert.Equal(BleHubClientStatus.Disconnected, late.Status);
    }


    [Fact]
    public async Task AStoppedHubRefusesAHandshake()
    {
        var client = this.CreateClient<ITestHub>(CharacteristicUuid);
        var found = await Find(client);

        // the advertisement is still up for OtherHub, so the invitation is accepted - the handshake is refused
        await this.host.StopHub<TestHub>();
        var ex = await Assert.ThrowsAsync<BleHubException>(() => client.Connect(found, new HubConnectOptions("Alice") { Allowed = HubTransports.Multipeer }));
        Assert.Contains("Hub is not running", ex.Message);
    }


    // ---- files ----

    [Fact]
    public async Task FilesMoveOverMultipeer()
    {
        var client = await this.Connect();
        Assert.True(client.CanTransferFiles);

        var transferred = new List<BleHubFileTransferredEventArgs>();
        this.host.FileTransferred += (_, e) =>
        {
            lock (transferred)
                transferred.Add(e);
        };

        var local = Path.GetTempFileName();
        var back = local + ".back";
        try
        {
            await File.WriteAllTextAsync(local, "avatar bytes");
            var progress = new List<TransferProgress>();
            var up = await client.UploadFile(local, "avatar.jpg", new SyncProgress(progress));
            Assert.Equal(12, up.BytesTransferred);
            Assert.Equal(12, progress.Last().BytesTransferred);
            Assert.Equal("avatar bytes", await File.ReadAllTextAsync(Path.Combine(this.filesDirectory, "avatar.jpg")));

            await client.DownloadFile("avatar.jpg", back);
            Assert.Equal("avatar bytes", await File.ReadAllTextAsync(back));

            await WaitFor(() =>
            {
                lock (transferred)
                    return transferred.Count == 2;
            });
            Assert.All(transferred, x => Assert.Equal("Alice", x.Client?.Name));
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

        // missing files
        var local = Path.GetTempFileName();
        try
        {
            var missing = await Assert.ThrowsAsync<BleHubException>(() => client.DownloadFile("nope.jpg", local));
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
        Assert.Equal("Alice", opened.Client.Name);
        Assert.StartsWith("mc:", opened.Channel.ConnectionId);

        await using var hostReader = opened.Channel.Receive().GetAsyncEnumerator();
        await channel.Send(new byte[] { 1, 2, 3 });
        Assert.True(await hostReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[] { 1, 2, 3 }, hostReader.Current.ToArray());

        await using var clientReader = channel.Receive().GetAsyncEnumerator();
        await opened.Channel.Send(new byte[] { 9, 8 });
        Assert.True(await clientReader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new byte[] { 9, 8 }, clientReader.Current.ToArray());

        // closing one end closes the other
        await channel.DisposeAsync();
        await opened.Channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }


    [Fact]
    public async Task MediaChannelsCloseWithTheConnection()
    {
        var client = await this.Connect();
        var channel = await client.OpenMediaChannel();
        await WaitFor(() => !this.mediaOpened.IsEmpty);
        this.mediaOpened.TryDequeue(out var opened);

        await client.Disconnect();
        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await opened!.Channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }


    // ---- the wire ----

    [Fact]
    public async Task UnknownMethodsAndOldVersionsAreRefusedLikeTheOtherTransports()
    {
        var raw = await RawSession.Open(this.network);

        raw.Send(MultipeerProtocol.Encode(MultipeerMessageKind.Handshake, 1, CharacteristicUuid, new HubHandshake(99, "Future", null, null), MultipeerJsonContext.Default.HubHandshake));
        var refused = (await raw.Next()).Read(MultipeerJsonContext.Default.MultipeerHandshakeReply);
        Assert.False(refused.Accepted);
        Assert.Contains("protocol version 99", refused.Reason);

        raw = await RawSession.Open(this.network);
        raw.Send(MultipeerProtocol.Encode(MultipeerMessageKind.Handshake, 1, CharacteristicUuid, new HubHandshake(MultipeerProtocol.Version, "Raw", null, null), MultipeerJsonContext.Default.HubHandshake));
        var reply = (await raw.Next()).Read(MultipeerJsonContext.Default.MultipeerHandshakeReply);
        Assert.True(reply.Accepted);
        Assert.Equal("host-1", reply.HostId);
        Assert.True(reply.Files);
        Assert.Equal(64 * 1024, reply.MediaMaxPacketSize);

        raw.Send(MultipeerProtocol.Encode(MultipeerMessageKind.Invoke, 2, "NoSuchMethod", []));
        var error = await raw.Next();
        Assert.Equal(MultipeerMessageKind.Error, error.Kind);
        Assert.Equal(2u, error.Id);
        Assert.Equal("HubMethodNotFound", error.Read(MultipeerJsonContext.Default.MultipeerError).Type);
    }


    [Fact]
    public async Task CallsBeforeTheHandshakeAreRefused()
    {
        var raw = await RawSession.Open(this.network);
        raw.Send(MultipeerProtocol.Encode(MultipeerMessageKind.Invoke, 7, "WhoAmI", []));

        var error = await raw.Next();
        Assert.Equal(MultipeerMessageKind.Error, error.Kind);
        Assert.Contains("Handshake required", error.Read(MultipeerJsonContext.Default.MultipeerError).Message);
    }


    [Fact]
    public void MessagesRoundTrip()
    {
        var bytes = MultipeerProtocol.Encode(MultipeerMessageKind.Push, 0xDEADBEEF, "Moved", new byte[] { 4, 5, 6 });
        var message = MultipeerProtocol.Decode(bytes);

        Assert.Equal(MultipeerMessageKind.Push, message.Kind);
        Assert.Equal(0xDEADBEEF, message.Id);
        Assert.Equal("Moved", message.Name);
        Assert.Equal(new byte[] { 4, 5, 6 }, message.Payload.ToArray());

        bytes[0] = 99;
        Assert.Throws<BleHubProtocolException>(() => MultipeerProtocol.Decode(bytes));
        Assert.Throws<BleHubProtocolException>(() => MultipeerProtocol.Decode(new byte[3]));
        Assert.Equal(42u, MultipeerProtocol.ReadFileResourceId(MultipeerProtocol.FileResourceName(42)));
        Assert.Null(MultipeerProtocol.ReadFileResourceId("something-else"));
    }


    [Theory]
    [InlineData("unigame-mc")]
    [InlineData("a")]
    [InlineData("a1")]
    [InlineData("fifteen-chars-x")]
    public void GoodServiceTypesAreAccepted(string serviceType) => MultipeerProtocol.ValidateServiceType(serviceType);


    [Theory]
    [InlineData("")]
    [InlineData("UPPER")]
    [InlineData("-lead")]
    [InlineData("trail-")]
    [InlineData("double--hyphen")]
    [InlineData("1234")]
    [InlineData("sixteen-chars-xx")]
    [InlineData("_unigame._tcp")]
    public void BadServiceTypesAreRejected(string serviceType)
    {
        Assert.Throws<ArgumentException>(() => MultipeerProtocol.ValidateServiceType(serviceType));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUniversalServer(s => s.Multipeer(serviceType).AddHub<TestHub>(CharacteristicUuid, MdnsServiceType)));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUniversalHubClient<ITestHub>(CharacteristicUuid, MdnsServiceType, o => o.Multipeer(serviceType)));
    }


    [Fact]
    public void BonjourServicesCoverTcpAndUdp()
        => Assert.Equal(["_unigame-mc._tcp", "_unigame-mc._udp"], MultipeerProtocol.BonjourServices("unigame-mc"));


    [Fact]
    public void MultipeerIsSetUpOncePerServer()
        => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(s => s
            .Multipeer("one")
            .Multipeer("two")
            .AddHub<TestHub>(CharacteristicUuid, MdnsServiceType)));


    [Theory]
    [InlineData(HubTransports.Http, new[] { HubTransports.Http, HubTransports.Multipeer, HubTransports.Ble })]
    [InlineData(HubTransports.Ble, new[] { HubTransports.Ble, HubTransports.Http, HubTransports.Multipeer })]
    [InlineData(HubTransports.Multipeer, new[] { HubTransports.Multipeer, HubTransports.Http, HubTransports.Ble })]
    [InlineData(HubTransports.All, new[] { HubTransports.Http, HubTransports.Multipeer, HubTransports.Ble })]
    public void TransportsAreTriedPreferredFirst(HubTransports prefer, HubTransports[] expected)
        => Assert.Equal(expected, new HubConnectOptions { Prefer = prefer }.Order);


    [Fact]
    public void DisplayNamesFitMultipeer()
    {
        Assert.Equal("Host", MultipeerNames.Truncate("Host"));
        Assert.Equal(63, System.Text.Encoding.UTF8.GetByteCount(MultipeerNames.Truncate(new string('x', 100))));

        // never splits a character
        var emoji = MultipeerNames.Truncate(String.Concat(Enumerable.Repeat("🎲", 20)));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(emoji) <= 63);
        Assert.Equal(15, emoji.EnumerateRunes().Count());
    }


    sealed class SyncProgress(List<TransferProgress> list) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            lock (list)
                list.Add(value);
        }
    }


    /// <summary>
    /// A session to the host with no hub client on top - what a client on another protocol version would look like
    /// </summary>
    sealed class RawSession : IMultipeerConnectionHandler
    {
        readonly Channel<MultipeerMessage> received = Channel.CreateUnbounded<MultipeerMessage>();
        IMultipeerConnection connection = null!;

        public static async Task<RawSession> Open(FakeMultipeerNetwork network)
        {
            var session = new RawSession();
            session.connection = await network.Connect(new FakePeer(network.Advertisements.Single().DisplayName), TimeSpan.FromSeconds(5), CancellationToken.None);
            session.connection.Start(session);
            return session;
        }

        public void Send(byte[] message) => this.connection.Send(message);

        public async Task<MultipeerMessage> Next() => await this.received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public void Received(ReadOnlyMemory<byte> message) => this.received.Writer.TryWrite(MultipeerProtocol.Decode(message));
        public void FileProgress(string name, double fraction) { }
        public void FileReceived(string name, string? path, Exception? error) { }
        public void Closed() => this.received.Writer.TryComplete();
    }
}
