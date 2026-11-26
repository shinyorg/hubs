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
/// A host seen over MultipeerConnectivity
/// </summary>
/// <param name="DisplayName">The host's Multipeer display name</param>
/// <param name="Info">What it advertises - the same keys as its mDNS TXT record</param>
public sealed record MultipeerHubEndpoint(string DisplayName, IReadOnlyDictionary<string, string> Info) : HubEndpoint
{
    /// <summary>
    /// The native peer (an MCPeerID)
    /// </summary>
    internal object? Peer { get; init; }
}


/// <summary>
/// Finds universal hub hosts over MultipeerConnectivity and connects to them - one session per hub connection. Apple only:
/// elsewhere <see cref="IsSupported"/> is false and it is left out. Added with <c>o.UseMultipeer(...)</c>.
/// </summary>
public sealed class MultipeerClientTransport : IHubClientTransport
{
    readonly IMultipeerLink link;
    readonly MultipeerClientOptions options;
    readonly ILoggerFactory? loggerFactory;
    readonly ILogger? logger;


    internal MultipeerClientTransport(IMultipeerLink link, MultipeerClientOptions options, ILoggerFactory? loggerFactory)
    {
        this.link = link;
        this.options = options;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory?.CreateLogger<MultipeerClientTransport>();
    }


    public string DisplayName => "Multipeer";
    public bool IsSupported => this.link.IsSupported;


    // nothing to find where there is no MultipeerConnectivity (Android) - not an error worth logging on every scan
    public IObservable<HubHostSighting> Discover(HubClientContext client) => !link.IsSupported ? Observable.Empty<HubHostSighting>() : Observable.Create<HubHostSighting>(observer =>
    {
        // a lost peer only comes with its peer - remember what it was called when it was found
        var names = new ConcurrentDictionary<object, string>();
        return link
            .Browse(options.ServiceType, this.PeerName(null))
            .Subscribe(
                seen =>
                {
                    if (seen.Info == null)
                    {
                        if (names.TryRemove(seen.Peer, out var gone))
                            observer.OnNext(new HubHostSighting(gone, null, null));
                    }
                    else if (this.Serves(client.HubName, seen))
                    {
                        var name = seen.Info.TryGetValue(MultipeerProtocol.InfoName, out var n) ? n : seen.DisplayName;
                        var hostId = seen.Info.TryGetValue(MultipeerProtocol.InfoHostId, out var id) ? id : null;
                        var host = new MultipeerHubEndpoint(seen.DisplayName, seen.Info) { Peer = seen.Peer };
                        names[seen.Peer] = name;
                        observer.OnNext(new HubHostSighting(name, hostId, host));
                    }
                },
                observer.OnError
            );
    });


    public async Task Connect(HubClientContext client, HubHostInfo host, CancellationToken cancellationToken)
    {
        var connectOptions = client.ConnectOptions;
        var peer = host.Endpoint<MultipeerHubEndpoint>()?.Peer ?? throw new BleHubException($"{host.Name} was not seen over Multipeer");
        var connection = await link.Connect(peer, options.InviteTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.Connect(
                events => new PeerHubTransport(
                    this.DisplayName,
                    connection,
                    events,
                    client.HubName,
                    loggerFactory?.CreateLogger<PeerHubTransport>(),
                    // a resume invites the same peer again - its MCPeerID lasts as long as the host's process
                    connectOptions.AutomaticReconnect ? ct => link.Connect(peer, options.InviteTimeout, ct) : null
                ),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }


    string PeerName(string? name) => MultipeerNames.Truncate(options.DisplayName ?? name ?? "Hub client");


    bool Serves(string mine, MultipeerSighting seen)
    {
        if (!seen.Info!.TryGetValue(MultipeerProtocol.InfoHubs, out var hubs))
            return false;

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
