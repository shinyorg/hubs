using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// A hub's own serializer: its name travels in the handshake, and a client using another is refused
/// </summary>
public class SerializerTests : IAsyncLifetime
{
    const string ServiceType = "_sertest._tcp";

    static SerializerTests() => Json.AddContext(TestJsonContext.Default);

    readonly List<ServiceProvider> clients = new();
    TestHttpServer app = null!;


    public async ValueTask InitializeAsync()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Services.AddSingleton<HubLog>();
        builder.Services.AddUniversalServer(server => server
            .Http(o =>
            {
                o.Advertise = false;
                o.ManageHttpServer = false;
            })
            .AddHub<TestHub>("own", h => h.UseHttp(ServiceType).Serializer(new NamedSerializer("json-v2")))
            .AddHub<OtherHub>("default", h => h.UseHttp(ServiceType)));
        this.app = TestHttpServer.For(builder.Build());
        await this.app.Services.GetRequiredService<IUniversalHubHost>().Start();
    }


    public async ValueTask DisposeAsync()
    {
        foreach (var c in this.clients)
            await c.DisposeAsync();
        await this.app.DisposeAsync();
    }


    IUniversalHubClient<TContract> Client<TContract>(string hubName, IHubSerializer? serializer) where TContract : class
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddUniversalHubClient<TContract>(hubName, o =>
            {
                o.UseHttp(ServiceType, h => h.MessageHandlerFactory = () => this.app.Server.CreateInMemoryHandler());
                if (serializer != null)
                    o.Serializer(serializer);
            })
            .BuildServiceProvider();
        this.clients.Add(sp);
        return sp.GetRequiredService<IUniversalHubClient<TContract>>();
    }


    [Fact]
    public async Task AClientWithTheHubsSerializerIsLetIn()
    {
        var serializer = new NamedSerializer("json-v2");
        var client = this.Client<ITestHub>("own", serializer);
        await client.Connect(HttpHubHost.FromUrl(new Uri("http://localhost")), new HubConnectOptions("Alice"), TestContext.Current.CancellationToken);

        Assert.Equal(new Payload("hi", 1), await client.Hub.Echo(new Payload("hi", 1)));
        Assert.True(serializer.Used > 0);
    }


    [Fact]
    public async Task AClientWithAnotherSerializerIsRefused()
    {
        var client = this.Client<ITestHub>("own", null);
        var ex = await Assert.ThrowsAsync<HubException>(() => client.Connect(HttpHubHost.FromUrl(new Uri("http://localhost")), new HubConnectOptions("Alice"), TestContext.Current.CancellationToken));

        Assert.Contains("Serializer mismatch", ex.Message);
        Assert.Contains("json-v2", ex.Message);
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
    }


    [Fact]
    public async Task HubsWithoutTheirOwnUseTheAppsSerializer()
    {
        var client = this.Client<IOtherHub>("default", null);
        await client.Connect(HttpHubHost.FromUrl(new Uri("http://localhost")), new HubConnectOptions("Alice"), TestContext.Current.CancellationToken);
        Assert.Equal("pong", await client.Hub.Ping());

        var mismatched = this.Client<IOtherHub>("default", new NamedSerializer("json-v2"));
        await Assert.ThrowsAsync<HubException>(() => mismatched.Connect(HttpHubHost.FromUrl(new Uri("http://localhost")), new HubConnectOptions("Bob"), TestContext.Current.CancellationToken));
    }


    sealed class NamedSerializer(string name) : IHubSerializer
    {
        readonly JsonHubSerializer json = new();
        int used;

        public string Name => name;
        public int Used => this.used;

        public byte[] Serialize<T>(T value)
        {
            Interlocked.Increment(ref this.used);
            return this.json.Serialize(value);
        }

        public T Deserialize<T>(ReadOnlySpan<byte> data) => this.json.Deserialize<T>(data);
    }
}
