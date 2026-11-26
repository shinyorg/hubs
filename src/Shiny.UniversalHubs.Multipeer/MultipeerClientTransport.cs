using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.UniversalHubs;

/// <summary>
/// Finds universal hub hosts over MultipeerConnectivity and connects to them - one session per hub connection
/// </summary>
internal sealed class MultipeerClientTransport(IMultipeerLink link, MultipeerClientOptions options, ILoggerFactory? loggerFactory) : IUniversalHubClientTransport
{
    readonly ILogger? logger = loggerFactory?.CreateLogger<MultipeerClientTransport>();


    public HubTransports Transport => HubTransports.Multipeer;


    // nothing to find where there is no MultipeerConnectivity (Android) - not an error worth logging on every scan
    public IObservable<HubHostSighting> Discover(BleHubClient client) => !link.IsSupported ? Observable.Empty<HubHostSighting>() : Observable.Create<HubHostSighting>(observer =>
    {
        // a lost peer only comes with its peer - remember what it was called when it was found
        var names = new ConcurrentDictionary<object, string>();
        return link
            .Browse(options.ServiceType, this.DisplayName(null))
            .Subscribe(
                seen =>
                {
                    if (seen.Info == null)
                    {
                        if (names.TryRemove(seen.Peer, out var gone))
                            observer.OnNext(new HubHostSighting(gone, null, x => x with { Multipeer = null }));
                    }
                    else if (this.Serves(client, seen))
                    {
                        var name = seen.Info.TryGetValue(MultipeerProtocol.InfoName, out var n) ? n : seen.DisplayName;
                        var hostId = seen.Info.TryGetValue(MultipeerProtocol.InfoHostId, out var id) ? id : null;
                        var host = new MultipeerHost(seen.DisplayName, seen.Info) { Peer = seen.Peer };
                        names[seen.Peer] = name;
                        observer.OnNext(new HubHostSighting(name, hostId, x => x with { Multipeer = host }));
                    }
                },
                observer.OnError
            );
    });


    public async Task Connect(BleHubClient client, HubHostInfo host, BleHubConnectOptions connectOptions, CancellationToken cancellationToken)
    {
        var peer = host.Multipeer?.Peer ?? throw new BleHubException($"{host.Name} was not seen over Multipeer");
        var connection = await link.Connect(peer, options.InviteTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.ConnectExternal(
                events => new PeerHubTransport(
                    HubTransports.Multipeer,
                    connection,
                    events,
                    client.HubName,
                    loggerFactory?.CreateLogger<PeerHubTransport>(),
                    // a resume invites the same peer again - its MCPeerID lasts as long as the host's process
                    connectOptions.AutomaticReconnect ? ct => link.Connect(peer, options.InviteTimeout, ct) : null
                ),
                connectOptions,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }


    string DisplayName(string? name) => MultipeerNames.Truncate(options.DisplayName ?? name ?? "Hub client");


    bool Serves(BleHubClient client, MultipeerSighting seen)
    {
        if (!seen.Info!.TryGetValue(MultipeerProtocol.InfoHubs, out var hubs))
            return false;

        var mine = client.HubName;
        if (!hubs.Split(',').Any(x => x.Trim().Equals(mine, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (seen.Info.TryGetValue(MultipeerProtocol.InfoProtocol, out var proto) && proto != PeerProtocol.Version.ToString())
        {
            this.logger?.LogInformation("Ignoring {Host}: it speaks Multipeer hub protocol {Protocol}", seen.DisplayName, proto);
            return false;
        }
        return true;
    }
}
