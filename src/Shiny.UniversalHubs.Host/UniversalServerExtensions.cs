using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;

namespace Shiny.UniversalHubs;

public static class UniversalServerExtensions
{
    /// <summary>
    /// Registers a universal server: its hubs are served over BLE (GATT) and HTTP (Switchboard, advertised over mDNS) at
    /// once, and each hub shares its clients, groups, MaxClients and ValidateClient across both. Start and stop it with
    /// <see cref="IUniversalHubHost"/>. The BLE side is blehubs' AddBleHubServer, which adds the BLE hosting stack on
    /// Android, iOS and Mac Catalyst; on plain .NET, register an IBleHostingManager yourself.
    /// <code>
    /// services.AddUniversalServer(server => server
    ///     .Ble(o => o.EnableFileTransfers(filesDir))
    ///     .Http(o => o.AuthorizeFile = req => req.FileName.StartsWith("avatar-"))
    ///     .AddHub&lt;GameHub&gt;(CharacteristicUuid, "_unigame._tcp", o => o.MaxClients = 6));
    /// </code>
    /// </summary>
    /// <param name="configure">BLE and HTTP setup, and the hubs to serve</param>
    public static IServiceCollection AddUniversalServer(
        this IServiceCollection services,
        Action<UniversalServerBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(x => x.ServiceType == typeof(UniversalServerMarker)))
            throw new InvalidOperationException("AddUniversalServer can only be called once - add every hub inside it");
        services.AddSingleton<UniversalServerMarker>();

        var server = new UniversalServerBuilder();
        configure(server);
        if (server.Hubs.Count == 0)
            throw new InvalidOperationException("A universal server needs at least one hub - call AddHub");
        if (String.IsNullOrWhiteSpace(server.Endpoint))
            throw new InvalidOperationException("A universal server needs an endpoint");

        services.AddBleHubServer(ble =>
        {
            if (server.BleServiceUuid != null)
                ble.ServiceUuid(server.BleServiceUuid);
            foreach (var c in server.BleActions)
                ble.Host(c);
            foreach (var c in server.ProtocolActions)
                ble.Protocol(c);
            foreach (var hub in server.Hubs)
                hub.AddBle(ble);
        });

        var serverExisted = services.Any(x => x.ServiceType == typeof(HttpServerOptions));
        services.AddShinyHttpServer(http =>
        {
            if (!serverExisted)
            {
                // reachable from other devices, on whatever port is free - mDNS tells clients which
                http.Options.Address = IPAddress.Any;
                http.Options.Port = 0;
            }
            foreach (var c in server.HttpServerActions)
                c(http);

            var switchboard = http.AddUniversalHubs(o =>
            {
                o.BasePath = server.Endpoint;
                foreach (var c in server.HttpActions)
                    c(o);
            });
            foreach (var hub in server.Hubs)
                hub.AddHttp(switchboard);
        }, autoStart: false); // IUniversalHubHost starts and stops it

        return services;
    }
}


/// <summary>
/// Sets up a universal server: BLE hosting, HTTP hosting, the HttpServer, and the hubs it serves
/// </summary>
public sealed class UniversalServerBuilder
{
    internal UniversalServerBuilder() { }

    internal string? BleServiceUuid { get; private set; }
    internal List<Action<BleHubHostOptions>> BleActions { get; } = new();
    internal List<Action<BleHubProtocolOptions>> ProtocolActions { get; } = new();
    internal List<Action<UniversalHubHostOptions>> HttpActions { get; } = new();
    internal List<Action<ShinyHttpServerBuilder>> HttpServerActions { get; } = new();
    internal List<UniversalHubRegistration> Hubs { get; } = new();

    /// <summary>
    /// The HTTP base path. Each hub is served at <c>{Endpoint}/{characteristicUuid}</c> and files at <c>{Endpoint}/files</c>.
    /// It is advertised over mDNS, so clients need no setting for it.
    /// </summary>
    public string Endpoint { get; set; } = UniversalHubProtocol.DefaultBasePath;

    /// <summary>
    /// The one BLE service every hub is a characteristic in - blehubs' default unless set. Clients must use the same one
    /// (UniversalHubClientOptions.ServiceUuid in AddUniversalHubClient).
    /// </summary>
    public UniversalServerBuilder ServiceUuid(string serviceUuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceUuid);
        this.BleServiceUuid = serviceUuid;
        return this;
    }

    /// <summary>
    /// BLE hosting: LocalName (also the mDNS instance name), file transfers (used by HTTP too), the client sweep
    /// </summary>
    public UniversalServerBuilder Ble(Action<BleHubHostOptions> configure) => Add(this, this.BleActions, configure);

    /// <summary>
    /// blehubs protocol limits (payload size, timeouts) - app-wide
    /// </summary>
    public UniversalServerBuilder Protocol(Action<BleHubProtocolOptions> configure) => Add(this, this.ProtocolActions, configure);

    /// <summary>
    /// HTTP hosting: HostId, Advertise, ManageHttpServer, AuthorizeFile. Each hub's mDNS service type comes from AddHub
    /// and the base path from <see cref="Endpoint"/>.
    /// </summary>
    public UniversalServerBuilder Http(Action<UniversalHubHostOptions> configure) => Add(this, this.HttpActions, configure);

    /// <summary>
    /// The HttpServer itself: address, port, TLS, middleware, your own routes. It binds every interface on a free port
    /// unless the server was already registered with AddShinyHttpServer.
    /// </summary>
    public UniversalServerBuilder HttpServer(Action<ShinyHttpServerBuilder> configure) => Add(this, this.HttpServerActions, configure);

    /// <summary>
    /// Serves <typeparamref name="THub"/> - a <see cref="UniversalHub{TContract}"/> - over BLE and HTTP
    /// </summary>
    /// <param name="characteristicUuid">The BLE characteristic UUID - one per hub. Also names the hub over HTTP.</param>
    /// <param name="mdnsServiceType">
    /// The mDNS service type this hub is advertised under, eg. <c>_unigame._tcp</c>. Its clients browse for the same one
    /// (AddUniversalHubClient), and iOS hosts list it in NSBonjourServices. Hubs that share a type share one advertisement.
    /// </param>
    /// <param name="configure">
    /// This hub's rules: MaxClients (counted across both transports), ValidateClient, and AllowTunnel
    /// </param>
    public UniversalServerBuilder AddHub<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>(
        string characteristicUuid,
        string mdnsServiceType,
        Action<UniversalHubOptions>? configure = null
    ) where THub : class
    {
        UniversalHubProtocol.ValidateServiceType(mdnsServiceType);
        if (this.Hubs.Any(x => x.HubType == typeof(THub)))
            throw new InvalidOperationException($"{typeof(THub).Name} is already added");

        var options = new UniversalHubOptions();
        configure?.Invoke(options);

        this.Hubs.Add(new UniversalHubRegistration(
            typeof(THub),
            ble => ble.AddHub<THub>(characteristicUuid, o =>
            {
                o.MaxClients = options.MaxClients;
                o.ValidateClient = options.ValidateClient;
            }),
            switchboard => switchboard.AddHub<THub>(mdnsServiceType, options.AllowTunnel)
        ));
        return this;
    }


    static UniversalServerBuilder Add<T>(UniversalServerBuilder builder, List<Action<T>> list, Action<T> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        list.Add(configure);
        return builder;
    }
}


/// <summary>
/// One universal hub's rules
/// </summary>
public sealed class UniversalHubOptions : BleHubOptions
{
    /// <summary>
    /// Let clients reach this hub through a tunnel - the public internet. Off by default: through a tunnel, a hub that
    /// doesn't allow it answers 404. Set up the tunnel itself on the HttpServer (eg. AddQuickTunnel from
    /// Shiny.Net.HttpServer.Ssh), and put authentication in front of anything you share this way.
    /// </summary>
    public bool AllowTunnel { get; set; }
}


internal sealed record UniversalHubRegistration(
    Type HubType,
    Action<BleHubServerBuilder> AddBle,
    Action<UniversalHubHostBuilder> AddHttp
);

internal sealed class UniversalServerMarker;
