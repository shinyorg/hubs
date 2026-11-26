using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.Switchboard;

namespace Shiny.UniversalHubs;

public static class HttpHubExtensions
{
    /// <summary>
    /// Serves this hub over HTTP: a Switchboard at <c>{BasePath}/{hub name}</c>, advertised over mDNS
    /// </summary>
    /// <param name="mdnsServiceType">
    /// The mDNS service type the hub is advertised under, eg. <c>_unigame._tcp</c> - its clients browse for the same one,
    /// and iOS hosts list it in NSBonjourServices. Null uses <see cref="HttpTransportOptions.ServiceType"/>. Hubs that
    /// share a type share one advertisement.
    /// </param>
    /// <param name="allowTunnel">
    /// Let clients reach this hub through a tunnel - the public internet. Off by default: through a tunnel, a hub that
    /// doesn't allow it answers 404. Set up the tunnel itself on the HttpServer (eg. AddQuickTunnel from
    /// Shiny.Net.HttpServer.Ssh), and put authentication in front of anything you share this way.
    /// </param>
    public static UniversalHubBuilder UseHttp(this UniversalHubBuilder hub, string? mdnsServiceType = null, bool allowTunnel = false)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (mdnsServiceType != null)
            UniversalHubProtocol.ValidateServiceType(mdnsServiceType);

        hub.Use<HttpHostTransport>(new HttpHubSettings(mdnsServiceType, allowTunnel));
        hub.Server.SetUpTransport<HttpHostTransport>(AddHttpTransport);
        return hub;
    }


    /// <summary>
    /// HTTP hosting settings: the base path, mDNS, the server's lifetime, the file gate
    /// </summary>
    public static UniversalServerBuilder Http(this UniversalServerBuilder server, Action<HttpTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);
        server.Configure<HttpHostTransport, HttpTransportOptions>(configure);
        server.SetUpTransport<HttpHostTransport>(AddHttpTransport);
        return server;
    }


    /// <summary>
    /// The HttpServer itself: address, port, TLS, middleware, your own routes. It binds every interface on a free port
    /// unless the server was already registered with AddShinyHttpServer.
    /// </summary>
    public static UniversalServerBuilder HttpServer(this UniversalServerBuilder server, Action<ShinyHttpServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);
        server.Configure<HttpHostTransport, ShinyHttpServerBuilder>(configure);
        server.SetUpTransport<HttpHostTransport>(AddHttpTransport);
        return server;
    }


    static void AddHttpTransport(IServiceCollection services, UniversalServerBuilder server)
    {
        var options = new HttpTransportOptions();
        foreach (var c in server.GetConfigurations<HttpHostTransport, HttpTransportOptions>())
            c(options);

        var serverExisted = services.Any(x => x.ServiceType == typeof(HttpServerOptions));
        services.AddShinyHttpServer(http =>
        {
            if (!serverExisted)
            {
                // reachable from other devices, on whatever port is free - mDNS tells clients which
                http.Options.Address = IPAddress.Any;
                http.Options.Port = 0;
            }
            foreach (var c in server.GetConfigurations<HttpHostTransport, ShinyHttpServerBuilder>())
                c(http);

            http.AddSwitchboard();
            http.Services.AddMdns();
            http.Services.AddSingleton(options);
            http.Services.TryAddSingleton<FileTokens>();
            http.Services.TryAddSingleton<MediaTokens>();
            http.Services.TryAddSingleton(sp => new HttpHostTransport(
                sp,
                sp.GetRequiredService<HttpTransportOptions>(),
                sp.GetRequiredService<UniversalServerOptions>(),
                sp.GetRequiredService<FileTokens>(),
                sp.GetRequiredService<MediaTokens>(),
                sp.GetRequiredService<UniversalHubHost>(),
                sp.GetService<ILogger<HttpHostTransport>>()
            ));
            http.Services.AddSingleton<IHubTransport>(sp => sp.GetRequiredService<HttpHostTransport>());
            http.Configure(app =>
            {
                // before routing: a hub that isn't shared over a tunnel doesn't exist as far as the internet can tell
                app.Use(async (ctx, next) =>
                {
                    if (ctx.Connection.IsTunneled && !TunnelGate.IsAllowed(
                        ctx.Request.Path,
                        options.BasePath,
                        app.Services!.GetRequiredService<UniversalHubHost>().Hubs,
                        app.Services!.GetService<DynamicHubRegistry>() != null
                    ))
                    {
                        ctx.Response.StatusCode = 404;
                        return;
                    }
                    await next(ctx);
                });
                FileRoutes.Map(app, options);
                MediaRoutes.Map(app, options);
            });

            var mapper = new HttpHubMapper(http, options);
            foreach (var hub in server.Hubs.Where(x => x.Transports.Contains(typeof(HttpHostTransport))))
                hub.Accept(mapper);

            if (server.DynamicHubs != null)
            {
                DynamicHubBoard.Register();
                http.Configure(app => app.MapSwitchboard<DynamicHubBoard>(
                    UniversalHubProtocol.HubPath(options.BasePath, $"{{{DynamicHubBoard.RouteParameter}}}")
                ));
            }
        }, autoStart: false); // IUniversalHubHost starts and stops it
    }


    /// <summary>
    /// Gives each compiled hub its Switchboard
    /// </summary>
    sealed class HttpHubMapper(ShinyHttpServerBuilder http, HttpTransportOptions options) : IHubTypeVisitor
    {
        public void Visit<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>(string key) where THub : class
        {
            HubBoardRegistration.Register<THub>();
            http.Services.TryAddSingleton<HttpHub<THub>>();
            http.Services.AddSingleton<HttpHub>(sp => sp.GetRequiredService<HttpHub<THub>>());
            http.Configure(app => app.MapSwitchboard<HubBoard<THub>>(UniversalHubProtocol.HubPath(options.BasePath, key)));
        }
    }
}


/// <summary>
/// Serves hubs over HTTP: each on its Switchboard, advertised over mDNS, with file and media routes beside them. Added with
/// <c>hub.UseHttp(...)</c>.
/// </summary>
public sealed class HttpHostTransport : IHubTransport, IListeningTransport
{
    readonly IServiceProvider services;
    readonly HttpTransportOptions options;
    readonly UniversalServerOptions serverOptions;
    readonly FileTokens fileTokens;
    readonly MediaTokens mediaTokens;
    readonly ILogger? logger;
    readonly object gate = new();
    readonly HashSet<IHubBinding> running = new();
    readonly List<IHttpServerAdvertiser> advertisers = new();
    bool startedServer;


    internal HttpHostTransport(
        IServiceProvider services,
        HttpTransportOptions options,
        UniversalServerOptions serverOptions,
        FileTokens fileTokens,
        MediaTokens mediaTokens,
        UniversalHubHost host,
        ILogger<HttpHostTransport>? logger = null
    )
    {
        this.services = services;
        this.options = options;
        this.serverOptions = serverOptions;
        this.fileTokens = fileTokens;
        this.mediaTokens = mediaTokens;
        this.logger = logger;

        // a client that left takes its tokens with it
        host.ClientGone += id =>
        {
            fileTokens.Remove(id);
            mediaTokens.Remove(id);
        };
    }


    public string DisplayName => "HTTP";
    public bool IsSupported => true;

    public bool AnyRunning
    {
        get
        {
            lock (this.gate)
                return this.running.Count > 0;
        }
    }

    public bool IsRunning(IHubBinding hub)
    {
        lock (this.gate)
            return this.running.Contains(hub);
    }

    public IReadOnlyList<string> ListenUrls => this.AnyRunning ? this.Server.ListenUrls : [];

    HttpServer Server => this.services.GetRequiredService<HttpServer>();

    IReadOnlyList<IHubBinding> Running
    {
        get
        {
            lock (this.gate)
                return this.running.ToList();
        }
    }


    /// <summary>
    /// The Switchboard side of a hub - compiled, or dynamic
    /// </summary>
    HttpHub? Find(IHubBinding hub)
        => this.services.GetServices<HttpHub>().FirstOrDefault(x => ReferenceEquals(x.Binding, hub))
           ?? this.services.GetService<DynamicHubRegistry>()?.FindHttp(hub);


    public async Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        var server = this.Server;
        if (this.options.ManageHttpServer && !server.IsRunning)
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            this.startedServer = true;
        }
        lock (this.gate)
        {
            foreach (var hub in hubs)
                this.running.Add(hub);
        }

        await this.Readvertise(cancellationToken).ConfigureAwait(false);
        this.logger?.LogInformation("HTTP hosting {Hubs} on {Urls}", String.Join(", ", hubs.Select(x => x.Name)), String.Join(", ", server.ListenUrls));
    }


    public async Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect)
    {
        // refuse new handshakes first, then hang up on everyone - the hubs see each line leave
        lock (this.gate)
        {
            foreach (var hub in hubs)
                this.running.Remove(hub);
        }

        await this.Readvertise(CancellationToken.None).ConfigureAwait(false);

        foreach (var hub in hubs)
        {
            try
            {
                if (this.Find(hub) is { } http)
                    await http.HangUpAll(disconnect).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Could not hang up {Hub} clients", hub.Name);
            }
        }

        if (!this.AnyRunning)
        {
            this.fileTokens.Clear();
            this.mediaTokens.Clear();
            if (this.startedServer)
            {
                this.startedServer = false;
                await this.Server.StopAsync().ConfigureAwait(false);
            }
        }
    }


    public Task HostRenamed(CancellationToken cancellationToken) => this.Readvertise(cancellationToken);


    /// <summary>
    /// Replaces the mDNS advertisements with ones for the hubs running over HTTP now
    /// </summary>
    async Task Readvertise(CancellationToken cancellationToken)
    {
        foreach (var advertiser in this.advertisers)
        {
            try
            {
                await advertiser.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "mDNS goodbye failed");
            }
        }
        this.advertisers.Clear();

        var running = this.Running;
        if (!this.options.Advertise || running.Count == 0)
            return;

        try
        {
            await this.Advertise(this.Server, running, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // hosting still works for anyone who has the address - only discovery is lost
            this.logger?.LogWarning(ex, "mDNS advertising failed");
        }
    }


    /// <summary>
    /// One advertisement per mDNS service type, listing the hubs advertised under it
    /// </summary>
    async Task Advertise(HttpServer server, IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        var mdns = this.services.GetService<IMdnsManager>() ?? throw new InvalidOperationException("No IMdnsManager is registered");
        var name = String.IsNullOrWhiteSpace(this.serverOptions.LocalName) ? "Hub host" : this.serverOptions.LocalName;

        foreach (var (serviceType, keys) in GetAdvertisements(hubs, this.options.ServiceType))
        {
            var advertiser = await server.AdvertiseAsync(mdns, o =>
            {
                o.ServiceType = serviceType;
                o.InstanceName = name;
                o.Path = this.options.BasePath;
                o.TxtRecords[UniversalHubProtocol.TxtProtocol] = UniversalHubProtocol.Version.ToString();
                o.TxtRecords[UniversalHubProtocol.TxtHostId] = this.serverOptions.HostId;
                o.TxtRecords[UniversalHubProtocol.TxtName] = name;
                o.TxtRecords[UniversalHubProtocol.TxtHubs] = keys;
            }, cancellationToken).ConfigureAwait(false);
            this.advertisers.Add(advertiser);
        }
    }


    /// <summary>
    /// Groups hubs by the mDNS service type they are advertised under: (service type, comma separated hub keys)
    /// </summary>
    internal static IReadOnlyList<(string ServiceType, string Hubs)> GetAdvertisements(IEnumerable<IHubBinding> hubs, string defaultServiceType)
    {
        var list = new List<(string, string)>();
        foreach (var group in hubs.GroupBy(x => x.GetSettings<HttpHubSettings>()?.ServiceType ?? defaultServiceType, StringComparer.OrdinalIgnoreCase))
        {
            var keys = String.Join(",", group.Select(x => x.Key.ToLowerInvariant()));

            // a TXT string holds 255 bytes including "hubs="
            if (keys.Length + UniversalHubProtocol.TxtHubs.Length + 1 > 255)
                throw new InvalidOperationException($"Too many hubs to advertise as {group.Key} over mDNS - their names don't fit in one TXT record");

            list.Add((group.Key, keys));
        }
        return list;
    }
}
