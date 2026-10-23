using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// blehubs media channels over Wi-Fi: the client's WebSocket at {BasePath}/media reaching the hub's MediaChannelOpened,
/// on the in-memory transport
/// </summary>
public class MediaChannelTests : IAsyncLifetime
{
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string MdnsServiceType = "_mediatest._tcp";

    static MediaChannelTests() => Json.AddContext(TestJsonContext.Default);

    readonly List<ServiceProvider> clientContainers = new();
    readonly ConcurrentQueue<BleHubMediaChannelEventArgs> opened = new();
    TestHttpServer app = null!;
    bool mediaChannels = true;


    public async ValueTask InitializeAsync() => await this.StartHost();


    async Task StartHost()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.HideExceptionDetails = false;

        builder.Services.AddSingleton(NoBluetoothHosting.Create());
        builder.Services.AddSingleton<HubLog>();
        builder.Services.AddUniversalServer(server => server
            .Ble(o =>
            {
                o.LocalName = "MediaHost";
                if (this.mediaChannels)
                    o.EnableMediaChannels(maxPacketSize: 256 * 1024);
            })
            .Http(o =>
            {
                o.Advertise = false;
                o.ManageHttpServer = false;
            })
            .AddHub<TestHub>(CharacteristicUuid, MdnsServiceType)
        );

        this.app = TestHttpServer.For(builder.Build());
        this.app.Services.GetRequiredService<IHubContext<TestHub>>().MediaChannelOpened += (_, e) => this.opened.Enqueue(e);
        await this.app.Services.GetRequiredService<IUniversalHubHost>().Start(HubTransports.Http);
    }


    public async ValueTask DisposeAsync()
    {
        foreach (var c in this.clientContainers)
            await c.DisposeAsync();
        await this.app.DisposeAsync();
    }


    async Task<(IUniversalHubClient<ITestHub> Client, string ConnectionId)> Connect(string name = "Alice")
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddUniversalHubClient<ITestHub>(CharacteristicUuid, MdnsServiceType, o =>
            {
                o.HttpMessageHandlerFactory = () => this.app.Server.CreateInMemoryHandler();
                o.HttpConnectTimeout = TimeSpan.FromSeconds(5);
            })
            .BuildServiceProvider();
        this.clientContainers.Add(services);

        var client = services.GetRequiredService<IUniversalHubClient<ITestHub>>();
        await client.Connect(new HubHostInfo(
            "MediaHost",
            "MediaHost",
            null,
            null,
            new DiscoveredHttpServer("MediaHost", new Uri("http://localhost/hubs"), [], 80, new Dictionary<string, string>
            {
                ["hubs"] = CharacteristicUuid,
                ["proto"] = UniversalHubProtocol.Version.ToString()
            })
        ), new HubConnectOptions(name));

        var connection = await client.Hub.MyConnection();   // "{transport}:{connectionId}"
        return (client, connection[(connection.IndexOf(':') + 1)..]);
    }


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    static async Task<byte[]> Next(IAsyncEnumerator<ReadOnlyMemory<byte>> reader)
    {
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        return reader.Current.ToArray();
    }


    [Fact]
    public async Task AClientsChannelReachesTheHubAndCarriesPacketsBothWays()
    {
        var (client, connectionId) = await this.Connect();
        Assert.True(client.CanOpenMediaChannel);

        var channel = await client.OpenMediaChannel();
        Assert.Null(channel.ConnectionId);
        Assert.Equal(256 * 1024, channel.MaxPacketSize);
        await WaitFor(() => !this.opened.IsEmpty);

        var e = this.opened.Single();
        Assert.Equal(connectionId, e.Client.Id);
        Assert.Equal(connectionId, e.Channel.ConnectionId);
        Assert.Same(e.Channel, this.app.Services.GetRequiredService<IHubContext<TestHub>>().GetMediaChannel(connectionId));

        await using var hostReader = e.Channel.Receive().GetAsyncEnumerator();
        await using var clientReader = channel.Receive().GetAsyncEnumerator();

        for (byte i = 0; i < 20; i++)
            await channel.Send(new[] { i });
        for (byte i = 0; i < 20; i++)
            Assert.Equal(new[] { i }, await Next(hostReader));   // whole and in order

        await e.Channel.Send(new byte[] { 9, 8, 7 });
        Assert.Equal(new byte[] { 9, 8, 7 }, await Next(clientReader));
    }


    [Fact]
    public async Task PacketsLargerThanOneSocketReadArriveWhole()
    {
        var (client, _) = await this.Connect();
        var channel = await client.OpenMediaChannel();
        await WaitFor(() => !this.opened.IsEmpty);
        var host = this.opened.Single().Channel;

        var big = new byte[200_000];
        Random.Shared.NextBytes(big);
        await using var clientReader = channel.Receive().GetAsyncEnumerator();
        await using var hostReader = host.Receive().GetAsyncEnumerator();

        // read while sending - a socket only takes so much before its reader drains it
        var received = Next(clientReader);
        await host.Send(big).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(big, await received);

        received = Next(hostReader);
        await channel.Send(big).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(big, await received);

        await Assert.ThrowsAsync<ArgumentException>(() => channel.Send(new byte[256 * 1024 + 1]).AsTask());
        Assert.True(channel.IsOpen);
    }


    [Fact]
    public async Task OpeningAgainReturnsTheOpenChannel()
    {
        var (client, _) = await this.Connect();
        var first = await client.OpenMediaChannel();
        Assert.Same(first, await client.OpenMediaChannel());
    }


    [Fact]
    public async Task TheChannelClosesWhenTheClientLeaves()
    {
        var (client, connectionId) = await this.Connect();
        var channel = await client.OpenMediaChannel();
        await WaitFor(() => !this.opened.IsEmpty);
        var host = this.opened.Single().Channel;

        await client.Disconnect();
        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.IsOpen);
        Assert.Null(this.app.Services.GetRequiredService<IHubContext<TestHub>>().GetMediaChannel(connectionId));
    }


    [Fact]
    public async Task TheHostClosingTheChannelEndsTheClientsReceive()
    {
        var (client, _) = await this.Connect();
        var channel = await client.OpenMediaChannel();
        await WaitFor(() => !this.opened.IsEmpty);

        await this.opened.Single().Channel.DisposeAsync();
        await foreach (var _ in channel.Receive().WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
        {
        }
        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BleHubClientStatus.Connected, client.Status);   // the hub connection is untouched
    }


    [Fact]
    public async Task AChannelNeedsAToken()
    {
        using var invoker = new HttpMessageInvoker(this.app.Server.CreateInMemoryHandler());
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(UniversalHubProtocol.MediaTokenHeader, "not-a-token");
        var ex = await Assert.ThrowsAnyAsync<WebSocketException>(() => socket.ConnectAsync(new Uri("ws://localhost/hubs/media"), invoker, CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.Empty(this.opened);
    }


    [Fact]
    public async Task WithoutMediaChannelsThereIsNoToken()
    {
        this.mediaChannels = false;
        await this.app.DisposeAsync();
        await this.StartHost();

        var (client, _) = await this.Connect();
        Assert.False(client.CanOpenMediaChannel);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.OpenMediaChannel());
    }
}
