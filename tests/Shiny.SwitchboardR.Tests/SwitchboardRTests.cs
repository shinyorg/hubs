using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.Switchboard.Client;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.SwitchboardR.Tests;

/// <summary>
/// The real SwitchboardR host and client - generated proxy, generated hub dispatcher, Switchboard, file routes - talking
/// over an in-memory HTTP connection
/// </summary>
public class SwitchboardRTests : IAsyncLifetime
{
    const string ServiceUuid = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

    static SwitchboardRTests() => Json.AddContext(TestJsonContext.Default);

    readonly string filesDirectory = Path.Combine(Path.GetTempPath(), "switchboardr-tests", Guid.NewGuid().ToString("N"));
    readonly List<ServiceProvider> clientContainers = new();
    TestHttpServer app = null!;
    ISwitchboardRHost host = null!;
    HubLog log = null!;


    public async ValueTask InitializeAsync()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.HideExceptionDetails = false;

        builder.Services.AddSingleton(NoBluetoothHosting.Create());
        builder.Services.AddSingleton<HubLog>();
        builder.Services.AddBleHub<TestHub>(ServiceUuid, CharacteristicUuid, o => o.MaxClients = 3);
        builder.Services.ConfigureBleHubHost(o =>
        {
            o.LocalName = "TestHost";
            o.EnableFileTransfers(this.filesDirectory, ft => ft.MaxUploadSize = 1024);
        });
        builder
            .AddSwitchboardR(o =>
            {
                o.Advertise = false;
                o.ManageHttpServer = false;
                o.HostId = "host-1";
                o.AuthorizeFile = req => !req.FileName.StartsWith("secret", StringComparison.Ordinal);
            })
            .AddHub<TestHub>();

        this.app = TestHttpServer.For(builder.Build());
        this.host = this.app.Services.GetRequiredService<ISwitchboardRHost>();
        this.log = this.app.Services.GetRequiredService<HubLog>();

        Assert.Equal(HubTransports.Wifi, await this.host.Start(HubTransports.Wifi));
    }


    public async ValueTask DisposeAsync()
    {
        foreach (var c in this.clientContainers)
            await c.DisposeAsync();

        await this.app.DisposeAsync();
        if (Directory.Exists(this.filesDirectory))
            Directory.Delete(this.filesDirectory, true);
    }


    ISwitchboardRClient<ITestHub> CreateClient()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSwitchboardRClient<ITestHub>(ServiceUuid, CharacteristicUuid, o =>
            {
                o.HttpMessageHandlerFactory = () => this.app.Server.CreateInMemoryHandler();
                o.WifiConnectTimeout = TimeSpan.FromSeconds(5);
            })
            .BuildServiceProvider();

        this.clientContainers.Add(services);
        return services.GetRequiredService<ISwitchboardRClient<ITestHub>>();
    }


    static HubHostInfo WifiHost(string path = "/hubs") => new(
        "TestHost",
        "TestHost",
        "host-1",
        null,
        new DiscoveredHttpServer(
            "TestHost",
            new Uri("http://localhost" + path),
            [],
            80,
            new Dictionary<string, string> { ["hubs"] = CharacteristicUuid, ["proto"] = "1", ["path"] = path }
        )
    );


    async Task<ISwitchboardRClient<ITestHub>> Connect(string name = "Alice")
    {
        var client = this.CreateClient();
        await client.Connect(WifiHost(), new HubConnectOptions(name, "1.0"));
        return client;
    }


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public async Task ConnectsOverWifiAndCallsWork()
    {
        var client = await this.Connect();

        Assert.Equal(HubTransports.Wifi, client.Transport);
        Assert.Equal(BleHubClientStatus.Connected, client.Status);
        Assert.Equal("TestHost", client.HostName);
        Assert.Contains("connected:Alice", this.log.Entries);

        Assert.Equal(new Payload("hi", 42), await client.Hub.Echo(new Payload("hi", 42)));
        Assert.Equal("Alice", await client.Hub.WhoAmI());

        await client.Hub.Record("void works");
        Assert.Contains("record:void works", this.log.Entries);
    }


    [Fact]
    public async Task WifiClientsShowUpInTheHubContext()
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
    public async Task GroupsAndHubContextPushesReachWifiClients()
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
        string? reason = null;
        client.Disconnected += (_, r) => reason = r;

        await client.Hub.Kick("kicked");

        await WaitFor(() => reason != null);
        Assert.Equal("kicked", reason);
        await WaitFor(() => this.log.Entries.Contains("disconnected:Alice:kicked"));
    }


    [Fact]
    public async Task HubContextDisconnectReachesTheClient()
    {
        var client = await this.Connect();
        string? reason = null;
        client.Disconnected += (_, r) => reason = r;

        var context = this.app.Services.GetRequiredService<IHubContext<TestHub>>();
        await context.Disconnect(context.ConnectedClients.Single().Id, "Removed by host");

        await WaitFor(() => reason != null);
        Assert.Equal("Removed by host", reason);
        Assert.Equal(HubTransports.None, client.Transport);
    }


    [Fact]
    public async Task ClientDisconnectRunsOnDisconnected()
    {
        var client = await this.Connect();
        await client.Disconnect();

        await WaitFor(() => this.log.Entries.Any(x => x.StartsWith("disconnected:Alice")));
        Assert.Empty(this.app.Services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients);
    }


    [Fact]
    public async Task HostStopDisconnectsClientsAndRefusesNewOnes()
    {
        var client = await this.Connect();
        string? reason = null;
        client.Disconnected += (_, r) => reason = r;

        await this.host.Stop("Game over");
        Assert.Equal(HubTransports.None, this.host.RunningTransports);

        await WaitFor(() => reason != null);
        Assert.Equal("Game over", reason);

        var late = this.CreateClient();
        var ex = await Assert.ThrowsAsync<BleHubException>(() => late.Connect(WifiHost(), new HubConnectOptions("Late")));
        Assert.Contains("Hub is not running", ex.Message);
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
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.Connect(WifiHost("/nowhere"), new HubConnectOptions("Alice")));
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
            SwitchboardRProtocol.HandshakeMethod,
            [HubJson.Serialize(new HubHandshake(SwitchboardRProtocol.Version, "Raw", null, null))],
            CancellationToken.None
        ));
        Assert.True(reply.Accepted);
        Assert.Equal("host-1", reply.HostId);

        var envelope = await line.InvokeCoreAsync<JsonElement>(
            SwitchboardRProtocol.InvokeMethod,
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
            SwitchboardRProtocol.HandshakeMethod,
            [HubJson.Serialize(new HubHandshake(99, "Future", null, null))],
            CancellationToken.None
        ));
        Assert.False(reply.Accepted);
        Assert.Contains("protocol version 99", reply.Reason);
    }
}
