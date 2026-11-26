using System.Collections.Concurrent;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// A host seen over Nearby Connections
/// </summary>
/// <param name="EndpointName">The host's advertised name</param>
public sealed record NearbyHubEndpoint(string EndpointName) : HubEndpoint
{
    /// <summary>
    /// The native endpoint id
    /// </summary>
    internal string? EndpointId { get; init; }
}


/// <summary>
/// Finds universal hub hosts over Nearby Connections and connects to them - one channel per hub connection, inside the
/// one Nearby connection to each host. Added with <c>o.UseNearby(...)</c>.
/// </summary>
public sealed class NearbyClientTransport : IHubClientTransport
{
    readonly NearbyLink link;
    readonly NearbyClientOptions options;
    readonly ILoggerFactory? loggerFactory;


    internal NearbyClientTransport(NearbyLink link, NearbyClientOptions options, ILoggerFactory? loggerFactory)
    {
        this.link = link;
        this.options = options;
        this.loggerFactory = loggerFactory;
    }


    public string DisplayName => "Nearby";
    public bool IsSupported => this.link.IsSupported;


    // nothing to find where there is no Nearby Connections - not an error worth logging on every scan
    public IObservable<HubHostSighting> Discover(HubClientContext client) => !link.IsSupported ? Observable.Empty<HubHostSighting>() : Observable.Create<HubHostSighting>(observer =>
    {
        var tag = NearbyProtocol.HubTag(client.HubName);

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
                            observer.OnNext(new HubHostSighting(gone, null, null));
                    }
                    else
                    {
                        var host = new NearbyHubEndpoint(seen.Info.Name) { EndpointId = seen.EndpointId };
                        names[seen.EndpointId] = seen.Info.Name;
                        observer.OnNext(new HubHostSighting(seen.Info.Name, seen.Info.HostId, host));
                    }
                },
                observer.OnError
            );
    });


    public async Task Connect(HubClientContext client, HubHostInfo host, CancellationToken cancellationToken)
    {
        var connectOptions = client.ConnectOptions;
        var endpointId = host.Endpoint<NearbyHubEndpoint>()?.EndpointId ?? throw new BleHubException($"{host.Name} was not seen over Nearby");
        var endpointName = options.EndpointName ?? connectOptions.Name ?? "Hub client";
        var connection = await link.Connect(endpointId, endpointName, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.Connect(
                events => new PeerHubTransport(
                    this.DisplayName,
                    connection,
                    events,
                    client.HubName,
                    loggerFactory?.CreateLogger<PeerHubTransport>(),
                    connectOptions.AutomaticReconnect ? ct => link.Connect(endpointId, endpointName, options.ConnectTimeout, ct) : null
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
}
