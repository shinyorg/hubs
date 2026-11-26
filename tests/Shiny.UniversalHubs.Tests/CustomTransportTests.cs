using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// A transport written outside the library: picked by type on a hub, started and stopped by type on the host, and the one
/// HubConnection.Transport reports for its clients
/// </summary>
public class CustomTransportTests : IAsyncLifetime
{
    ServiceProvider services = null!;
    IUniversalHubHost host = null!;
    HubLog log = null!;


    public ValueTask InitializeAsync()
    {
        this.services = Build(server => server
            .AddHub<TestHub>("test", h => h.UseTransport<LoopTransport>(new LoopSettings("test-room")).UseTransport<OtherLoopTransport>())
            .AddHub<OtherHub>("other", h => h.UseTransport<LoopTransport>(new LoopSettings("other-room"))));
        this.host = this.services.GetRequiredService<IUniversalHubHost>();
        this.log = this.services.GetRequiredService<HubLog>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Condition was not met in time");
            await Task.Delay(20);
        }
    }


    static ServiceProvider Build(Action<UniversalServerBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HubLog>();
        services.AddUniversalServer(configure);
        return services.BuildServiceProvider();
    }


    [Fact]
    public async Task ACustomTransportStartsEveryHubSetUpWithIt()
    {
        TransportAssert.Equal([typeof(LoopTransport), typeof(OtherLoopTransport)], await this.host.Start());

        var loop = this.host.GetTransport<LoopTransport>()!;
        Assert.Equal(["other-room", "test-room"], loop.Running.Select(x => x.GetSettings<LoopSettings>()!.Room).Order());
        Assert.True(this.host.IsRunning<LoopTransport>());
        TransportAssert.Equal([typeof(LoopTransport), typeof(OtherLoopTransport)], this.host.GetRunningTransports<TestHub>());
        TransportAssert.Equal([typeof(LoopTransport)], this.host.GetRunningTransports<OtherHub>());
    }


    [Fact]
    public async Task HubsSeeTheCustomTransportTheirClientIsOn()
    {
        await this.host.Start();
        var loop = this.host.GetTransport<LoopTransport>()!;
        var hub = loop.Running.Single(x => x.HubType == typeof(TestHub));

        Assert.Null(await loop.Connect(hub, "loop-1", "Alice"));

        Assert.Contains("connected-via:Loop:loop-1", this.log.Entries);
        var client = Assert.Single(this.services.GetRequiredService<IHubContext<TestHub>>().ConnectedClients);
        Assert.Same(loop, HubConnection.From(client).Transport);
    }


    [Fact]
    public async Task StoppingOneTransportLeavesTheOthersRunning()
    {
        await this.host.Start();
        var loop = this.host.GetTransport<LoopTransport>()!;
        var changed = 0;
        this.host.RunningChanged += (_, _) => changed++;
        await loop.Connect(loop.Running.Single(x => x.HubType == typeof(TestHub)), "loop-1", "Alice");

        await this.host.Stop<LoopTransport>("loop off");

        Assert.False(this.host.IsRunning<LoopTransport>());
        TransportAssert.Equal([typeof(OtherLoopTransport)], this.host.RunningTransports);
        TransportAssert.Equal([typeof(OtherLoopTransport)], this.host.GetRunningTransports<TestHub>());
        Assert.Empty(this.host.GetRunningTransports<OtherHub>());
        await WaitFor(() => this.log.Entries.Contains("disconnected:Alice:loop off"));
        Assert.Equal(1, changed);

        // and back on, for just that transport
        TransportAssert.Equal([typeof(LoopTransport)], await this.host.Start<LoopTransport>());
        TransportAssert.Equal([typeof(LoopTransport)], this.host.GetRunningTransports<OtherHub>());
    }


    [Fact]
    public async Task OneHubStartsAndStopsOnOneTransport()
    {
        TransportAssert.Equal([typeof(OtherLoopTransport)], await this.host.StartHub<TestHub, OtherLoopTransport>());
        Assert.Empty(this.host.GetRunningTransports<OtherHub>());

        await this.host.StartHub<TestHub>();
        await this.host.StopHub<TestHub, OtherLoopTransport>();
        TransportAssert.Equal([typeof(LoopTransport)], this.host.GetRunningTransports<TestHub>());
    }


    [Fact]
    public async Task AskingForATransportTheServerDoesNotHaveIsAMistake()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this.host.Start([typeof(UnsupportedTransport)]));
        await Assert.ThrowsAsync<ArgumentException>(() => this.host.Start([]));
    }


    [Fact]
    public async Task AnUnsupportedTransportIsSkippedQuietly()
    {
        await using var sp = Build(server => server
            .AddHub<TestHub>("test", h => h.UseTransport<LoopTransport>().UseTransport<UnsupportedTransport>()));
        var host = sp.GetRequiredService<IUniversalHubHost>();

        TransportAssert.Equal([typeof(LoopTransport)], await host.Start());
        Assert.Equal(0, host.GetTransport<UnsupportedTransport>()!.Starts);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => host.Start<UnsupportedTransport>());
    }


    [Fact]
    public void RegistrationMistakesWithCustomTransportsThrow()
    {
        Assert.Throws<InvalidOperationException>(() => Build(server => server
            .AddHub<TestHub>("test", h => h.UseTransport<LoopTransport>().UseTransport<LoopTransport>())));

        // set up on the server, but no hub uses it
        Assert.Throws<InvalidOperationException>(() => Build(server => server
            .AddTransport<OtherLoopTransport>()
            .AddHub<TestHub>("test", h => h.UseTransport<LoopTransport>())));

        // fine for dynamic hubs, which are served over every transport the server has
        using var sp = Build(server => server.AddTransport<LoopTransport>().AllowDynamicHubs());
        Assert.IsType<LoopTransport>(Assert.Single(sp.GetRequiredService<IUniversalHubHost>().Transports));
    }
}


public sealed record LoopSettings(string Room);


/// <summary>
/// An in-memory transport: it keeps the hubs it was started with and connects clients straight to their endpoint
/// </summary>
public class LoopTransport : IHubTransport
{
    readonly List<IHubBinding> running = new();

    public virtual string DisplayName => "Loop";
    public virtual bool IsSupported => true;

    public IReadOnlyList<IHubBinding> Running
    {
        get { lock (this.running) return this.running.ToList(); }
    }

    public int Starts { get; private set; }

    public bool IsRunning(IHubBinding hub)
    {
        lock (this.running)
            return this.running.Contains(hub);
    }

    public Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        this.Starts++;
        lock (this.running)
            this.running.AddRange(hubs.Where(x => !this.running.Contains(x)));
        return Task.CompletedTask;
    }

    public async Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect)
    {
        lock (this.running)
            this.running.RemoveAll(hubs.Contains);

        foreach (var hub in hubs)
        {
            foreach (var id in hub.Endpoint is HubRuntime runtime ? runtime.ConnectionIds(x => x is LoopChannel c && c.Transport == this) : [])
                await hub.Endpoint.Disconnect(id, disconnect);
        }
    }

    public Task<string?> Connect(IHubBinding hub, string connectionId, string name)
        => hub.Endpoint.Connect(
            connectionId,
            new HandshakeInfo(FrameCodec.ProtocolVersion, name, null, new Dictionary<string, string>()),
            new LoopChannel(this),
            CancellationToken.None
        );


    sealed class LoopChannel(LoopTransport transport) : IBleHubPeerChannel
    {
        public IHubTransport Transport => transport;
        public Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}


public sealed class OtherLoopTransport : LoopTransport
{
    public override string DisplayName => "Other loop";
}


public sealed class UnsupportedTransport : LoopTransport
{
    public override string DisplayName => "Unsupported";
    public override bool IsSupported => false;
}
