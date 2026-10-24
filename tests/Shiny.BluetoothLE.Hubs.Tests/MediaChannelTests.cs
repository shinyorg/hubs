using System.Buffers.Binary;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// Media channels over BLE: the real BleHubHost's media PSM against the fake GATT server and an in-memory L2CAP link
/// </summary>
public class MediaChannelTests : IAsyncLifetime
{
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const int MaxPacket = 64 * 1024;

    static MediaChannelTests() => Json.AddContext(TestJsonContext.Default);

    readonly FakeHostingManager hosting = new();
    readonly BleHubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(5) };
    readonly IBleHubSerializer serializer = new ShinyJsonBleHubSerializer();
    readonly Dictionary<string, List<FakeL2CapLink>> links = new();
    readonly List<BleHubMediaChannelEventArgs> opened = new();
    ServiceProvider services = null!;
    BleHubHost host = null!;
    IHubContext<TestHub> hub = null!;


    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    async Task Start(bool media = true, bool secure = false)
    {
        this.services = new ServiceCollection()
            .AddSingleton<HubLog>()
            .AddTransient<TestHub>()
            .BuildServiceProvider();

        var hostOptions = new BleHubHostOptions { LocalName = "Host", ClientSweepInterval = TimeSpan.FromMinutes(5) };
        if (media)
            hostOptions.EnableMediaChannels(MaxPacket, secure);

        this.host = new BleHubHost(
            this.hosting,
            [new BleHubRegistration(typeof(TestHub), TestChar, new BleHubOptions())],
            hostOptions,
            this.options,
            this.serializer,
            this.services
        );
        this.hub = new HubContext<TestHub>(() => this.host.GetRuntime(typeof(TestHub)), this.host);
        this.hub.MediaChannelOpened += (_, e) =>
        {
            lock (this.opened)
                this.opened.Add(e);
        };
        await this.host.Start();
    }


    async Task<BleHubClient> Connect(string centralId)
    {
        var client = BleHubClientFactories.Create<ITestHub>(new BleHubClientServices(this.options, this.serializer), BleHubProtocolOptions.DefaultServiceUuid, TestChar);
        var central = new FakeCentral(centralId);
        var ch = this.hosting.Characteristic(TestChar);
        var previous = ch.Notified;
        ch.Notified = (id, frame) =>
        {
            if (id == centralId)
                client.ReceiveFrame(frame);
            else
                previous?.Invoke(id, frame);
        };
        await ch.Subscribe(central);
        await client.ConnectTransport(
            (frame, _) => ch.Write(central, frame),
            central.Mtu + BleConstants.AttHeaderSize,
            new BleHubConnectOptions(centralId),
            CancellationToken.None,
            (psm, secure, _) =>
            {
                var link = this.hosting.ConnectL2Cap(psm, centralId, secure);
                lock (this.links)
                {
                    if (!this.links.TryGetValue(centralId, out var list))
                        this.links[centralId] = list = new();
                    list.Add(link);
                }
                return Task.FromResult(link.Central);
            }
        );
        return client;
    }


    BleHubMediaChannelEventArgs OpenedFor(string connectionId)
    {
        lock (this.opened)
            return this.opened.Last(x => x.Client.Id == connectionId);
    }


    static byte[] Numbered(int n, int length = 8)
    {
        var bytes = new byte[Math.Max(4, length)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, n);
        for (var i = 4; i < bytes.Length; i++)
            bytes[i] = (byte)(n + i);
        return bytes;
    }


    static async Task<List<byte[]>> Read(IBleHubMediaChannel channel, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var list = new List<byte[]>();
        await foreach (var packet in channel.Receive(cts.Token))
        {
            list.Add(packet.ToArray());
            if (list.Count == count)
                break;
        }
        return list;
    }


    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public async Task StartOpensAMediaListenerBesideTheHubs()
    {
        await this.Start(secure: true);
        Assert.Contains(this.hosting.Log, x => x.StartsWith("open-l2cap:") && x.EndsWith(":True"));
        Assert.Single(this.hosting.L2CapListeners);

        var client = await this.Connect("c1");
        Assert.True(client.CanOpenMediaChannel);
        var channel = await client.OpenMediaChannel();
        Assert.Null(channel.ConnectionId);
        Assert.Equal(MaxPacket, channel.MaxPacketSize);
        Assert.True(channel.IsOpen);
    }


    [Fact]
    public async Task PacketsFlowBothWaysInOrder()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var clientChannel = await client.OpenMediaChannel();

        await WaitFor(() => this.opened.Count == 1);
        var e = this.OpenedFor("c1");
        var hostChannel = e.Channel;
        Assert.Equal("c1", e.Client.Id);
        Assert.Equal("c1", hostChannel.ConnectionId);
        Assert.Same(hostChannel, this.hub.GetMediaChannel("c1"));

        var hostReads = Read(hostChannel, 200);
        var clientReads = Read(clientChannel, 200);

        // sends from several threads at once still go out whole - each packet is checked for its own contents
        await Task.WhenAll(
            Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                    await clientChannel.Send(Numbered(i, 3 + i));
            }),
            Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                    await hostChannel.Send(Numbered(1000 + i, 500));
            })
        );

        Assert.Equal(Enumerable.Range(0, 200).Select(i => Numbered(i, 3 + i)), await hostReads);
        Assert.Equal(Enumerable.Range(1000, 200).Select(i => Numbered(i, 500)), await clientReads);
    }


    [Fact]
    public async Task LargePacketsSpanManyL2CapWrites()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var clientChannel = await client.OpenMediaChannel();
        await WaitFor(() => this.opened.Count == 1);
        var hostChannel = this.OpenedFor("c1").Channel;
        var link = this.links["c1"].Single();
        var writesBefore = link.CentralWrites;

        var big = new byte[MaxPacket];
        new Random(7).NextBytes(big);
        var reads = Read(hostChannel, 2);
        await clientChannel.Send(big);
        await clientChannel.Send(new byte[] { 1, 2, 3 });

        var received = await reads;
        Assert.Equal(big, received[0]);
        Assert.Equal(new byte[] { 1, 2, 3 }, received[1]);
        Assert.True(link.CentralWrites - writesBefore > 10, $"only {link.CentralWrites - writesBefore} writes");

        var back = Read(clientChannel, 1);
        await hostChannel.Send(big);
        Assert.Equal(big, (await back)[0]);
    }


    [Fact]
    public async Task OversizePacketThrowsAndTheChannelStaysOpen()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var channel = await client.OpenMediaChannel();

        await Assert.ThrowsAsync<ArgumentException>(() => channel.Send(new byte[MaxPacket + 1]).AsTask());
        Assert.True(channel.IsOpen);
    }


    [Fact]
    public async Task OpenReturnsTheChannelAlreadyOpen()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var first = await client.OpenMediaChannel();
        var again = await client.OpenMediaChannel();

        Assert.Same(first, again);
        Assert.Single(this.links["c1"]);
    }


    [Fact]
    public async Task OneReaderAtATime()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var channel = await client.OpenMediaChannel();

        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in channel.Receive(cts.Token)) { }
        });
        await Task.Delay(50);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in channel.Receive()) { }
        });
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);
    }


    [Fact]
    public async Task UnknownTokenIsRefused()
    {
        await this.Start();
        await this.Connect("c1");

        var psm = this.hosting.L2CapListeners.Single();
        var link = this.hosting.ConnectL2Cap(psm, "intruder");
        var answers = new List<byte[]>();
        var completed = false;
        using var _ = link.Central.DataReceived.Subscribe(x => answers.Add(x), () => completed = true);

        foreach (var write in MediaFrameCodec.Encode("not-a-token"u8))
            await link.Central.Write(write).ToTask();

        await WaitFor(() => completed);
        Assert.Empty(answers);
        Assert.Empty(this.opened);
        Assert.Null(this.hub.GetMediaChannel("c1"));
    }


    [Fact]
    public async Task ATokenIsOnlyGoodForItsOwnConnection()
    {
        await this.Start();
        var a = await this.Connect("c1");
        await this.Connect("c2");
        await a.OpenMediaChannel();

        await WaitFor(() => this.opened.Count == 1);
        Assert.Equal("c1", this.opened.Single().Client.Id);
        Assert.NotNull(this.hub.GetMediaChannel("c1"));
        Assert.Null(this.hub.GetMediaChannel("c2"));
    }


    [Fact]
    public async Task ClientDisconnectClosesBothEnds()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var clientChannel = await client.OpenMediaChannel();
        await WaitFor(() => this.opened.Count == 1);
        var hostChannel = this.OpenedFor("c1").Channel;

        await client.Disconnect();

        await clientChannel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await hostChannel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(clientChannel.IsOpen);
        Assert.False(hostChannel.IsOpen);
        Assert.Null(this.hub.GetMediaChannel("c1"));
        await Assert.ThrowsAsync<BleHubException>(() => hostChannel.Send(new byte[1]).AsTask());
    }


    [Fact]
    public async Task ReceiveCompletesWhenTheChannelCloses()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var clientChannel = await client.OpenMediaChannel();
        await WaitFor(() => this.opened.Count == 1);

        var reading = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in clientChannel.Receive())
                count++;
            return count;
        });
        await this.OpenedFor("c1").Channel.Send(new byte[] { 1 });
        await this.OpenedFor("c1").Channel.DisposeAsync();

        Assert.Equal(1, await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(clientChannel.IsOpen);
        Assert.Equal(BleHubClientStatus.Connected, client.Status);
    }


    [Fact]
    public async Task HubStopClosesTheChannel()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var clientChannel = await client.OpenMediaChannel();
        await WaitFor(() => this.opened.Count == 1);
        var hostChannel = this.OpenedFor("c1").Channel;

        await this.hub.Stop("bye");

        await hostChannel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await clientChannel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(this.hosting.L2CapListeners);
    }


    [Fact]
    public async Task AClosedChannelCanBeReopenedAndReplacesTheOld()
    {
        await this.Start();
        var client = await this.Connect("c1");
        var first = await client.OpenMediaChannel();
        await WaitFor(() => this.opened.Count == 1);

        await first.DisposeAsync();
        await WaitFor(() => this.hub.GetMediaChannel("c1") == null);

        var second = await client.OpenMediaChannel();
        Assert.NotSame(first, second);
        await WaitFor(() => this.opened.Count == 2);
        Assert.Same(this.OpenedFor("c1").Channel, this.hub.GetMediaChannel("c1"));

        var reads = Read(this.OpenedFor("c1").Channel, 1);
        await second.Send(new byte[] { 42 });
        Assert.Equal(new byte[] { 42 }, (await reads)[0]);
    }


    [Fact]
    public async Task WithoutMediaChannelsTheClientCantOpenOne()
    {
        await this.Start(media: false);
        Assert.Empty(this.hosting.L2CapListeners);

        var client = await this.Connect("c1");
        Assert.False(client.CanOpenMediaChannel);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.OpenMediaChannel());
    }


    [Fact]
    public async Task HubsStillStartWhenL2CapIsUnavailable()
    {
        this.hosting.FailL2Cap = true;
        await this.Start();
        Assert.True(this.host.IsRunning);

        var client = await this.Connect("c1");
        Assert.False(client.CanOpenMediaChannel);
    }


    [Fact]
    public async Task NotConnectedCantOpen()
    {
        await this.Start();
        var client = BleHubClientFactories.Create<ITestHub>(new BleHubClientServices(this.options, this.serializer), BleHubProtocolOptions.DefaultServiceUuid, TestChar);
        Assert.False(client.CanOpenMediaChannel);
        await Assert.ThrowsAsync<BleHubDisconnectedException>(() => client.OpenMediaChannel());
    }
}
