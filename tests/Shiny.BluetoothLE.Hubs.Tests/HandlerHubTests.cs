using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// Handler hubs (<see cref="IBleHubHandler"/>) added to the real BleHubHost while it runs, over the in-memory GATT server
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
    ServiceProvider services = null!;
    BleHubHost host = null!;


    public ValueTask InitializeAsync()
    {
        this.services = new ServiceCollection()
            .AddSingleton<HubLog>()
            .AddTransient<TestHub>()
            .BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    void Build(params BleHubRegistration[] registrations) => this.host = new BleHubHost(
        this.hosting,
        registrations,
        new BleHubHostOptions { ServiceUuid = ServiceUuid, LocalName = "Host", ClientSweepInterval = TimeSpan.FromMinutes(5) },
        this.options,
        this.serializer,
        this.services
    );


    async Task<TContract> Connect<TContract>(string characteristicUuid, string centralId) where TContract : class
    {
        var client = BleHubClientFactories.Create<TContract>(new BleHubClientServices(this.options, this.serializer), ServiceUuid, characteristicUuid);
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
        var handle = this.host.Add(HandlerChar, "game", new BleHubOptions(), this.handler);
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
        var handle = this.host.Add(HandlerChar, "game", new BleHubOptions(), this.handler);
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
        Assert.Empty(this.host.Handlers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.host.Start());

        this.host.Add(HandlerChar, "game", new BleHubOptions(), this.handler);
        await this.host.Start();
        Assert.Contains($"add-service:{ServiceUuid}:1", this.hosting.Log);
    }


    [Fact]
    public async Task AddingAHubWhileTheServiceIsUpRebuildsItWhenTheHubStarts()
    {
        this.Build(new BleHubRegistration(typeof(TestHub), TestChar, new BleHubOptions()));
        await this.host.Start();
        var compiled = await this.Connect<ITestHub>(TestChar, "c1");
        HubDisconnect? asked = null;
        ((IBleHubConnection)compiled).Disconnected += (_, d) => asked = d;

        // adding alone changes nothing on air
        var handle = this.host.Add(HandlerChar, "game", new BleHubOptions(), this.handler);
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
        this.Build(new BleHubRegistration(typeof(TestHub), TestChar, new BleHubOptions()));
        var handle = this.host.Add(HandlerChar, "game", new BleHubOptions(), this.handler);
        await this.host.Start();
        var client = await this.Connect<ITestHub>(HandlerChar, "c1");
        HubDisconnect? left = null;
        ((IBleHubConnection)client).Disconnected += (_, d) => left = d;

        await this.host.Remove(HandlerChar, "Game over");

        await WaitFor(() => left != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerShutdown, "Game over"), left);
        await WaitFor(() => this.handler.Log.Contains("disconnected:c1:ServerShutdown"));
        Assert.Empty(this.host.Handlers);
        Assert.False(handle.IsRunning);
        Assert.DoesNotContain($"remove-service:{ServiceUuid}", this.hosting.Log);   // the compiled hub is still running

        // the characteristic can be used again
        this.host.Add(HandlerChar, "game", new BleHubOptions(), this.handler);
    }


    [Fact]
    public void ACharacteristicBelongsToOneHub()
    {
        this.Build(new BleHubRegistration(typeof(TestHub), TestChar, new BleHubOptions()));
        Assert.Throws<InvalidOperationException>(() => this.host.Add(TestChar, "game", new BleHubOptions(), this.handler));

        this.host.Add(HandlerChar, "game", new BleHubOptions(), this.handler);
        Assert.Throws<InvalidOperationException>(() => this.host.Add(HandlerChar, "other", new BleHubOptions(), this.handler));
    }


    [Fact]
    public void RegistrationNeedsAHubUnlessHandlerHubsAreAllowed()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddBleHubServer(_ => { }));

        var services = new ServiceCollection()
            .AddSingleton<Shiny.BluetoothLE.Hosting.IBleHostingManager>(this.hosting)
            .AddBleHubServer(server => server.AllowHandlerHubs())
            .BuildServiceProvider();
        Assert.Same(services.GetRequiredService<IBleHubHost>(), services.GetRequiredService<IBleHubHandlerHost>());
    }


    [Fact]
    public void DisposingAHostThatNeverStartedLeavesTheRadioAlone()
    {
        this.Build();
        this.host.Add(HandlerChar, "game", new BleHubOptions(), this.handler);
        this.host.Dispose();

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
