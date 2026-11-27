using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// NearbyLink's limits and clean-up - oversized messages, unfinished files, abandoned connections, files that never
/// report back, temp files nobody takes, early file updates, and re-advertising without a gap
/// </summary>
public class NearbyLinkFixesTests : IAsyncDisposable
{
    const string ServiceId = "com.shiny.nearbyfixes";
    const string CharacteristicUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string OtherCharacteristicUuid = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";
    const string MdnsServiceType = "_nbfix._tcp";

    static NearbyLinkFixesTests() => Json.AddContext(TestJsonContext.Default);

    readonly FakeNearbyNetwork network = new();
    readonly List<string> tempFiles = new();
    TestHttpServer? app;


    public async ValueTask DisposeAsync()
    {
        if (this.app != null)
            await this.app.DisposeAsync();

        foreach (var file in this.tempFiles.Where(File.Exists))
            File.Delete(file);
    }


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition(), "condition not met in time");
    }


    string TempFile(int length = 1000)
    {
        var path = Path.Combine(Path.GetTempPath(), "nearby-fixes-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[length]);
        this.tempFiles.Add(path);
        return path;
    }


    /// <summary>
    /// A link that advertises and keeps every channel a client opens
    /// </summary>
    static async Task<(NearbyLink Link, List<Handler> Accepted)> Host(INearbyRadio radio, HubProtocolOptions? protocol = null)
    {
        var link = new NearbyLink(radio, null, protocol);
        var accepted = new List<Handler>();
        await link.Advertise(ServiceId, NearbyStrategy.Cluster, new NearbyEndpointInfo("host", "Host", [1]), connection =>
        {
            var handler = new Handler();
            lock (accepted)
                accepted.Add(handler);
            connection.Start(handler);
        });
        return (link, accepted);
    }


    static async Task<(NearbyLink Link, IPeerConnection Channel, Handler Handler)> Client(INearbyRadio radio, string hostEndpoint)
    {
        var link = new NearbyLink(radio);
        var channel = await link.Connect(hostEndpoint, "Client", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var handler = new Handler();
        channel.Start(handler);
        return (link, channel, handler);
    }


    // ---- limits ----

    [Fact]
    public async Task AMessageOverTheLimitDropsTheSender()
    {
        var hostRadio = this.network.CreateDevice();
        var (_, accepted) = await Host(hostRadio, new HubProtocolOptions { MaxPayloadSize = 1024 });
        var (_, channel, handler) = await Client(this.network.CreateDevice(), hostRadio.EndpointId);
        await WaitFor(() => { lock (accepted) return accepted.Count == 1; });

        // just under the limit arrives...
        channel.Send(new byte[1024 + NearbyLink.MessageHeadroom]);
        await WaitFor(() => accepted[0].Messages.Count == 1);

        // ...a byte over is an attack: the host hangs up before it has buffered it all
        channel.Send(new byte[1024 + NearbyLink.MessageHeadroom + 1]);
        await WaitFor(() => handler.IsClosed && accepted[0].IsClosed);
        Assert.Empty(this.network.Connections);
        Assert.Single(accepted[0].Messages);
    }


    [Fact]
    public async Task TooManyUnfinishedFilesDropTheSender()
    {
        var hostRadio = this.network.CreateDevice();
        var clientRadio = this.network.CreateDevice();
        var (_, accepted) = await Host(hostRadio);
        var (_, _, handler) = await Client(clientRadio, hostRadio.EndpointId);
        await WaitFor(() => { lock (accepted) return accepted.Count == 1; });

        // file names for files that never come, straight onto the client's first channel (opener, id 1)
        for (var i = 0; i < NearbyLink.MaxPendingFilesPerRemote; i++)
            clientRadio.SendBytes(hostRadio.EndpointId, NearbyProtocol.FileNameFrame(true, 1, 1000 + i, "f" + i));

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(this.network.Connections);

        clientRadio.SendBytes(hostRadio.EndpointId, NearbyProtocol.FileNameFrame(true, 1, 5000, "one too many"));
        await WaitFor(() => handler.IsClosed && accepted[0].IsClosed);
        Assert.Empty(this.network.Connections);
    }


    // ---- connections ----

    [Fact]
    public async Task ACanceledConnectHangsUpTheConnectionItAskedFor()
    {
        var hostRadio = new ScriptedRadio(this.network.CreateDevice());
        var clientRadio = new ScriptedRadio(this.network.CreateDevice());
        var hostAccepting = new TaskCompletionSource();
        var hostMayAccept = new TaskCompletionSource();
        var clientAccepted = new TaskCompletionSource();
        hostRadio.BeforeAccept = async () =>
        {
            hostAccepting.TrySetResult();
            await hostMayAccept.Task;
        };
        clientRadio.BeforeAccept = () =>
        {
            clientAccepted.TrySetResult();
            return Task.CompletedTask;
        };

        await Host(hostRadio);
        var link = new NearbyLink(clientRadio);
        using var cts = new CancellationTokenSource();
        var connecting = link.Connect(hostRadio.EndpointId, "Client", TimeSpan.FromSeconds(30), cts.Token);
        await hostAccepting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await clientAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);

        // the host accepts late - the connection completes, and the client, wanting it no more, hangs up
        hostMayAccept.TrySetResult();
        await WaitFor(() => this.network.Connections.Count == 0 && hostRadio.Results > 0);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(this.network.Connections);
    }


    // ---- files ----

    [Fact]
    public async Task AFileThatNeverReportsBackFailsWhenTheConnectionGoes()
    {
        var hostRadio = this.network.CreateDevice();
        var clientRadio = new ScriptedRadio(this.network.CreateDevice()) { SilentFiles = true };
        await Host(hostRadio);
        var (_, channel, _) = await Client(clientRadio, hostRadio.EndpointId);

        var sending = channel.SendFile(this.TempFile(), "lost.bin", null, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(sending.IsCompleted);

        hostRadio.DropAll();
        await Assert.ThrowsAsync<HubDisconnectedException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }


    [Fact]
    public async Task AnUpdateBeforeSendFileReturnsStillFindsTheFile()
    {
        var hostRadio = this.network.CreateDevice();
        var clientRadio = new ScriptedRadio(this.network.CreateDevice()) { ReportBeforeReturning = true };
        await Host(hostRadio);
        var (_, channel, _) = await Client(clientRadio, hostRadio.EndpointId);

        // the radio says the file went before handing back its id - it must still complete the send
        await channel.SendFile(this.TempFile(), "early.bin", null, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task AFileFromAnUnknownDeviceIsDeleted()
    {
        var (link, _) = await Host(this.network.CreateDevice());
        var path = this.TempFile();

        link.FileTransfer("EP99", 42, 1000, 1000, NearbyTransferStatus.Success, path);
        await WaitFor(() => !File.Exists(path));
    }


    [Fact]
    public async Task AFileNamedForAClosedChannelIsDeleted()
    {
        var hostRadio = this.network.CreateDevice();
        var clientRadio = this.network.CreateDevice();
        var (link, _) = await Host(hostRadio);
        await Client(clientRadio, hostRadio.EndpointId);

        // channel 77 was never open
        clientRadio.SendBytes(hostRadio.EndpointId, NearbyProtocol.FileNameFrame(true, 77, 42, "orphan.bin"));
        var path = this.TempFile();
        link.FileTransfer(clientRadio.EndpointId, 42, 1000, 1000, NearbyTransferStatus.Success, path);
        await WaitFor(() => !File.Exists(path));
    }


    [Fact]
    public async Task AFileWhoseChannelClosedBeforeItArrivedIsDeleted()
    {
        var hostRadio = this.network.CreateDevice();
        var clientRadio = this.network.CreateDevice();
        var (link, accepted) = await Host(hostRadio);
        var (_, channel, _) = await Client(clientRadio, hostRadio.EndpointId);
        await WaitFor(() => { lock (accepted) return accepted.Count == 1; });

        clientRadio.SendBytes(hostRadio.EndpointId, NearbyProtocol.FileNameFrame(true, 1, 42, "late.bin"));
        channel.Dispose();
        await WaitFor(() => accepted[0].IsClosed);

        var path = this.TempFile();
        link.FileTransfer(clientRadio.EndpointId, 42, 1000, 1000, NearbyTransferStatus.Success, path);
        await WaitFor(() => !File.Exists(path));
        Assert.Empty(accepted[0].Files);
    }


    [Fact]
    public async Task AnUnnamedFileIsDeletedWhenItsDeviceGoes()
    {
        var hostRadio = this.network.CreateDevice();
        var clientRadio = this.network.CreateDevice();
        var (link, _) = await Host(hostRadio);
        await Client(clientRadio, hostRadio.EndpointId);

        var path = this.TempFile();
        link.FileTransfer(clientRadio.EndpointId, 42, 1000, 1000, NearbyTransferStatus.Success, path);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path));

        clientRadio.DropAll();
        await WaitFor(() => !File.Exists(path));
    }


    // ---- re-advertising ----

    [Fact]
    public async Task ReadvertisingKeepsAcceptingConnections()
    {
        var hostRadio = new ScriptedRadio(this.network.CreateDevice());
        this.app = BuildHost(hostRadio);
        var host = this.app.Services.GetRequiredService<IUniversalHubHost>();
        await host.Start();
        var transport = this.app.Services.GetRequiredService<NearbyHostTransport>();

        // stopping one hub re-advertises the other - hold the radio inside it
        var stopping = new TaskCompletionSource();
        var mayFinish = new TaskCompletionSource();
        hostRadio.OnStopAdvertising = () =>
        {
            stopping.TrySetResult();
            mayFinish.Task.Wait(TimeSpan.FromSeconds(10));
        };
        var stop = Task.Run(() => host.StopHub<OtherHub>(), TestContext.Current.CancellationToken);
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        try
        {
            // a client arriving meanwhile is still let in, and its channel kept
            var (_, _, handler) = await Client(this.network.CreateDevice(), hostRadio.EndpointId);
            await WaitFor(() => ((IPeerHostTransport)transport).Sessions.Any());
            Assert.False(handler.IsClosed);
        }
        finally
        {
            hostRadio.OnStopAdvertising = null;
            mayFinish.TrySetResult();
            await stop;
        }
        Assert.Equal(ServiceId, hostRadio.Inner.AdvertisingService);
    }


    static TestHttpServer BuildHost(INearbyRadio radio)
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Services.AddSingleton(NoBluetoothHosting.Create());
        builder.Services.AddSingleton(radio);
        builder.Services.AddSingleton<HubLog>();
        builder.Services.AddUniversalServer(server => server
            .Configure(o =>
            {
                o.LocalName = "FixHost";
                o.HostId = "fix-host";
            })
            .Http(o =>
            {
                o.Advertise = false;
                o.ManageHttpServer = false;
            })
            .Nearby(ServiceId)
            .AddHub<TestHub>(CharacteristicUuid, h => h.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, CharacteristicUuid).UseHttp(MdnsServiceType).UseNearby())
            .AddHub<OtherHub>(OtherCharacteristicUuid, h => h.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, OtherCharacteristicUuid).UseHttp(MdnsServiceType).UseNearby())
        );
        return TestHttpServer.For(builder.Build());
    }


    sealed class Handler : IPeerConnectionHandler
    {
        readonly object gate = new();
        readonly List<byte[]> received = new();
        readonly List<string> files = new();
        volatile bool closed;

        public bool IsClosed => this.closed;

        public IReadOnlyList<byte[]> Messages
        {
            get
            {
                lock (this.gate)
                    return this.received.ToList();
            }
        }

        public IReadOnlyList<string> Files
        {
            get
            {
                lock (this.gate)
                    return this.files.ToList();
            }
        }

        public void Received(ReadOnlyMemory<byte> message)
        {
            lock (this.gate)
                this.received.Add(message.ToArray());
        }

        public void FileProgress(string name, double fraction) { }

        public void FileReceived(string name, string? path, Exception? error)
        {
            lock (this.gate)
                this.files.Add(name);
        }

        public void Closed() => this.closed = true;
    }


    /// <summary>
    /// A fake radio with hooks: a slow accept, a stop-advertising that blocks, files that never report back, and file
    /// updates that arrive before SendFile returns
    /// </summary>
    sealed class ScriptedRadio(FakeNearbyRadio inner) : INearbyRadio, INearbyRadioListener
    {
        INearbyRadioListener listener = null!;
        long nextSilent = 1_000_000;
        int results;

        public FakeNearbyRadio Inner => inner;
        public string EndpointId => inner.EndpointId;
        public Func<Task>? BeforeAccept { get; set; }
        public Action? OnStopAdvertising { get; set; }
        public bool SilentFiles { get; set; }
        public bool ReportBeforeReturning { get; set; }
        public int Results => Volatile.Read(ref this.results);

        public bool IsSupported => true;

        public void Start(INearbyRadioListener listener)
        {
            this.listener = listener;
            inner.Start(this);
        }

        public Task StartAdvertising(string serviceId, byte[] endpointInfo, NearbyStrategy strategy) => inner.StartAdvertising(serviceId, endpointInfo, strategy);

        public void StopAdvertising()
        {
            this.OnStopAdvertising?.Invoke();
            inner.StopAdvertising();
        }

        public Task StartDiscovery(string serviceId, NearbyStrategy strategy) => inner.StartDiscovery(serviceId, strategy);
        public void StopDiscovery() => inner.StopDiscovery();
        public Task RequestConnection(string endpointId, byte[] info) => inner.RequestConnection(endpointId, info);

        public async Task AcceptConnection(string endpointId)
        {
            if (this.BeforeAccept is { } before)
                await before();
            await inner.AcceptConnection(endpointId);
        }

        public void RejectConnection(string endpointId) => inner.RejectConnection(endpointId);
        public void SendBytes(string endpointId, byte[] data) => inner.SendBytes(endpointId, data);

        public long SendFile(string endpointId, string path)
        {
            if (this.SilentFiles)
                return Interlocked.Increment(ref this.nextSilent);

            if (!this.ReportBeforeReturning)
                return inner.SendFile(endpointId, path);

            // the update is out before the id is - and given time to be handled first, as a busy radio thread would
            var id = Interlocked.Increment(ref this.nextSilent);
            this.listener.FileTransfer(endpointId, id, 10, 10, NearbyTransferStatus.Success, null);
            Thread.Sleep(100);
            return id;
        }

        public void CancelFile(long payloadId) => inner.CancelFile(payloadId);
        public void Disconnect(string endpointId) => inner.Disconnect(endpointId);

        // what the fake network reports, passed on
        public void EndpointFound(string endpointId, byte[] info) => this.listener.EndpointFound(endpointId, info);
        public void EndpointLost(string endpointId) => this.listener.EndpointLost(endpointId);
        public void ConnectionInitiated(string endpointId, byte[] info, bool incoming) => this.listener.ConnectionInitiated(endpointId, info, incoming);

        public void ConnectionResult(string endpointId, bool connected, string? error)
        {
            Interlocked.Increment(ref this.results);
            this.listener.ConnectionResult(endpointId, connected, error);
        }

        public void Disconnected(string endpointId) => this.listener.Disconnected(endpointId);
        public void BytesReceived(string endpointId, byte[] data) => this.listener.BytesReceived(endpointId, data);
        public void FileTransfer(string endpointId, long payloadId, long transferred, long total, NearbyTransferStatus status, string? receivedPath)
            => this.listener.FileTransfer(endpointId, payloadId, transferred, total, status, receivedPath);
    }
}
