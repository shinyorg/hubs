using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shiny.UniversalHubs;

public static class MultipeerExtensions
{
    /// <summary>
    /// Also serves the universal server's hubs over Apple's MultipeerConnectivity, advertised under
    /// <paramref name="serviceType"/>. Apple devices then reach the host with no shared network at all, at Wi-Fi speed.
    /// <c>Start()</c> includes it; on platforms without MultipeerConnectivity it is skipped.
    /// <code>
    /// services.AddUniversalServer(server => server
    ///     .Multipeer("unigame-mc")
    ///     .AddHub&lt;GameHub&gt;(CharacteristicUuid, "_unigame._tcp"));
    /// </code>
    /// The app's Info.plist needs <c>_unigame-mc._tcp</c> and <c>_unigame-mc._udp</c> in NSBonjourServices, and an
    /// NSLocalNetworkUsageDescription - without them discovery silently finds nothing.
    /// </summary>
    /// <param name="server">The universal server being set up</param>
    /// <param name="serviceType">
    /// 1-15 lowercase letters, digits and hyphens. One per host, like the BLE service UUID - every hub is listed in its
    /// advertisement. It must not be the name in any hub's mDNS service type (<c>unigame</c> for <c>_unigame._tcp</c>):
    /// Multipeer advertises over Bonjour too.
    /// </param>
    /// <param name="configure">The host's Multipeer display name</param>
    public static UniversalServerBuilder Multipeer(this UniversalServerBuilder server, string serviceType, Action<MultipeerHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        MultipeerProtocol.ValidateServiceType(serviceType);

        var options = new MultipeerHostOptions(serviceType);
        configure?.Invoke(options);

        if (server.IsSetUp<MultipeerHostTransport>())
            throw new InvalidOperationException("Multipeer can only be set up once per universal server");

        server.SetUpTransport<MultipeerHostTransport>((services, _) =>
        {
            services.AddMultipeerLink();
            services.AddSingleton(options);
            services.AddSingleton(sp => new MultipeerHostTransport(
                sp,
                sp.GetRequiredService<IMultipeerLink>(),
                options,
                sp.GetRequiredService<UniversalServerOptions>(),
                sp.GetService<ILogger<MultipeerHostTransport>>()
            ));
            services.AddSingleton<IHubTransport>(sp => sp.GetRequiredService<MultipeerHostTransport>());
        });
        return server;
    }


    /// <summary>
    /// Serves this hub over Multipeer too - set the transport up once on the server with server.Multipeer(...)
    /// </summary>
    public static UniversalHubBuilder UseMultipeer(this UniversalHubBuilder hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        hub.Use<MultipeerHostTransport>(new MultipeerHubSettings());
        return hub;
    }


    /// <summary>
    /// Also finds hosts over Apple's MultipeerConnectivity and connects through it - the host's
    /// <c>server.Multipeer(serviceType)</c>. <c>DiscoverAll</c> and <c>Connect</c> include it; it is tried after Wi-Fi and
    /// before BLE. Needs the same NSBonjourServices entries as the host.
    /// <code>
    /// services.AddUniversalHubClient&lt;IGameHub&gt;(CharacteristicUuid, "_unigame._tcp", o => o.Multipeer("unigame-mc"));
    /// </code>
    /// </summary>
    public static UniversalHubClientOptions UseMultipeer(this UniversalHubClientOptions options, string serviceType, Action<MultipeerClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        MultipeerProtocol.ValidateServiceType(serviceType);
        var mc = new MultipeerClientOptions(serviceType);
        configure?.Invoke(mc);

        return options.UseTransport(
            sp => new MultipeerClientTransport(
                sp.GetRequiredService<IMultipeerLink>(),
                mc,
                sp.GetService<ILoggerFactory>()
            ),
            services => services.AddMultipeerLink()
        );
    }


    static void AddMultipeerLink(this IServiceCollection services)
    {
#if IOS || MACCATALYST || MACOS
        services.TryAddSingleton<IMultipeerLink, AppleMultipeerLink>();
#else
        services.TryAddSingleton<IMultipeerLink, UnsupportedMultipeerLink>();
#endif
    }
}


public sealed class MultipeerHostOptions
{
    internal MultipeerHostOptions(string serviceType) => this.ServiceType = serviceType;

    /// <summary>
    /// The Multipeer service type the host advertises under
    /// </summary>
    public string ServiceType { get; }

    /// <summary>
    /// The host's Multipeer display name - the BLE local name unless set. At most 63 UTF-8 bytes; longer is cut.
    /// </summary>
    public string? DisplayName { get; set; }
}


public sealed class MultipeerClientOptions
{
    internal MultipeerClientOptions(string serviceType) => this.ServiceType = serviceType;

    /// <summary>
    /// The Multipeer service type to browse - the host's
    /// </summary>
    public string ServiceType { get; }

    /// <summary>
    /// How long the host gets to accept the invitation and connect before the next transport is tried
    /// </summary>
    public TimeSpan InviteTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// This device's Multipeer display name, which the host sees as <see cref="MultipeerPeer.DisplayName"/>. The hub client
    /// name travels in the handshake as usual - this is only the radio's. "Hub client" unless set.
    /// </summary>
    public string? DisplayName { get; set; }
}


internal static class MultipeerNames
{
    /// <summary>
    /// MCPeerID display names are at most 63 UTF-8 bytes - cut on a character boundary
    /// </summary>
    public static string Truncate(string name)
    {
        if (String.IsNullOrWhiteSpace(name))
            return "Hub peer";

        var max = 63;
        if (Encoding.UTF8.GetByteCount(name) <= max)
            return name;

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > max)
                break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}


/// <summary>
/// What a hub was told by UseMultipeer - nothing beyond that it is served over Multipeer
/// </summary>
internal sealed record MultipeerHubSettings;


/// <summary>
/// A client's MultipeerConnectivity peer - <c>connection.MultipeerPeer()</c> in a hub
/// </summary>
/// <param name="DisplayName">The name the client's device connected under - the hub's own client name is <see cref="HubConnectedClient.Name"/></param>
public sealed record MultipeerPeer(string DisplayName);


public static class MultipeerPeerConnectionExtensions
{
    /// <summary>
    /// The MultipeerConnectivity peer the client is connected through - null on the other transports
    /// </summary>
    public static MultipeerPeer? MultipeerPeer(this HubConnection connection) => connection.Get<MultipeerPeer>();
}
