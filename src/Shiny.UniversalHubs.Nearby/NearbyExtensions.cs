using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

public static class NearbyExtensions
{
    /// <summary>
    /// Also serves the universal server's hubs over Google's Nearby Connections, advertised under
    /// <paramref name="serviceId"/>. Android, iOS and macOS devices then reach the host with no shared network at all -
    /// over Bluetooth, Wi-Fi Direct, a hotspot or Wi-Fi Aware, whichever Nearby picks. <c>Start()</c>
    /// includes it; where Nearby Connections doesn't exist it is skipped.
    /// <code>
    /// services.AddUniversalServer(server => server
    ///     .Nearby("com.mycompany.mygame")
    ///     .AddHub&lt;GameHub&gt;(CharacteristicUuid, "_mygame._tcp"));
    /// </code>
    /// iOS and macOS apps need <see cref="NearbyProtocol.BonjourServices"/> in NSBonjourServices, an
    /// NSLocalNetworkUsageDescription and an NSBluetoothAlwaysUsageDescription. Android apps need Google Play services.
    /// </summary>
    /// <param name="server">The universal server being set up</param>
    /// <param name="serviceId">
    /// Every app that should find each other uses the same one, conventionally a reverse-DNS name. One per app: the
    /// hub clients use it too (Nearby allows one advertisement and one discovery per app).
    /// </param>
    /// <param name="configure">The host's advertised name and the Nearby strategy</param>
    public static UniversalServerBuilder Nearby(this UniversalServerBuilder server, string serviceId, Action<NearbyHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        NearbyProtocol.ValidateServiceId(serviceId);

        var options = new NearbyHostOptions(serviceId);
        configure?.Invoke(options);

        if (server.IsSetUp<NearbyHostTransport>())
            throw new InvalidOperationException("Nearby can only be set up once per universal server");

        server.SetUpTransport<NearbyHostTransport>((services, _) =>
        {
            services.AddNearbyLink();
            services.AddSingleton(options);
            services.AddSingleton(sp => new NearbyHostTransport(
                sp,
                sp.GetRequiredService<NearbyLink>(),
                options,
                sp.GetRequiredService<UniversalServerOptions>(),
                sp.GetService<ILogger<NearbyHostTransport>>()
            ));
            services.AddSingleton<IHubTransport>(sp => sp.GetRequiredService<NearbyHostTransport>());
        });
        return server;
    }


    /// <summary>
    /// Serves this hub over Nearby too - set the transport up once on the server with server.Nearby(...)
    /// </summary>
    public static UniversalHubBuilder UseNearby(this UniversalHubBuilder hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        hub.Use<NearbyHostTransport>(new NearbyHubSettings());
        return hub;
    }


    /// <summary>
    /// Also finds hosts over Google's Nearby Connections and connects through them - the host's
    /// <c>server.Nearby(serviceId)</c>. <c>DiscoverAll</c> and <c>Connect</c> include it; it is tried after Wi-Fi and
    /// Multipeer and before BLE. Needs the same Info.plist entries as the host.
    /// <code>
    /// services.AddUniversalHubClient&lt;IGameHub&gt;(CharacteristicUuid, "_mygame._tcp", o => o.Nearby("com.mycompany.mygame"));
    /// </code>
    /// </summary>
    public static UniversalHubClientOptions UseNearby(this UniversalHubClientOptions options, string serviceId, Action<NearbyClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        NearbyProtocol.ValidateServiceId(serviceId);

        var nearby = new NearbyClientOptions(serviceId);
        configure?.Invoke(nearby);

        options.ServiceActions.Add(services => services.AddNearbyLink());
        options.AddTransport(HubTransports.Nearby, "Nearby", sp => new NearbyClientTransport(
            sp.GetRequiredService<NearbyLink>(),
            nearby,
            sp.GetService<ILoggerFactory>()
        ));
        return options;
    }


    static void AddNearbyLink(this IServiceCollection services)
    {
#if ANDROID
        services.TryAddSingleton<INearbyRadio, AndroidNearbyRadio>();
#elif IOS || MACOS
        services.TryAddSingleton<INearbyRadio, AppleNearbyRadio>();
#else
        services.TryAddSingleton<INearbyRadio, UnsupportedNearbyRadio>();
#endif
        services.TryAddSingleton<NearbyLink>();
    }
}


public sealed class NearbyHostOptions
{
    internal NearbyHostOptions(string serviceId) => this.ServiceId = serviceId;

    /// <summary>
    /// The Nearby service id the host advertises under
    /// </summary>
    public string ServiceId { get; }

    /// <summary>
    /// The name the host advertises - the BLE local name unless set. Cut to fit the endpoint info.
    /// </summary>
    public string? EndpointName { get; set; }

    /// <summary>
    /// Cluster unless set - clients must use the same
    /// </summary>
    public NearbyStrategy Strategy { get; set; } = NearbyStrategy.Cluster;
}


public sealed class NearbyClientOptions
{
    internal NearbyClientOptions(string serviceId) => this.ServiceId = serviceId;

    /// <summary>
    /// The Nearby service id to discover - the host's
    /// </summary>
    public string ServiceId { get; }

    /// <summary>
    /// How long the host gets to accept the connection before the next transport is tried
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The name this device asks to connect under, which the host sees as <see cref="NearbyPeer.EndpointName"/> - the hub
    /// client name unless set
    /// </summary>
    public string? EndpointName { get; set; }

    /// <summary>
    /// Cluster unless set - the host's
    /// </summary>
    public NearbyStrategy Strategy { get; set; } = NearbyStrategy.Cluster;
}


/// <summary>
/// What a hub was told by UseNearby - nothing beyond that it is served over Nearby
/// </summary>
internal sealed record NearbyHubSettings;
