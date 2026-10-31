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
internal sealed class MultipeerHostTransport(
    IServiceProvider services,
    IMultipeerLink link,
    MultipeerHostOptions options,
    BleHubHostOptions bleOptions,
    UniversalHubHostOptions httpOptions,
    ILogger<MultipeerHostTransport>? logger = null
) : IUniversalHubTransport, IPeerHostTransport
{
    readonly object gate = new();
    readonly List<IHubBinding> running = new();
    readonly ConcurrentDictionary<string, PeerHostSession> sessions = new(StringComparer.Ordinal);
    IDisposable? advertisement;


    public HubTransports Transport => HubTransports.Multipeer;
    public string ConnectionPrefix => HubConnection.MultipeerPrefix;

    public UniversalHubHost Host => services.GetRequiredService<UniversalHubHost>();
    public BleHubHostOptions BleOptions => bleOptions;
    public UniversalHubHostOptions HttpOptions => httpOptions;
    public ILogger? Logger => logger;

    /// <summary>
    /// The Multipeer display name hosts advertise as: the configured one, else the BLE local name
    /// </summary>
    internal string DisplayName => MultipeerNames.Truncate(options.DisplayName ?? bleOptions.LocalName ?? "Hub host");


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
        logger?.LogInformation("Multipeer hosting {Hubs} as {Name} ({ServiceType})", String.Join(", ", hubs.Select(x => x.Name)), this.DisplayName, options.ServiceType);
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

        var stopping = this.sessions.Values.Where(x => x.Binding != null && hubs.Contains(x.Binding)).ToList();
        await Task.WhenAll(stopping.Select(x => x.Binding!.Endpoint.Disconnect(x.ConnectionId, disconnect))).ConfigureAwait(false);

        bool any;
        lock (this.gate)
            any = this.running.Count > 0;

        // nothing left to join - sessions still waiting to hand-shake have nowhere to go
        if (!any)
        {
            foreach (var session in this.sessions.Values.Where(x => x.Binding == null))
                session.Close();
        }
    }


    /// <summary>
    /// The running hub a handshake asks for, by characteristic UUID
    /// </summary>
    public IHubBinding? FindRunning(string? characteristicUuid)
    {
        lock (this.gate)
            return this.running.FirstOrDefault(x => x.CharacteristicUuid.Equals(characteristicUuid, StringComparison.OrdinalIgnoreCase));
    }


    public object CreatePeer(IPeerConnection connection) => new MultipeerPeer(connection.RemoteName);

    public void Remove(PeerHostSession session) => this.sessions.TryRemove(session.ConnectionId, out _);


    // under the gate
    void Readvertise()
    {
        this.advertisement?.Dispose();
        this.advertisement = null;

        if (this.running.Count == 0)
            return;

        var hubs = String.Join(",", this.running.Select(x => x.CharacteristicUuid.ToLowerInvariant()));
        var info = new Dictionary<string, string>
        {
            [MultipeerProtocol.InfoProtocol] = PeerProtocol.Version.ToString(),
            [MultipeerProtocol.InfoHostId] = httpOptions.HostId,
            [MultipeerProtocol.InfoName] = bleOptions.LocalName ?? this.DisplayName,
            [MultipeerProtocol.InfoHubs] = hubs
        };
        this.advertisement = link.Advertise(options.ServiceType, this.DisplayName, info, this.Accepted);
    }


    void Accepted(IPeerConnection connection)
    {
        var session = new PeerHostSession(this, connection);
        this.sessions[session.ConnectionId] = session;
        logger?.LogDebug("Multipeer session {Id} with {Peer}", session.ConnectionId, connection.RemoteName);
        connection.Start(session);
    }
}
