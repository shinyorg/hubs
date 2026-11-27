using Microsoft.Extensions.DependencyInjection;
using Shiny.UniversalHubs;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// The peer-link client's resume attempts (each failed attempt's session is closed) and UniversalHubClient's record of
/// the transport carrying the connection
/// </summary>
public class ClientFixesTests : IDisposable
{
    readonly List<ServiceProvider> containers = new();


    public void Dispose()
    {
        foreach (var c in this.containers)
            c.Dispose();
    }


    static async Task WaitFor(Func<bool> condition, int timeoutMs = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Condition was not met in time");
            await Task.Delay(20);
        }
    }


    // ---- resume ----

    static byte[] Reply(uint id, bool accepted, string? reason = null) => PeerProtocol.Encode(
        PeerMessageKind.HandshakeReply, id, null,
        new PeerHandshakeReply(accepted, reason, "Host", "host-1", false, null, "token", 30),
        PeerJsonContext.Default.PeerHandshakeReply
    );


    static async Task<(PeerHubTransport Transport, ScriptedSession Original, RecordingEvents Events)> ConnectResumable(Func<CancellationToken, Task<IPeerConnection>> reopen)
    {
        var events = new RecordingEvents();
        var original = new ScriptedSession(m => m.Kind == PeerMessageKind.Handshake ? Reply(m.Id, true) : null);
        var transport = new PeerHubTransport("Fake", original, events, "char", null, reopen);
        var ack = await transport.Handshake(new HandshakeInfo(FrameCodec.ProtocolVersion, "Alice", null, null, true), CancellationToken.None);
        Assert.True(ack.Accepted);
        return (transport, original, events);
    }


    [Fact]
    public async Task FailedResumeAttemptsCloseTheirSessions()
    {
        var attempts = new List<ScriptedSession>();
        var (_, original, events) = await ConnectResumable(_ =>
        {
            ScriptedSession session;
            lock (attempts)
            {
                // the first two sessions are gone before the resume gets through, the third is taken back
                session = attempts.Count < 2
                    ? new ScriptedSession(_ => throw new IOException("session gone"))
                    : new ScriptedSession(m => m.Kind == PeerMessageKind.Resume ? Reply(m.Id, true) : null);
                attempts.Add(session);
            }
            return Task.FromResult<IPeerConnection>(session);
        });

        original.Drop();
        await WaitFor(() => events.Reconnected == 1);

        Assert.Equal(3, attempts.Count);
        Assert.Equal(1, attempts[0].Disposals);
        Assert.Equal(1, attempts[1].Disposals);
        Assert.Equal(0, attempts[2].Disposals);
        Assert.Empty(events.Closed);
    }


    [Fact]
    public async Task ARefusedResumeClosesItsSessionOnce()
    {
        var attempts = new List<ScriptedSession>();
        var (_, original, events) = await ConnectResumable(_ =>
        {
            var session = new ScriptedSession(m => m.Kind == PeerMessageKind.Resume ? Reply(m.Id, false, "gone") : null);
            lock (attempts)
                attempts.Add(session);
            return Task.FromResult<IPeerConnection>(session);
        });

        original.Drop();
        await WaitFor(() => events.Closed.Count == 1);

        var only = Assert.Single(attempts);
        Assert.Equal(1, only.Disposals);
        Assert.Equal("gone", events.Closed[0].Message);
        Assert.Equal(0, events.Reconnected);
    }


    // ---- the transport carrying the connection ----

    IUniversalHubClient<ITestHub> Client(Action<UniversalHubClientOptions> configure)
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddUniversalHubClient<ITestHub>("test", configure)
            .BuildServiceProvider();
        this.containers.Add(sp);
        return sp.GetRequiredService<IUniversalHubClient<ITestHub>>();
    }


    static HubHostInfo SeenOn(params Type[] transports) => transports.Aggregate(
        new HubHostInfo("Host", "Host", null),
        (host, t) => host.With(t, new FakeEndpoint(t.Name), [])
    );


    [Fact]
    public async Task ConnectingAgainLeavesTransportNamingTheOneCarryingTheConnection()
    {
        var attempts = new List<string>();
        var wired = new Wired(attempts);
        var broken = new Broken(attempts);
        var client = this.Client(o => o.UseTransport(_ => wired).UseTransport(_ => broken));
        var host = SeenOn(typeof(Wired), typeof(Broken));
        IHubClientTransport? seenOnConnected = null;
        client.Connected += (_, _) => seenOnConnected = client.Transport;

        await client.Connect(host, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Same(wired, client.Transport);
        Assert.Same(wired, seenOnConnected);

        // a second connect fails without trying anything, and without touching the live connection
        attempts.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Connect(host, new HubConnectOptions().Prefer<Broken>(), TestContext.Current.CancellationToken));
        Assert.Empty(attempts);
        Assert.Equal(HubClientStatus.Connected, client.Status);
        Assert.Same(wired, client.Transport);

        // so turning the real transport off still disconnects
        await client.Disable<Wired>();
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
        Assert.Null(client.Transport);
    }


    [Fact]
    public async Task AFailedTransportBeforeTheOneThatConnectsIsNotNamed()
    {
        var attempts = new List<string>();
        var wired = new Wired(attempts);
        var client = this.Client(o => o.UseTransport(_ => new Broken(attempts)).UseTransport(_ => wired));

        await client.Connect(SeenOn(typeof(Wired), typeof(Broken)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["Broken", "Wired"], attempts);
        Assert.Same(wired, client.Transport);

        await client.Disable<Broken>();
        Assert.Equal(HubClientStatus.Connected, client.Status);
    }


    [Fact]
    public async Task DisconnectingWhileConnectingDoesNotFallThroughToTheNextTransport()
    {
        var attempts = new List<string>();
        IUniversalHubClient<ITestHub> client = null!;
        var hanging = new Wired(attempts, async () => await client.Disconnect());
        client = this.Client(o => o.UseTransport(_ => hanging).UseTransport(_ => new Fallback(attempts)));

        var ex = await Assert.ThrowsAsync<HubDisconnectedException>(() => client.Connect(SeenOn(typeof(Wired), typeof(Fallback)), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HubDisconnectReason.ClientDisconnect, ex.Disconnect?.Reason);
        Assert.Equal(["Wired"], attempts);
        Assert.Equal(HubClientStatus.Disconnected, client.Status);
        Assert.Null(client.Transport);
    }


    // ---- fakes ----

    /// <summary>
    /// A peer-link session that answers what the client sends through <c>respond</c> (null for no answer, an exception
    /// for a session that is gone)
    /// </summary>
    internal sealed class ScriptedSession(Func<PeerMessage, byte[]?> respond) : PeerConnectionBase
    {
        int disposals;
        public int Disposals => Volatile.Read(ref this.disposals);

        public override string RemoteName => "Host";

        public override void Send(byte[] message)
        {
            var reply = respond(PeerProtocol.Decode(message));
            if (reply != null)
                this.RaiseReceived(reply);
        }

        public override Task SendFile(string path, string name, Action<double>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void Drop() => this.RaiseClosed();

        public override void Dispose()
        {
            Interlocked.Increment(ref this.disposals);
            this.RaiseClosed();
        }
    }


    internal sealed class RecordingEvents : IHubConnectionTransportEvents
    {
        int reconnected;
        public int Reconnected => Volatile.Read(ref this.reconnected);
        public List<HubDisconnect> Closed { get; } = new();

        public void Pushed(string eventName, ReadOnlyMemory<byte> arguments) { }
        public void HostRenamed(string? hostName) { }

        void IHubConnectionTransportEvents.Closed(HubDisconnect disconnect)
        {
            lock (this.Closed)
                this.Closed.Add(disconnect);
        }

        void IHubConnectionTransportEvents.Reconnected() => Interlocked.Increment(ref this.reconnected);
    }


    /// <summary>
    /// Connects through a link that accepts every handshake - <c>duringHandshake</c> runs before the reply
    /// </summary>
    public class Wired(List<string> attempts, Func<Task>? duringHandshake = null) : IHubClientTransport
    {
        public virtual string DisplayName => this.GetType().Name;
        public bool IsSupported => true;
        public IObservable<HubHostSighting> Discover(HubClientContext client) => System.Reactive.Linq.Observable.Never<HubHostSighting>();

        public Task Connect(HubClientContext client, HubHostInfo host, CancellationToken cancellationToken)
        {
            lock (attempts)
                attempts.Add(this.DisplayName);
            return client.Connect(_ => new AcceptingLink(duringHandshake), cancellationToken);
        }
    }

    public sealed class Fallback(List<string> attempts) : Wired(attempts);

    public sealed class Broken(List<string> attempts) : RecordingClientTransport(attempts);


    sealed class AcceptingLink(Func<Task>? duringHandshake) : IHubConnectionTransport
    {
        public async Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken)
        {
            if (duringHandshake != null)
                await duringHandshake();
            return new HandshakeAck(true, null, "Host", 0, false);
        }

        public Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken)
            => Task.FromResult(ReadOnlyMemory<byte>.Empty);

        public IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, CancellationToken cancellationToken)
            => System.Linq.AsyncEnumerable.Empty<ReadOnlyMemory<byte>>();

        public bool CanTransferFiles => false;

        public Task<HubFileTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<HubTransferProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<HubFileTransferResult> Download(string remoteFileName, Stream destination, IProgress<HubTransferProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task Close(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
