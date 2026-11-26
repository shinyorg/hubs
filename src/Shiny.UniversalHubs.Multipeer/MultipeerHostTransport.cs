using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// Serves universal hubs over MultipeerConnectivity. The host advertises once under the Multipeer service type, listing its
/// running hubs, and accepts every invitation into a session of its own - one session per hub connection, so the 8-peer
/// limit of an MCSession never applies and clients never mesh with each other. Each session reaches its hub through the
/// blehubs transport seam, so it shares Clients, Groups, MaxClients and ValidateClient with BLE and HTTP clients.
/// </summary>
public sealed class MultipeerHostTransport : IHubTransport, IPeerHostTransport
{
    readonly IServiceProvider services;
    readonly IMultipeerLink link;
    readonly MultipeerHostOptions options;
    readonly UniversalServerOptions serverOptions;
    readonly ILogger<MultipeerHostTransport>? logger;
    readonly object gate = new();
    readonly List<IHubBinding> running = new();
    // keyed by the session itself: a session that resumes a held connection takes that connection's id
    readonly ConcurrentDictionary<PeerHostSession, byte> sessions = new();
    IDisposable? advertisement;


    internal MultipeerHostTransport(
        IServiceProvider services,
        IMultipeerLink link,
        MultipeerHostOptions options,
        UniversalServerOptions serverOptions,
        ILogger<MultipeerHostTransport>? logger = null
    )
    {
        this.services = services;
        this.link = link;
        this.options = options;
        this.serverOptions = serverOptions;
        this.logger = logger;
    }


    public string DisplayName => "Multipeer";
    public bool IsSupported => link.IsSupported;
    string IPeerHostTransport.ConnectionPrefix => HubConnection.MultipeerPrefix;

    UniversalHubHost IPeerHostTransport.Host => services.GetRequiredService<UniversalHubHost>();
    UniversalServerOptions IPeerHostTransport.ServerOptions => serverOptions;
    ILogger? IPeerHostTransport.Logger => logger;

    /// <summary>
    /// The Multipeer display name hosts advertise as: the configured one, else the BLE local name
    /// </summary>
    internal string AdvertisedName => MultipeerNames.Truncate(options.DisplayName ?? serverOptions.LocalName ?? "Hub host");


    public bool IsRunning(IHubBinding hub)
    {
        lock (this.gate)
            return this.running.Contains(hub);
    }


    public Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        if (!link.IsSupported)
            throw new PlatformNotSupportedException("MultipeerConnectivity is only available on iOS, Mac Catalyst and macOS");

        lock (this.gate)
        {
            var added = hubs.Where(x => !this.running.Contains(x)).ToList();
            this.running.AddRange(added);
            try
            {
                this.Readvertise();
            }
            catch
            {
                foreach (var hub in added)
                    this.running.Remove(hub);
                throw;
            }
        }
        logger?.LogInformation("Multipeer hosting {Hubs} as {Name} ({ServiceType})", String.Join(", ", hubs.Select(x => x.Name)), this.AdvertisedName, options.ServiceType);
        return Task.CompletedTask;
    }


    public async Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect)
    {
        // refuse new handshakes first, then tell everyone connected why they're going
        lock (this.gate)
        {
            foreach (var hub in hubs)
                this.running.Remove(hub);

            this.Readvertise();
        }

        var stopping = this.sessions.Keys.Where(x => x.Binding != null && hubs.Contains(x.Binding)).ToList();
        await Task.WhenAll(stopping.Select(x => x.Binding!.Endpoint.Disconnect(x.ConnectionId, disconnect))).ConfigureAwait(false);

        bool any;
        lock (this.gate)
            any = this.running.Count > 0;

        // nothing left to join - sessions still waiting to hand-shake have nowhere to go
        if (!any)
        {
            foreach (var session in this.sessions.Keys.Where(x => x.Binding == null))
                session.Close();
        }
    }


    /// <summary>
    /// The running hub a handshake asks for, by characteristic UUID
    /// </summary>
    IHubBinding? IPeerHostTransport.FindRunning(string? key)
    {
        lock (this.gate)
            return this.running.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }


    object IPeerHostTransport.CreatePeer(IPeerConnection connection) => new MultipeerPeer(connection.RemoteName);

    void IPeerHostTransport.Remove(PeerHostSession session) => this.sessions.TryRemove(session, out _);

    IEnumerable<PeerHostSession> IPeerHostTransport.Sessions => this.sessions.Keys;


    // under the gate
    void Readvertise()
    {
        this.advertisement?.Dispose();
        this.advertisement = null;

        if (this.running.Count == 0)
            return;

        var hubs = String.Join(",", this.running.Select(x => x.Key.ToLowerInvariant()));
        var info = new Dictionary<string, string>
        {
            [MultipeerProtocol.InfoProtocol] = PeerProtocol.Version.ToString(),
            [MultipeerProtocol.InfoHostId] = serverOptions.HostId,
            [MultipeerProtocol.InfoName] = serverOptions.LocalName ?? this.AdvertisedName,
            [MultipeerProtocol.InfoHubs] = hubs
        };
        this.advertisement = link.Advertise(options.ServiceType, this.AdvertisedName, info, this.Accepted);
    }


    void Accepted(IPeerConnection connection)
    {
        var session = new PeerHostSession(this, connection);
        this.sessions[session] = 0;
        logger?.LogDebug("Multipeer session {Id} with {Peer}", session.ConnectionId, connection.RemoteName);
        connection.Start(session);
    }
}
