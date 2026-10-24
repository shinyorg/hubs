using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// The generated proxy and dispatcher over a non-BLE transport: BleHubClient.ConnectExternal on one side,
/// IBleHubTransportEndpoint on the other, joined in memory
/// </summary>
public class ExternalTransportTests : IAsyncLifetime
{
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

    static ExternalTransportTests() => Json.AddContext(TestJsonContext.Default);

    readonly BleHubProtocolOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(10) };
    readonly BleHubOptions hubOptions = new() { MaxClients = 3 };
    readonly IBleHubSerializer serializer = new ShinyJsonBleHubSerializer();
    readonly Dictionary<string, BleHubClient> bleClients = new();
    ServiceProvider services = null!;
    HubRuntime runtime = null!;
    HubLog log = null!;


    public ValueTask InitializeAsync()
    {
        this.services = new ServiceCollection()
            .AddSingleton<HubLog>()
            .AddTransient<TestHub>()
            .BuildServiceProvider();
        this.log = this.services.GetRequiredService<HubLog>();

        this.runtime = new HubRuntime(
            new BleHubRegistration(typeof(TestHub), CharacteristicUuid, this.hubOptions),
            BleHubDispatchers.Get(typeof(TestHub)),
            this.services,
            this.options,
            this.serializer,
            null
        )
        {
            HostName = "TestHost",
            IsRunning = true,
            Notify = (peer, frame, _) =>
            {
                this.bleClients[peer.Id].ReceiveFrame(frame);
                return Task.CompletedTask;
            }
        };
        return ValueTask.CompletedTask;
    }


    public async ValueTask DisposeAsync() => await this.services.DisposeAsync();


    BleHubClient CreateClient() => BleHubClientFactories.Create<ITestHub>(new BleHubClientServices(this.options, this.serializer), BleHubProtocolOptions.DefaultServiceUuid, CharacteristicUuid);


    async Task<(ITestHub Hub, BleHubClient Client, MemoryTransport Transport)> Connect(string id = "wifi-1", string name = "Alice")
    {
        var client = this.CreateClient();
        MemoryTransport? transport = null;
        await client.ConnectExternal(events => transport = new MemoryTransport(this.runtime, id, events), new BleHubConnectOptions(name));
        return ((ITestHub)client, client, transport!);
    }


    async Task<ITestHub> ConnectBle(string peerId, string name)
    {
        var client = this.CreateClient();
        this.bleClients[peerId] = client;
        await client.ConnectTransport(
            (frame, _) =>
            {
                this.runtime.OnFrame(peerId, 185, null, frame);
                return Task.CompletedTask;
            },
            185,
            new BleHubConnectOptions(name),
            CancellationToken.None
        );
        return (ITestHub)client;
    }


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    [Fact]
    public async Task ConnectRunsOnConnectedAndCallsWork()
    {
        var (hub, client, _) = await this.Connect();

        Assert.Equal(BleHubClientStatus.Connected, client.Status);
        Assert.NotNull(client.ExternalTransport);
        Assert.Null(client.Host);
        Assert.Contains("connected:Alice", this.log.Entries);
        Assert.Equal(["wifi-1"], this.runtime.GetMembers("everyone"));

        Assert.Equal(new Payload("hi", 1), await hub.Echo(new Payload("hi", 1)));
        Assert.Equal("a7b9", await hub.Concat("a", 7, new Payload("b", 9)));
        Assert.Equal("Alice", await hub.WhoAmI());
    }


    [Fact]
    public async Task HubExceptionBecomesRemoteException()
    {
        var (hub, _, _) = await this.Connect();
        var ex = await Assert.ThrowsAsync<BleHubRemoteException>(() => hub.Fail());
        Assert.Equal(nameof(InvalidOperationException), ex.RemoteErrorType);
        Assert.Equal("boom", ex.Message);
    }


    [Fact]
    public async Task Streaming()
    {
        var (hub, _, _) = await this.Connect();
        var items = new List<int>();
        await foreach (var i in hub.Count(5, CancellationToken.None))
            items.Add(i);

        Assert.Equal([1, 2, 3, 4, 5], items);
    }


    [Fact]
    public async Task PushesArriveInOrder()
    {
        var (hub, _, _) = await this.Connect();
        var received = new List<int>();
        hub.Numbered += (_, n) =>
        {
            lock (received)
                received.Add(n);
        };

        await hub.PushSequence(50);
        await WaitFor(() => received.Count == 50);
        Assert.Equal(Enumerable.Range(0, 50), received);
    }


    [Fact]
    public async Task BleAndExternalClientsShareGroupsAndPushes()
    {
        var ble = await this.ConnectBle("ble-1", "Bob");
        var (wifi, _, _) = await this.Connect("wifi-1", "Alice");

        var bleMessages = new List<string>();
        var wifiMessages = new List<string>();
        ble.Message += m => bleMessages.Add(m);
        wifi.Message += m => wifiMessages.Add(m);

        await wifi.JoinGroup("room");
        await ble.JoinGroup("room");
        await ble.SayToGroup("room", "hello");

        await WaitFor(() => bleMessages.Count == 1 && wifiMessages.Count == 1);
        Assert.Equal(2, this.runtime.Clients.Count);

        await wifi.SayToOthers("from wifi");
        await WaitFor(() => bleMessages.Count == 2);
        Assert.Single(wifiMessages);
    }


    [Fact]
    public async Task MaxClientsCountsEveryTransport()
    {
        await this.ConnectBle("ble-1", "A");
        await this.Connect("wifi-1", "B");
        await this.Connect("wifi-2", "C");

        var ex = await Assert.ThrowsAsync<BleHubException>(() => this.Connect("wifi-3", "D"));
        Assert.Contains("Host is full", ex.Message);
    }


    [Fact]
    public async Task ValidateClientApplies()
    {
        this.hubOptions.ValidateClient = info => info.Name == "Mallory" ? "Not you" : null;
        var ex = await Assert.ThrowsAsync<BleHubException>(() => this.Connect(name: "Mallory"));
        Assert.Contains("Not you", ex.Message);
        Assert.Empty(this.runtime.Clients);
    }


    [Fact]
    public async Task HostDisconnectReachesTheClient()
    {
        var (_, client, _) = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        await this.runtime.Disconnect("wifi-1", new HubDisconnect(HubDisconnectReason.ServerDisconnect, "Go away"));

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerDisconnect, "Go away"), disconnect);
        Assert.Equal(BleHubClientStatus.Disconnected, client.Status);
        await WaitFor(() => this.log.Entries.Contains("disconnected:Alice:Go away"));
        Assert.Contains("reason:Alice:ServerDisconnect", this.log.Entries);
    }


    [Fact]
    public async Task AbortTakesEffectAfterTheReply()
    {
        var (hub, client, _) = await this.Connect();
        HubDisconnect? disconnect = null;
        client.Disconnected += (_, d) => disconnect = d;

        await hub.Kick("kicked");

        await WaitFor(() => disconnect != null);
        Assert.Equal(new HubDisconnect(HubDisconnectReason.ServerDisconnect, "kicked"), disconnect);
        Assert.Empty(this.runtime.Clients);
    }


    [Fact]
    public async Task ClientDisconnectRunsOnDisconnected()
    {
        var (_, client, transport) = await this.Connect();
        await client.Disconnect();

        Assert.True(transport.ClosedByApp);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientDisconnect"));
        Assert.Empty(this.runtime.Clients);
    }


    [Fact]
    public async Task LostConnectionFailsPendingCalls()
    {
        var (hub, client, transport) = await this.Connect();
        var slow = hub.Slow(5000, CancellationToken.None);

        transport.Lose("Wi-Fi dropped");

        var ex = await Assert.ThrowsAsync<BleHubDisconnectedException>(() => slow);
        Assert.Equal("Wi-Fi dropped", ex.Reason);
        Assert.Equal(HubDisconnectReason.ClientTimeout, ex.Disconnect!.Reason);
        await WaitFor(() => this.log.Entries.Contains("reason:Alice:ClientTimeout"));
        Assert.Equal(BleHubClientStatus.Disconnected, client.Status);
        Assert.False(transport.ClosedByApp);
    }


    [Fact]
    public async Task FilesGoThroughTheTransport()
    {
        var (_, client, transport) = await this.Connect();
        Assert.True(client.CanTransferFiles);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "avatar bytes");
            var up = await client.UploadFile(path, "avatar.jpg");
            Assert.Equal("avatar.jpg", up.FileName);
            Assert.Equal("avatar bytes", transport.Files["avatar.jpg"]);

            var down = path + ".down";
            await client.DownloadFile("avatar.jpg", down);
            Assert.Equal("avatar bytes", await File.ReadAllTextAsync(down));
            File.Delete(down);
        }
        finally
        {
            File.Delete(path);
        }
    }


    /// <summary>
    /// Both halves of a transport in one object: the client's IBleHubClientTransport and the hub's IBleHubPeerChannel
    /// </summary>
    [Fact]
    public async Task RenameGoesThroughTheTransportAndOtherClientsSeeIt()
    {
        var (_, client, _) = await this.Connect("wifi-1", "Alice");
        var ble = await this.ConnectBle("ble-1", "Bob");
        BleHubClientRenamedEventArgs? renamed = null;
        this.runtime.ClientRenamed += (_, e) => renamed = e;

        await client.Rename("Alicia");

        Assert.Equal("Alicia", client.ClientName);
        Assert.Equal("Alicia", this.runtime.FindClient("wifi-1")!.Name);
        Assert.Equal("Alice", renamed?.PreviousName);
        Assert.Contains("renamed:Alice->Alicia", this.log.Entries);
        Assert.Equal("Bob", await ble.WhoAmI());
    }


    [Fact]
    public async Task RefusedRenameComesBackThroughTheTransport()
    {
        this.hubOptions.ValidateClient = info => info.Name == "mallory" ? "Not you" : null;
        var (hub, client, _) = await this.Connect();

        var ex = await Assert.ThrowsAsync<BleHubRemoteException>(() => client.Rename("mallory"));
        Assert.Equal(BleHubRemoteException.RenameRefused, ex.RemoteErrorType);
        Assert.Equal("Alice", await hub.WhoAmI());
    }


    [Fact]
    public async Task HostRenameReachesClientsOnBothTransports()
    {
        var (_, wifi, _) = await this.Connect();
        var ble = (BleHubClient)(object)await this.ConnectBle("ble-1", "Bob");
        string? wifiSaw = null;
        wifi.HostRenamed += (_, name) => wifiSaw = name;

        await this.runtime.SetHostName("NewHost", CancellationToken.None);

        await WaitFor(() => wifiSaw == "NewHost" && ble.HostName == "NewHost");
        Assert.Equal("NewHost", wifi.HostName);
    }


    [Fact]
    public async Task ExternalMediaChannelReachesTheHub()
    {
        var opened = new List<BleHubMediaChannelEventArgs>();
        this.runtime.MediaChannelOpened += (_, e) => opened.Add(e);
        var (_, client, transport) = await this.Connect();

        Assert.True(client.CanOpenMediaChannel);
        var clientChannel = await client.OpenMediaChannel();
        Assert.Same(clientChannel, await client.OpenMediaChannel());
        Assert.Equal(1, transport.MediaOpens);

        var e = Assert.Single(opened);
        Assert.Equal("wifi-1", e.Client.Id);
        Assert.Same(e.Channel, this.runtime.GetMediaChannel("wifi-1"));

        await clientChannel.Send(new byte[] { 1, 2 });
        await foreach (var packet in e.Channel.Receive())
        {
            Assert.Equal(new byte[] { 1, 2 }, packet.ToArray());
            break;
        }

        // the client's connection drops - the hub closes the channel it was given
        transport.Lose("gone");
        await e.Channel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await clientChannel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(this.runtime.GetMediaChannel("wifi-1"));
    }


    [Fact]
    public async Task ExternalMediaChannelClosesWhenTheClientDisconnects()
    {
        var (_, client, transport) = await this.Connect();
        var clientChannel = await client.OpenMediaChannel();
        var hostChannel = this.runtime.GetMediaChannel("wifi-1")!;

        await client.Disconnect();
        await hostChannel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await clientChannel.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }


    [Fact]
    public async Task MediaChannelForAnUnknownClientIsRefused()
    {
        var link = new FakeL2CapLink(0, "x", "host");
        await using var channel = FramedMediaChannel.Over(link.Host.DataReceived, 100, (b, ct) => Task.CompletedTask, link.Host.Dispose);

        Assert.False(((IBleHubTransportEndpoint)this.runtime).MediaChannelOpened("nobody", channel));
        Assert.Null(this.runtime.GetMediaChannel("nobody"));
    }


    [Fact]
    public async Task TransportsWithoutMediaChannelsSayNo()
    {
        var client = this.CreateClient();
        await client.ConnectExternal(events => new NoMediaTransport(new MemoryTransport(this.runtime, "wifi-9", events)), new BleHubConnectOptions("Old"));

        Assert.False(client.CanOpenMediaChannel);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.OpenMediaChannel());
    }


    /// <summary>
    /// A transport written before media channels - it relies on the interface's defaults
    /// </summary>
    sealed class NoMediaTransport(MemoryTransport inner) : IBleHubClientTransport
    {
        public Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken) => inner.Handshake(info, cancellationToken);
        public Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken) => inner.Invoke(method, arguments, cancellationToken);
        public IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, CancellationToken cancellationToken) => inner.Stream(method, arguments, cancellationToken);
        public bool CanTransferFiles => false;
        public Task<L2CapTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<L2CapTransferResult> Download(string remoteFileName, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task Close(CancellationToken cancellationToken) => inner.Close(cancellationToken);
    }


    sealed class MemoryTransport(HubRuntime runtime, string connectionId, IBleHubClientTransportEvents events)
        : IBleHubClientTransport, IBleHubPeerChannel
    {
        public bool ClosedByApp { get; private set; }
        public int MediaOpens { get; private set; }
        public bool CanOpenMediaChannel => true;

        public Task<IBleHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken)
        {
            this.MediaOpens++;
            var link = new FakeL2CapLink(0, connectionId, "host");
            var hostEnd = FramedMediaChannel.Over(link.Host.DataReceived, 1024, (b, _) => link.Host.Write(b).ToTask(), link.Host.Dispose);
            var clientEnd = FramedMediaChannel.Over(link.Central.DataReceived, 1024, (b, _) => link.Central.Write(b).ToTask(), link.Central.Dispose);

            if (!((IBleHubTransportEndpoint)runtime).MediaChannelOpened(connectionId, hostEnd))
            {
                link.Close();
                throw new BleHubException("Unknown client");
            }
            return Task.FromResult<IBleHubMediaChannel>(clientEnd);
        }
        public Dictionary<string, string> Files { get; } = new();
        public bool CanTransferFiles => true;


        public async Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken)
        {
            var rejection = await runtime.Connect(connectionId, info, this, cancellationToken);
            return new HandshakeAck(rejection == null, rejection, "TestHost", 0, false);
        }


        public async Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken)
        {
            BleHubInvocationResult result;
            try
            {
                result = await runtime.Invoke(connectionId, method, arguments, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BleHubRemoteException(ex.GetType().Name, ex.Message);
            }

            if (result.AbortRequested)
                _ = runtime.Disconnect(connectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, result.AbortReason));

            return result.Result ?? ReadOnlyMemory<byte>.Empty;
        }


        public async IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in runtime.Stream(connectionId, method, arguments, cancellationToken))
                yield return item;
        }


        public async Task<L2CapTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(source);
            this.Files[remoteFileName] = await reader.ReadToEndAsync(cancellationToken);
            return new L2CapTransferResult(L2CapTransferType.Upload, remoteFileName, length, TimeSpan.Zero);
        }


        public async Task<L2CapTransferResult> Download(string remoteFileName, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(this.Files[remoteFileName]);
            await destination.WriteAsync(bytes, cancellationToken);
            return new L2CapTransferResult(L2CapTransferType.Download, remoteFileName, bytes.Length, TimeSpan.Zero);
        }


        public Task Close(CancellationToken cancellationToken)
        {
            this.ClosedByApp = true;
            runtime.Disconnected(connectionId, new HubDisconnect(HubDisconnectReason.ClientDisconnect));
            return Task.CompletedTask;
        }


        public void Lose(string reason)
        {
            var disconnect = new HubDisconnect(HubDisconnectReason.ClientTimeout, reason);
            runtime.Disconnected(connectionId, disconnect);
            events.Closed(disconnect);
        }


        // ---- host -> client ----

        Task IBleHubPeerChannel.Push(string eventName, byte[] arguments, CancellationToken cancellationToken)
        {
            events.Pushed(eventName, arguments);
            return Task.CompletedTask;
        }


        Task IBleHubPeerChannel.Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken)
        {
            events.Closed(disconnect);
            return Task.CompletedTask;
        }

        Task IBleHubPeerChannel.HostRenamed(string? hostName, CancellationToken cancellationToken)
        {
            events.HostRenamed(hostName);
            return Task.CompletedTask;
        }


        public async Task Rename(string? name, CancellationToken cancellationToken)
        {
            if (await runtime.Rename(connectionId, name, cancellationToken) is { } rejection)
                throw new BleHubRemoteException(BleHubRemoteException.RenameRefused, rejection);
        }
    }
}
