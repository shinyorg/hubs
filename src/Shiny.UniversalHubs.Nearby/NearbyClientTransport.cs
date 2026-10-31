using System.Collections.Concurrent;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// Finds universal hub hosts over Nearby Connections and connects to them - one channel per hub connection, inside the
/// one Nearby connection to each host
/// </summary>
internal sealed class NearbyClientTransport(NearbyLink link, NearbyClientOptions options, ILoggerFactory? loggerFactory) : IUniversalHubClientTransport
{
    public HubTransports Transport => HubTransports.Nearby;


    // nothing to find where there is no Nearby Connections - not an error worth logging on every scan
    public IObservable<HubHostSighting> Discover(BleHubClient client) => !link.IsSupported ? Observable.Empty<HubHostSighting>() : Observable.Create<HubHostSighting>(observer =>
    {
        var tag = NearbyProtocol.HubTag(client.CharacteristicUuid);

        // a lost endpoint only comes with its id - remember what it was called when it was found
        var names = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        return link
            .Discover(options.ServiceId, options.Strategy)
            .Subscribe(
                seen =>
                {
                    if (seen.Info == null || !seen.Info.Hubs.Contains(tag))
                    {
                        // gone, or no longer running this hub
                        if (names.TryRemove(seen.EndpointId, out var gone))
                            observer.OnNext(new HubHostSighting(gone, null, x => x with { Nearby = null }));
                    }
                    else
                    {
                        var host = new NearbyHost(seen.Info.Name) { Peer = seen.EndpointId };
                        names[seen.EndpointId] = seen.Info.Name;
                        observer.OnNext(new HubHostSighting(seen.Info.Name, seen.Info.HostId, x => x with { Nearby = host }));
                    }
                },
                observer.OnError
            );
    });


    public async Task Connect(BleHubClient client, HubHostInfo host, BleHubConnectOptions connectOptions, CancellationToken cancellationToken)
    {
        var endpointId = host.Nearby?.Peer as string ?? throw new BleHubException($"{host.Name} was not seen over Nearby");
        var connection = await link.Connect(
            endpointId,
            options.EndpointName ?? connectOptions.Name ?? "Hub client",
            options.ConnectTimeout,
            cancellationToken
        ).ConfigureAwait(false);
        try
        {
            await client.ConnectExternal(
                events => new PeerHubTransport(HubTransports.Nearby, connection, events, client.CharacteristicUuid, loggerFactory?.CreateLogger<PeerHubTransport>()),
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
}
