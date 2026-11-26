using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.UniversalHubs;

/// <summary>
/// Serves universal hubs over Nearby Connections. The host advertises once under the Nearby service id, listing its
/// running hubs, and every channel a client opens becomes a hub session of its own. Each session reaches its hub through
/// the blehubs transport seam, so it shares Clients, Groups, MaxClients and ValidateClient with every other transport.
/// </summary>
public sealed class NearbyHostTransport : IHubTransport, IPeerHostTransport
{
    readonly IServiceProvider services;
    readonly NearbyLink link;
    readonly NearbyHostOptions options;
    readonly UniversalServerOptions serverOptions;
    readonly ILogger<NearbyHostTransport>? logger;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly List<IHubBinding> running = new();
    // keyed by the session itself: a session that resumes a held connection takes that connection's id
    readonly ConcurrentDictionary<PeerHostSession, byte> sessions = new();
    IDisposable? advertisement;


    internal NearbyHostTransport(
        IServiceProvider services,
        NearbyLink link,
        NearbyHostOptions options,
        UniversalServerOptions serverOptions,
        ILogger<NearbyHostTransport>? logger = null
    )
    {
        this.services = services;
        this.link = link;
        this.options = options;
        this.serverOptions = serverOptions;
        this.logger = logger;
    }


    public string DisplayName => "Nearby";
    public bool IsSupported => link.IsSupported;
    IHubTransport IPeerHostTransport.HubTransport => this;
    string IPeerHostTransport.ConnectionPrefix => HubConnection.NearbyPrefix;

    UniversalHubHost IPeerHostTransport.Host => services.GetRequiredService<UniversalHubHost>();
    UniversalServerOptions IPeerHostTransport.ServerOptions => serverOptions;
    ILogger? IPeerHostTransport.Logger => logger;

    /// <summary>
    /// The name hosts advertise as: the configured one, else the BLE local name
    /// </summary>
    internal string EndpointName => options.EndpointName ?? serverOptions.LocalName ?? "Hub host";


    public bool IsRunning(IHubBinding hub)
    {
        lock (this.running)
            return this.running.Contains(hub);
    }


    public async Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        if (!link.IsSupported)
            throw new PlatformNotSupportedException("Nearby Connections is only available on Android (with Google Play services), iOS and macOS");

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<IHubBinding> added;
            lock (this.running)
            {
                added = hubs.Where(x => !this.running.Contains(x)).ToList();
                this.running.AddRange(added);
            }
            try
            {
                await this.Readvertise().ConfigureAwait(false);
            }
            catch
            {
                lock (this.running)
                {
                    foreach (var hub in added)
                        this.running.Remove(hub);
                }
                throw;
            }
        }
        finally
        {
            this.gate.Release();
        }
        logger?.LogInformation("Nearby hosting {Hubs} as {Name} ({ServiceId})", String.Join(", ", hubs.Select(x => x.Name)), this.EndpointName, options.ServiceId);
    }


    public async Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect)
    {
        // refuse new handshakes first, then tell everyone connected why they're going
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (this.running)
            {
                foreach (var hub in hubs)
                    this.running.Remove(hub);
            }
            await this.Readvertise().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Nearby could not re-advertise");
        }
        finally
        {
            this.gate.Release();
        }

        var stopping = this.sessions.Keys.Where(x => x.Binding != null && hubs.Contains(x.Binding)).ToList();
        await Task.WhenAll(stopping.Select(x => x.Binding!.Endpoint.Disconnect(x.ConnectionId, disconnect))).ConfigureAwait(false);

        bool any;
        lock (this.running)
            any = this.running.Count > 0;

        // nothing left to join - sessions still waiting to hand-shake have nowhere to go
        if (!any)
        {
            foreach (var session in this.sessions.Keys.Where(x => x.Binding == null))
                session.Close();
        }
    }


    IHubBinding? IPeerHostTransport.FindRunning(string? key)
    {
        lock (this.running)
            return this.running.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }


    object IPeerHostTransport.CreatePeer(IPeerConnection connection) => new NearbyPeer(connection.RemoteName);

    void IPeerHostTransport.Remove(PeerHostSession session) => this.sessions.TryRemove(session, out _);

    IEnumerable<PeerHostSession> IPeerHostTransport.Sessions => this.sessions.Keys;


    // under the gate
    async Task Readvertise()
    {
        this.advertisement?.Dispose();
        this.advertisement = null;

        List<uint> hubs;
        lock (this.running)
            hubs = this.running.Select(x => NearbyProtocol.HubTag(x.Key)).ToList();

        if (hubs.Count == 0)
            return;

        this.advertisement = await link.Advertise(
            options.ServiceId,
            options.Strategy,
            new NearbyEndpointInfo(serverOptions.HostId, this.EndpointName, hubs),
            this.Accepted
        ).ConfigureAwait(false);
    }


    void Accepted(IPeerConnection connection)
    {
        var session = new PeerHostSession(this, connection);
        this.sessions[session] = 0;
        logger?.LogDebug("Nearby session {Id} with {Peer}", session.ConnectionId, connection.RemoteName);
        connection.Start(session);
    }
}
