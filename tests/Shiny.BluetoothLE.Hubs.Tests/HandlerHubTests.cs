using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hosting;
using Shiny.UniversalHubs;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// Handler hubs (<see cref="IBleHubHandler"/>) added to the real host while it runs, served by the BLE transport over the
/// in-memory GATT server
/// </summary>
public class HandlerHubTests : IAsyncLifetime
{
    const string ServiceUuid = BleHubProtocolOptions.DefaultServiceUuid;
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string HandlerChar = "6e400020-b5a3-f393-e0a9-e50e24dcca9e";

    static HandlerHubTests() => Json.AddContext(TestJsonContext.Default);

    readonly FakeHostingManager hosting = new();
    readonly BleHubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(5) };
    readonly IBleHubSerializer serializer = new ShinyJsonBleHubSerializer();
    readonly EchoHandler handler = new();
    ServiceProvider? services;
    UniversalHubHost host = null!;


    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (this.services != null)
            await this.services.DisposeAsync();
    }


    void Build(bool withTestHub = false)
    {
        this.services = new ServiceCollection()
            .AddSingleton<HubLog>()
            .AddSingleton<IBleHostingManager>(this.hosting)
            .AddUniversalServer(server =>
            {
                server
                    .Configure(o => o.LocalName = "Host")
                    .Protocol(o => o.RequestTimeout = this.options.RequestTimeout)
                    .BluetoothLE(o => o.ClientSweepInterval = TimeSpan.FromMinutes(5))
                    .AllowDynamicHubs();

                if (withTestHub)
                    server.AddHub<TestHub>(TestChar, hub => hub.UseBluetoothLE(ServiceUuid, TestChar));
            })
            .BuildServiceProvider();
        this.host = this.services.GetRequiredService<UniversalHubHost>();
    }


    Handle Add(string characteristicUuid, string name) => new(this.host, this.host.AddDynamic(
        characteristicUuid,
        name,
        new BleHubOptions(),
        this.handler,
        HubTransports.Ble,
        new Dictionary<Type, object> { [typeof(BleHubSettings)] = new BleHubSettings(ServiceUuid, characteristicUuid) }
    ));


    IReadOnlyList<HubEntry> Handlers => this.host.Hubs.Where(x => x.HubType == null).ToList();


    sealed record Handle(UniversalHubHost Host, HubEntry Entry)
    {
        public bool IsRunning => this.Entry.Runtime.IsRunning;
        public Task Start() => this.Host.StartHubs([this.Entry], HubTransports.All, CancellationToken.None);

        public IHubClients<BleHubPush<IBleHubHandler>> Clients
            => new HubClients<BleHubPush<IBleHubHandler>>(this.Entry.Runtime, target => new BleHubPush<IBleHubHandler>(target, this.Entry.Runtime.Serializer));
    }


    async Task<TContract> Connect<TContract>(string characteristicUuid, string centralId) where TContract : class
    {
        var client = BleHubClientFactories.Create<TContract>(new BleHubClientServices(this.options, this.serializer), characteristicUuid);
        var central = new FakeCentral(centralId);
        var ch = this.hosting.Characteristic(characteristicUuid);

        var previous = ch.Notified;
        ch.Notified = (id, frame) =>
        {
            if (id == centralId)
                client.ReceiveFrame(frame);
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
    public async Task AHandlerHubAnswersOverBleWithTheCallersValues()
    {
        this.Build();
        var handle = this.Add(HandlerChar, "game");
        await handle.Start();

        var client = await this.Connect<ITestHub>(HandlerChar, "c1");
        Assert.Contains("connected:c1", this.handler.Log);
        Assert.Equal(new Payload("x", 1), await client.Echo(new Payload("x", 1)));
        Assert.Equal("x", JsonSerializer.Deserialize(this.handler.LastArguments![0], TestJsonContext.Default.Payload)!.Text);

        var items = new List<int>();
        await foreach (var i in client.Count(3, CancellationToken.None))
            items.Add(i);
        Assert.Equal([1, 2, 3], items);

        var ex = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.Fail());
        Assert.Equal(nameof(InvalidOperationException), ex.RemoteErrorType);

        // a relayed error keeps its own type
        this.handler.Relay = true;
        var relayed = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.Fail());
        Assert.Equal("GameRule", relayed.RemoteErrorType);

        var missing = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.WhoAmI());
        Assert.Contains("game has no method 'WhoAmI'", missing.Message);
    }


    [Fact]
    public async Task HandlerPushesUseAlreadySerializedValues()
    {
        this.Build();
        var handle = this.Add(HandlerChar, "game");
        await handle.Start();
        var client = await this.Connect<ITestHub>(HandlerChar, "c1");
        var messages = new List<string>();
        client.Message += m => messages.Add(m);

        var push = handle.Clients.All;
        await push.Send("Message", push.CreateArguments().WriteSerialized("\"hello\""u8));

        await WaitFor(() => messages.Count == 1);
        Assert.Equal("hello", messages[0]);
    }


    [Fact]
    public async Task AServerMayStartWithOnlyHandlerHubs()
    {
        this.Build();
        Assert.Empty(this.Handlers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.host.Start());

        this.Add(HandlerChar, "game");
        await this.host.Start();
        Assert.Contains($"add-service:{ServiceUuid}:1", this.hosting.Log);
    }


    [Fact]
    public async Task AddingAHubWhileTheServiceIsUpRebuildsItWhenTheHubStarts()
    {
        this.Build(withTestHub: true);
        await this.host.Start();
        var compiled = await this.Connect<ITestHub>(TestChar, "c1");
        HubDisconnect? asked = null;
        ((IBleHubConnection)compiled).Disconnected += (_, d) => asked = d;

        // adding alone changes nothing on air
        var handle = this.Add(HandlerChar, "game");
        Assert.Single(this.hosting.Log, x => x.StartsWith("add-service", StringComparison.Ordinal));

        // starting it rebuilds the one service with both characteristics, and the compiled hub's BLE client is asked to reconnect
        await handle.Start();
        await WaitFor(() => asked != null);
        Assert.Equal(HubDisconnectReason.ServerShutdown, asked!.Reason);
        Assert.Contains($"remove-service:{ServiceUuid}", this.hosting.Log);
        Assert.Contains($"add-service:{ServiceUuid}:2", this.hosting.Log);

        var dynamic = await this.Connect<ITestHub>(HandlerChar, "c2");
        Assert.Equal(new Payload("y", 2), await dynamic.Echo(new Payload("y", 2)));
    }


    [Fact]
    public async Task RemovingAHandlerHubDisconnectsItsClientsAndLeavesTheServiceAlone()
    {
        this.Build(withTestHub: true);
        var handle = this.Add(HandlerChar, "game");
        await this.host.Start();
        var client = await this.Connect<ITestHub>(HandlerChar, "c1");
        HubDisconnect? left = null;
        ((IBleHubConnection)client).Disconnected += (_, d) => left = d;

        await this.host.RemoveDynamic(this.Handlers.Single(), "Game over");

        await WaitFor(() => left != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "Game over"), left);
        await WaitFor(() => this.handler.Log.Contains("disconnected:c1:ServerShutdown"));
        Assert.Empty(this.Handlers);
        Assert.False(handle.IsRunning);
        Assert.DoesNotContain($"remove-service:{ServiceUuid}", this.hosting.Log);   // the compiled hub is still running

        // the characteristic can be used again
        this.Add(HandlerChar, "game");
    }


    [Fact]
    public void ACharacteristicBelongsToOneHub()
    {
        this.Build(withTestHub: true);
        Assert.Throws<InvalidOperationException>(() => this.Add(TestChar, "game"));

        this.Add(HandlerChar, "game");
        Assert.Throws<InvalidOperationException>(() => this.Add(HandlerChar, "other"));
    }


    [Fact]
    public void RegistrationNeedsAHubUnlessDynamicHubsAreAllowed()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(server => server.BluetoothLE(_ => { })));

        using var services = new ServiceCollection()
            .AddSingleton<IBleHostingManager>(this.hosting)
            .AddUniversalServer(server => server.BluetoothLE(_ => { }).AllowDynamicHubs())
            .BuildServiceProvider();
        Assert.NotNull(services.GetRequiredService<IUniversalHubHost>());
    }


    [Fact]
    public void DisposingAHostThatNeverStartedLeavesTheRadioAlone()
    {
        this.Build();
        this.Add(HandlerChar, "game");
        this.services!.GetRequiredService<BleHubTransport>().Dispose();

        Assert.Empty(this.hosting.Log);
    }


    [Fact]
    public void RawArgumentsRoundTrip()
    {
        var bytes = new BleHubArgumentWriter(this.serializer).WriteSerialized("1"u8).Write("two").ToArray();
        var reader = new BleHubArgumentReader(this.serializer, bytes);

        Assert.Equal("1", Encoding.UTF8.GetString(reader.ReadSerialized().Span));
        Assert.Equal("two", reader.Read<string>());
    }


    sealed class EchoHandler : IBleHubHandler
    {
        public List<string> Log { get; } = new();
        public byte[][]? LastArguments { get; private set; }
        public bool Relay { get; set; }

        public BleHubMethodKind GetMethodKind(string method) => method switch
        {
            "Echo" or "Fail" => BleHubMethodKind.Invoke,
            "Count" => BleHubMethodKind.Stream,
            _ => BleHubMethodKind.NotFound
        };

        public Task<byte[]?> Invoke(BleHubHandlerCall call, CancellationToken cancellationToken)
        {
            this.LastArguments = call.Arguments.Select(x => x.ToArray()).ToArray();
            if (call.Method == "Echo")
                return Task.FromResult<byte[]?>(call.Arguments[0].ToArray());

            throw this.Relay ? new BleHubRemoteException("GameRule", "Not your turn") : new InvalidOperationException("boom");
        }

        public async IAsyncEnumerable<byte[]> Stream(BleHubHandlerCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var to = JsonSerializer.Deserialize(call.Arguments[0].Span, TestJsonContext.Default.Int32);
            for (var i = 1; i <= to; i++)
            {
                await Task.Yield();
                yield return Encoding.UTF8.GetBytes(i.ToString());
            }
        }

        public Task OnConnectedAsync(BleHubCallerContext context)
        {
            lock (this.Log)
                this.Log.Add($"connected:{context.Client.Name}");
            return Task.CompletedTask;
        }

        public Task OnDisconnectedAsync(BleHubCallerContext context, HubDisconnect disconnect)
        {
            lock (this.Log)
                this.Log.Add($"disconnected:{context.Client.Name}:{disconnect.Reason}");
            return Task.CompletedTask;
        }

        public Task OnRenamedAsync(BleHubCallerContext context, string? previousName) => Task.CompletedTask;
    }
}
