using Microsoft.Extensions.DependencyInjection;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// Drives the real BleHubHost against an in-memory GATT server - start/stop per hub, shared services, advertising
/// </summary>
public class BleHubHostTests : IAsyncLifetime
{
    const string ServiceA = BleHubProtocolOptions.DefaultServiceUuid;
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string SecondChar = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

    static BleHubHostTests() => Json.AddContext(TestJsonContext.Default);

    readonly FakeHostingManager hosting = new();
    readonly BleHubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(5) };
    readonly IBleHubSerializer serializer = new ShinyJsonBleHubSerializer();
    string serviceUuid = ServiceA;
    ServiceProvider services = null!;
    BleHubHost host = null!;
    IHubContext<TestHub> testHub = null!;
    IHubContext<SecondHub> secondHub = null!;


    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    void Build(TimeSpan? sweepInterval = null)
    {
        this.services = new ServiceCollection()
            .AddSingleton<HubLog>()
            .AddTransient<TestHub>()
            .AddTransient<SecondHub>()
            .BuildServiceProvider();

        this.host = new BleHubHost(
            this.hosting,
            [
                new BleHubRegistration(typeof(TestHub), TestChar, new BleHubOptions()),
                new BleHubRegistration(typeof(SecondHub), SecondChar, new BleHubOptions())
            ],
            new BleHubHostOptions { ServiceUuid = this.serviceUuid, LocalName = "Host", ClientSweepInterval = sweepInterval ?? TimeSpan.FromMinutes(5) },
            this.options,
            this.serializer,
            this.services
        );
        this.testHub = new HubContext<TestHub>(() => this.host.GetRuntime(typeof(TestHub)), this.host);
        this.secondHub = new HubContext<SecondHub>(() => this.host.GetRuntime(typeof(SecondHub)), this.host);
    }


    /// <summary>
    /// A client proxy wired to the fake GATT server like a real central would be
    /// </summary>
    /// <param name="mtu">The central's payload size, as Shiny reports it (ATT MTU minus 3)</param>
    async Task<TContract> Connect<TContract>(string characteristicUuid, string centralId, int mtu = 185, List<byte[]>? notified = null) where TContract : class
    {
        var client = BleHubClientFactories.Create<TContract>(new BleHubClientServices(this.options, this.serializer), this.serviceUuid, characteristicUuid);
        var central = new FakeCentral(centralId, mtu);
        var ch = this.hosting.Characteristic(characteristicUuid);

        var previous = ch.Notified;
        ch.Notified = (id, frame) =>
        {
            if (id == centralId)
            {
                notified?.Add(frame);
                client.ReceiveFrame(frame);
            }
            else
                previous?.Invoke(id, frame);
        };
        await ch.Subscribe(central);
        await client.ConnectTransport((frame, _) => ch.Write(central, frame), central.Mtu + BleConstants.AttHeaderSize, new BleHubConnectOptions(centralId), CancellationToken.None);
        return (TContract)(object)client;
    }


    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public async Task StartRunsEveryHubInOneSharedService()
    {
        this.Build();
        await this.host.Start();

        Assert.True(this.host.IsRunning);
        Assert.True(this.testHub.IsRunning);
        Assert.True(this.secondHub.IsRunning);
        Assert.Contains($"add-service:{ServiceA}:2", this.hosting.Log);
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);
        Assert.Equal("Host", this.hosting.AdvertisedName);

        var test = await this.Connect<ITestHub>(TestChar, "c1");
        var second = await this.Connect<ISecondHub>(SecondChar, "c2");
        Assert.Equal(new Payload("x", 1), await test.Echo(new Payload("x", 1)));
        Assert.Equal("pong", await second.Ping());
    }


    [Fact]
    public async Task FramesFillTheCentralsWholePayload()
    {
        this.Build();
        await this.host.Start();

        // Shiny reports a central's payload size (ATT MTU 185 - 3) - every frame but the last is exactly that big
        var frames = new List<byte[]>();
        var test = await this.Connect<ITestHub>(TestChar, "c1", mtu: 182, notified: frames);
        frames.Clear();

        var big = new Payload(new string('x', 2000), 1);
        Assert.Equal(big, await test.Echo(big));
        Assert.True(frames.Count > 1);
        Assert.All(frames[..^1], x => Assert.Equal(182, x.Length));
        Assert.True(frames[^1].Length <= 182);
        Assert.Equal(185, this.host.GetRuntime(typeof(TestHub)).FindClient("c1")!.Mtu);
    }


    [Fact]
    public async Task StartingOneHubLeavesTheOtherRefusingClients()
    {
        this.Build();
        await this.testHub.Start();

        Assert.True(this.testHub.IsRunning);
        Assert.False(this.secondHub.IsRunning);
        Assert.True(this.host.IsRunning);

        // the shared service holds both characteristics, but only the running hub accepts clients
        await this.Connect<ITestHub>(TestChar, "c1");
        var ex = await Assert.ThrowsAsync<BleHubException>(() => this.Connect<ISecondHub>(SecondChar, "c2"));
        Assert.Contains("Hub is not running", ex.Message);
    }


    [Fact]
    public async Task StoppingOneHubKeepsASharedServiceForTheOther()
    {
        this.Build();
        await this.host.Start();

        var test = await this.Connect<ITestHub>(TestChar, "c1");
        var second = await this.Connect<ISecondHub>(SecondChar, "c2");
        HubDisconnect? kicked = null;
        ((IBleHubConnection)test).Disconnected += (_, d) => kicked = d;

        await this.testHub.Stop("maintenance");

        await WaitFor(() => kicked != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "maintenance"), kicked);
        Assert.False(this.testHub.IsRunning);
        Assert.True(this.hosting.HasService(ServiceA));
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);
        Assert.Equal("pong", await second.Ping());
        Assert.DoesNotContain($"remove-service:{ServiceA}", this.hosting.Log);
    }


    [Fact]
    public async Task StoppingTheLastHubTearsEverythingDown()
    {
        this.Build();
        await this.host.Start();

        await this.testHub.Stop();
        await this.secondHub.Stop();

        Assert.False(this.host.IsRunning);
        Assert.False(this.hosting.HasService(ServiceA));
        Assert.False(this.hosting.IsAdvertising);
    }


    [Fact]
    public async Task StartingAndStoppingOneHubBesideAnotherChangesNothingOnAir()
    {
        this.Build();
        await this.testHub.Start();
        await this.secondHub.Start();
        await this.secondHub.Stop();
        await this.secondHub.Start();

        // one service, one advertised UUID, advertised once - however hubs come and go beside each other
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);
        Assert.Equal(1, this.hosting.Log.Count(x => x.StartsWith("advertise:")));
        Assert.Equal(1, this.hosting.Log.Count(x => x.StartsWith("add-service:")));
        Assert.DoesNotContain($"remove-service:{ServiceA}", this.hosting.Log);
    }


    [Fact]
    public async Task TheServiceUuidComesFromTheHostOptions()
    {
        const string custom = "11111111-2222-3333-4444-555555555555";
        this.serviceUuid = custom.ToUpperInvariant();
        this.Build();
        await this.host.Start();

        Assert.True(this.hosting.HasService(custom));
        Assert.Equal([custom], this.hosting.AdvertisedServices);
        await this.Connect<ITestHub>(TestChar, "c1");
    }


    [Fact]
    public void AnInvalidServiceUuidIsRefused()
    {
        this.serviceUuid = "180D";
        Assert.Throws<ArgumentException>(() => this.Build());
    }


    [Fact]
    public async Task HubCanBeRestarted()
    {
        this.Build();
        await this.testHub.Start();
        await this.testHub.Stop();
        await this.testHub.Start();

        var test = await this.Connect<ITestHub>(TestChar, "c1");
        Assert.Equal("a1b2", await test.Concat("a", 1, new Payload("b", 2)));
    }


    [Fact]
    public async Task ClientDisconnectSaysGoodbyeBeforeUnsubscribing()
    {
        this.Build();
        await this.host.Start();
        var test = await this.Connect<ITestHub>(TestChar, "c1");
        BleHubClientDisconnectedEventArgs? left = null;
        this.testHub.ClientDisconnected += (_, e) => left = e;

        await ((IBleHubConnection)test).Disconnect();
        // the central's unsubscribe arrives after the goodbye - it must not turn the leave into a timeout
        await this.hosting.Characteristic(TestChar).Unsubscribe(new FakeCentral("c1"));

        await WaitFor(() => left != null);
        Assert.Equal(HubDisconnectReason.ClientDisconnect, left!.Disconnect.Reason);
    }


    [Fact]
    public async Task ConnectedClientsCarryTheirCentral()
    {
        this.Build();
        await this.host.Start();
        await this.Connect<ITestHub>(TestChar, "c1");

        var client = Assert.Single(this.testHub.ConnectedClients);
        Assert.Equal("c1", client.Peripheral?.Uuid);

        // a client on another transport has no central
        await this.testHub.TransportEndpoint.Connect("wifi-1", new Protocol.HandshakeInfo(1, "WiFi", null, null), new RecordingChannel(), CancellationToken.None);
        Assert.Null(this.testHub.ConnectedClients.Single(x => x.Id == "wifi-1").Peripheral);
    }


    [Fact]
    public async Task UnsubscribeWithoutGoodbyeIsATimeout()
    {
        this.Build();
        await this.host.Start();
        await this.Connect<ITestHub>(TestChar, "c1");
        BleHubClientDisconnectedEventArgs? left = null;
        this.testHub.ClientDisconnected += (_, e) => left = e;

        await this.hosting.Characteristic(TestChar).Unsubscribe(new FakeCentral("c1"));

        await WaitFor(() => left != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ClientTimeout, "Unsubscribed"), left!.Disconnect);
    }


    [Fact]
    public async Task SweptClientIsATimeout()
    {
        this.Build(TimeSpan.FromMilliseconds(50));
        await this.host.Start();
        await this.Connect<ITestHub>(TestChar, "c1");
        BleHubClientDisconnectedEventArgs? left = null;
        this.testHub.ClientDisconnected += (_, e) => left = e;

        // gone from the GATT server without an unsubscribe being reported
        this.hosting.Characteristic(TestChar).Forget("c1");

        await WaitFor(() => left != null);
        Assert.Equal(HubDisconnectReason.ClientTimeout, left!.Disconnect.Reason);
    }


    [Fact]
    public async Task HostStopStopsEveryHub()
    {
        this.Build();
        await this.host.Start();
        var test = await this.Connect<ITestHub>(TestChar, "c1");
        HubDisconnect? disconnected = null;
        ((IBleHubConnection)test).Disconnected += (_, d) => disconnected = d;

        await this.host.Stop("bye");

        await WaitFor(() => disconnected != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "bye"), disconnected);
        Assert.False(this.testHub.IsRunning);
        Assert.False(this.secondHub.IsRunning);
        Assert.False(this.hosting.IsAdvertising);
    }


    [Fact]
    public async Task SweepLeavesClientsOnOtherTransportsAlone()
    {
        this.Build(TimeSpan.FromMilliseconds(50));
        await this.testHub.Start();

        var channel = new RecordingChannel();
        var rejection = await this.testHub.TransportEndpoint.Connect("wifi-1", new Protocol.HandshakeInfo(1, "WiFi", null, null), channel, CancellationToken.None);
        Assert.Null(rejection);

        // several sweeps - the client is not a GATT subscriber, but it must not be reaped
        await Task.Delay(300);
        Assert.Single(this.testHub.ConnectedClients);

        await this.testHub.Stop("bye");
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "bye"), channel.Disconnect);
        Assert.Empty(this.testHub.ConnectedClients);
    }


    [Fact]
    public async Task RenameReadvertisesAndTellsClientsWithoutStopping()
    {
        this.Build();
        await this.host.Start();
        var test = (BleHubClient)(object)await this.Connect<ITestHub>(TestChar, "c1");
        var channel = new RecordingChannel();
        await this.testHub.TransportEndpoint.Connect("wifi-1", new Protocol.HandshakeInfo(1, "WiFi", null, null), channel, CancellationToken.None);

        await this.host.Rename("Renamed");

        Assert.Equal("Renamed", this.hosting.AdvertisedName);
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);
        Assert.Equal(2, this.hosting.Log.Count(x => x == $"advertise:{ServiceA}"));
        await WaitFor(() => test.HostName == "Renamed");
        Assert.Equal("Renamed", channel.HostName);
        Assert.Equal(2, this.testHub.ConnectedClients.Count);
        Assert.Equal(BleHubClientStatus.Connected, test.Status);
    }


    [Fact]
    public async Task RenameWhileStoppedIsUsedOnTheNextStart()
    {
        this.Build();
        await this.host.Rename("Later");
        Assert.False(this.hosting.IsAdvertising);

        await this.host.Start();
        Assert.Equal("Later", this.hosting.AdvertisedName);
        var test = (BleHubClient)(object)await this.Connect<ITestHub>(TestChar, "c1");
        Assert.Equal("Later", test.HostName);
    }


    sealed class RecordingChannel : IBleHubPeerChannel
    {
        public HubDisconnect? Disconnect { get; private set; }
        public string? HostName { get; private set; }
        public Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken) => Task.CompletedTask;

        Task IBleHubPeerChannel.HostRenamed(string? hostName, CancellationToken cancellationToken)
        {
            this.HostName = hostName;
            return Task.CompletedTask;
        }

        Task IBleHubPeerChannel.Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken)
        {
            this.Disconnect = disconnect;
            return Task.CompletedTask;
        }
    }
}
