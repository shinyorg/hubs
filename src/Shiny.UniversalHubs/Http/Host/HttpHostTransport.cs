using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;
using Shiny.Net.HttpServer.WebSockets;

namespace Shiny.UniversalHubs;

public static class HttpConnectionExtensions
{
    /// <summary>
    /// The HTTP request the client's WebSocket was opened with - its User, headers and connection - null on the other
    /// transports
    /// </summary>
    public static HttpContext? HttpContext(this HubConnection connection) => connection.Get<HttpContext>();
}


public static class HttpHostExtensions
{
    /// <summary>
    /// How this dynamic hub is served over HTTP - by default under HttpTransportOptions.ServiceType, not through tunnels
    /// </summary>
    /// <param name="mdnsServiceType">One of HttpTransportOptions.DynamicServiceTypes</param>
    /// <param name="allowTunnel">Let clients reach it through a tunnel (the public internet)</param>
    public static DynamicHubDefinition UseHttp(this DynamicHubDefinition definition, string? mdnsServiceType = null, bool allowTunnel = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (mdnsServiceType != null)
            UniversalHubProtocol.ValidateServiceType(mdnsServiceType);

        return definition.With(new HttpHubSettings(mdnsServiceType, allowTunnel));
    }


    /// <summary>
    /// Serves this hub over HTTP: a WebSocket at <c>{BasePath}/{hub name}</c> carrying the hub session (calls, pushes,
    /// files and media channels), advertised over mDNS
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
    /// HTTP hosting settings: the base path, mDNS, the server's lifetime
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

        foreach (var type in options.DynamicServiceTypes)
            UniversalHubProtocol.ValidateServiceType(type);

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

            http.Services.AddMdns();
            http.Services.AddSingleton(options);
            http.Services.TryAddSingleton(sp => new HttpHostTransport(
                sp,
                sp.GetRequiredService<HttpTransportOptions>(),
                sp.GetRequiredService<UniversalServerOptions>(),
                sp.GetRequiredService<HubProtocolOptions>(),
                sp.GetService<ILogger<HttpHostTransport>>()
            ));
            http.Services.AddSingleton<IHubTransport>(sp => sp.GetRequiredService<HttpHostTransport>());
            http.Configure(app => app.MapRoute(
                HttpMethods.Get,
                UniversalHubProtocol.HubPath(options.BasePath, "{hub}"),
                ctx => ctx.RequestServices.GetRequiredService<HttpHostTransport>().Accept(ctx),
                new DisableRequestTimeoutAttribute()
            ));
        }, autoStart: false); // IUniversalHubHost starts and stops it
    }
}


/// <summary>
/// Serves hubs over HTTP: a WebSocket per hub connection at <c>{BasePath}/{hub name}</c>, carrying the same hub session
/// the peer-to-peer transports do (Multipeer, Nearby), and an mDNS advertisement per service type. Added with
/// <c>hub.UseHttp(...)</c>.
/// </summary>
public sealed class HttpHostTransport : IHubTransport, IDynamicHubTransport
{
    readonly IServiceProvider services;
    readonly HttpTransportOptions options;
    readonly UniversalServerOptions serverOptions;
    readonly HubProtocolOptions protocolOptions;
    readonly ILogger? logger;
    readonly object gate = new();
    readonly HashSet<IHubBinding> running = new();
    readonly List<IHttpServerAdvertiser> advertisers = new();
    readonly HashSet<PeerHostSession> sessions = new();
    bool startedServer;


    internal HttpHostTransport(
        IServiceProvider services,
        HttpTransportOptions options,
        UniversalServerOptions serverOptions,
        HubProtocolOptions protocolOptions,
        ILogger<HttpHostTransport>? logger = null
    )
    {
        this.services = services;
        this.options = options;
        this.serverOptions = serverOptions;
        this.protocolOptions = protocolOptions;
        this.logger = logger;
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
    internal UniversalHubHost Host => this.services.GetRequiredService<UniversalHubHost>();
    internal UniversalServerOptions ServerOptions => this.serverOptions;
    internal ILogger? Logger => this.logger;

    IReadOnlyList<IHubBinding> Running
    {
        get
        {
            lock (this.gate)
                return this.running.ToList();
        }
    }


    internal IHubBinding? FindRunning(string? key)
    {
        lock (this.gate)
            return this.running.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }


    internal IReadOnlyList<PeerHostSession> Sessions
    {
        get
        {
            lock (this.sessions)
                return this.sessions.ToList();
        }
    }

    internal void Remove(PeerHostSession session)
    {
        lock (this.sessions)
            this.sessions.Remove(session);
    }


    /// <summary>
    /// A WebSocket for <c>{BasePath}/{hub}</c>. Through a tunnel, a hub that doesn't allow tunnels (or isn't served over
    /// HTTP) doesn't exist as far as the caller can tell; otherwise the session's handshake says whether the hub is running.
    /// </summary>
    internal async ValueTask Accept(HttpContext ctx)
    {
        var key = ctx.Request.RouteValues.TryGetValue("hub", out var value) ? value?.ToString() : null;
        var hub = this.Host.FindByKey(key);
        if (hub != null && !hub.UsesTransport(this))
            hub = null;

        if (hub == null || (ctx.Connection.IsTunneled && hub.GetSettings<HttpHubSettings>()?.AllowTunnel != true))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!ctx.Request.IsWebSocketRequest())
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteTextAsync("A WebSocket upgrade is required");
            return;
        }

        var accept = new WebSocketAcceptOptions
        {
            MaxMessageLength = this.MaxMessageLength,
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };
        await using var socket = await ctx.AcceptWebSocketAsync(accept);
        var remote = ctx.Connection.ConnectionId.ToString();
        var connection = new WebSocketPeerConnection(remote, new ServerFrames(socket), this.logger);
        var session = new PeerHostSession(new SessionTransport(this, hub, ctx), connection);
        lock (this.sessions)
            this.sessions.Add(session);

        this.logger?.LogDebug("HTTP session {Id} from {Remote} for {Hub}", session.ConnectionId, remote, hub.Name);
        connection.Start(session);

        // the WebSocket lives as long as this request does
        try
        {
            await connection.Completion.WaitAsync(ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
        connection.Dispose();
    }


    /// <summary>
    /// The largest WebSocket message either side sends: a full payload or media packet, with the peer-link framing
    /// </summary>
    int MaxMessageLength => Math.Max(
        Math.Max(this.protocolOptions.MaxPayloadSize, this.serverOptions.MediaChannels?.MaxPacketSize ?? 0),
        WebSocketPeerConnection.FileChunkSize
    ) + 64 * 1024;


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
        // refuse new connections first, then tell everyone connected why they're going
        lock (this.gate)
        {
            foreach (var hub in hubs)
                this.running.Remove(hub);
        }

        await this.Readvertise(CancellationToken.None).ConfigureAwait(false);

        var stopping = this.Sessions.Where(x => x.Binding != null && hubs.Contains(x.Binding)).ToList();
        await Task.WhenAll(stopping.Select(x => x.Binding!.Endpoint.Disconnect(x.ConnectionId, disconnect))).ConfigureAwait(false);

        // sessions still waiting to hand-shake with a hub that stopped have nowhere to go
        foreach (var session in this.Sessions.Where(x => x.Binding == null && x.PeerTransport is SessionTransport t && hubs.Contains(t.Requested)))
            session.Close();

        if (!this.AnyRunning && this.startedServer)
        {
            this.startedServer = false;
            await this.Server.StopAsync().ConfigureAwait(false);
        }
    }


    public Task HostRenamed(CancellationToken cancellationToken) => this.Readvertise(cancellationToken);


    // ---- dynamic hubs ----

    object? IDynamicHubTransport.CreateSettings(DynamicHubDefinition definition)
    {
        var settings = definition.GetSettings<HttpHubSettings>() ?? new HttpHubSettings(null, false);
        var type = settings.ServiceType ?? this.options.ServiceType;
        IList<string> allowed = this.options.DynamicServiceTypes.Count == 0 ? [this.options.ServiceType] : this.options.DynamicServiceTypes;
        if (!allowed.Contains(type, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{type} isn't one of this host's dynamic hub service types ({String.Join(", ", allowed)})");

        return settings;
    }


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
    /// Groups hubs by the mDNS service type they are advertised under: (service type, comma separated hub names)
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


    /// <summary>
    /// What one HTTP session needs from the transport: the hub its URL named, and the request it came in on
    /// </summary>
    sealed class SessionTransport(HttpHostTransport http, IHubBinding hub, HttpContext context) : IPeerHostTransport
    {
        public IHubTransport HubTransport => http;
        public string ConnectionPrefix => HubConnection.HttpPrefix;
        public UniversalHubHost Host => http.Host;
        public UniversalServerOptions ServerOptions => http.ServerOptions;
        public ILogger? Logger => http.Logger;
        public IHubBinding Requested => hub;

        // only the hub in the URL - the tunnel gate was applied to it
        public IHubBinding? FindRunning(string? key)
            => hub.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && http.IsRunning(hub) ? hub : null;

        public object CreatePeer(IPeerConnection connection) => context;
        public void Remove(PeerHostSession session) => http.Remove(session);
        public IEnumerable<PeerHostSession> Sessions => http.Sessions;
    }


    sealed class ServerFrames(WebSocket socket) : IWebSocketFrames
    {
        public ValueTask Send(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) => socket.SendAsync(message, cancellationToken);

        public async ValueTask<byte[]?> Receive(CancellationToken cancellationToken)
        {
            while (true)
            {
                var message = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (message == null)
                    return null;
                if (!message.IsText)
                    return message.Payload.ToArray();
            }
        }

        public async ValueTask Close()
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed").ConfigureAwait(false);
            }
            catch
            {
                // already gone
            }
        }
    }
}
