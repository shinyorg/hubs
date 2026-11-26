using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.UniversalHubs;

public static class HttpHubClientExtensions
{
    /// <summary>
    /// Reaches the hub over HTTP: hosts found over mDNS, or an address you have (<see cref="HubHostInfo.FromUrl"/>)
    /// </summary>
    /// <param name="mdnsServiceType">The mDNS service type to browse - the one the host gave the hub's UseHttp. Null for the default.</param>
    /// <param name="configure">Timeouts, and the HTTP stack</param>
    public static UniversalHubClientOptions UseHttp(this UniversalHubClientOptions options, string? mdnsServiceType = null, Action<HttpClientTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (mdnsServiceType != null)
            UniversalHubProtocol.ValidateServiceType(mdnsServiceType);

        var http = new HttpClientTransportOptions { ServiceType = mdnsServiceType ?? UniversalHubProtocol.ServiceType };
        configure?.Invoke(http);

        return options.UseTransport(
            sp => new HttpClientTransport(
                http,
                options.AutomaticReconnect,
                // resolved on first discovery, so connecting to a known host needs no mDNS stack
                () => sp.GetService<IHttpServerLocator>(),
                sp.GetService<ILoggerFactory>()
            ),
            services => services.AddHttpServerLocator()
        );
    }
}


/// <summary>
/// How a hub client connects over HTTP
/// </summary>
public sealed class HttpClientTransportOptions
{
    /// <summary>
    /// The mDNS service type browsed for - the hub's own, as the host set it with UseHttp
    /// </summary>
    public string ServiceType { get; internal set; } = UniversalHubProtocol.ServiceType;

    /// <summary>
    /// How long one address gets to answer before the next address (or the next transport) is tried
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// When none of a discovered host's addresses answer, how long to spend looking it up again over mDNS - a host that
    /// restarted (or was killed without a goodbye) is on a new port. Zero turns this off.
    /// </summary>
    public TimeSpan RediscoverTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The HTTP stack - tests swap in an in-memory handler
    /// </summary>
    public Func<HttpMessageHandler>? MessageHandlerFactory { get; set; }
}


/// <summary>
/// A host seen over mDNS, or given by address (<see cref="HubHostInfo.FromUrl"/>)
/// </summary>
public sealed record HttpHubEndpoint(DiscoveredHttpServer Server) : HubEndpoint;


/// <summary>
/// A hub client's HTTP side: browses mDNS for hosts listing the hub, and connects to its Switchboard. Added with
/// <c>o.UseHttp(...)</c>.
/// </summary>
public sealed class HttpClientTransport : IHubClientTransport
{
    readonly HttpClientTransportOptions options;
    readonly bool automaticReconnect;
    readonly Lazy<IHttpServerLocator?> locator;
    readonly ILoggerFactory? loggerFactory;
    readonly ILogger? logger;


    internal HttpClientTransport(HttpClientTransportOptions options, bool automaticReconnect, Func<IHttpServerLocator?> locator, ILoggerFactory? loggerFactory)
    {
        this.options = options;
        this.automaticReconnect = automaticReconnect;
        this.locator = new(locator);
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory?.CreateLogger<HttpClientTransport>();
    }


    public string DisplayName => "HTTP";
    public bool IsSupported => true;


    public IObservable<HubHostSighting> Discover(HubClientContext client) => Observable.Create<HubHostSighting>(async (observer, ct) =>
    {
        if (this.locator.Value is not { } wifi)
            return;

        try
        {
            await foreach (var e in wifi.WatchAsync(this.options.ServiceType, ct).ConfigureAwait(false))
            {
                if (this.Serves(client.HubName, e.Server))
                {
                    var name = e.Server.TxtRecords.TryGetValue(UniversalHubProtocol.TxtName, out var n) ? n : e.Server.InstanceName;
                    var hostId = e.Server.TxtRecords.TryGetValue(UniversalHubProtocol.TxtHostId, out var id) ? id : null;
                    observer.OnNext(new HubHostSighting(name, e.Found ? hostId : null, e.Found ? new HttpHubEndpoint(e.Server) : null));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    });


    /// <summary>
    /// A host that restarted listens on a new port, and when it was killed it never said goodbye, so mDNS can still hand
    /// out its old address. When no address answers, look the host up again by name and try what it advertises now.
    /// </summary>
    public async Task Connect(HubClientContext client, HubHostInfo host, CancellationToken cancellationToken)
    {
        var server = host.Endpoint<HttpHubEndpoint>()?.Server ?? throw new BleHubException($"{host.Name} was not seen over HTTP");
        try
        {
            await this.ConnectHttp(client, server, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!UniversalHubClientErrors.IsRefusal(ex) && !cancellationToken.IsCancellationRequested)
        {
            var fresh = await this.Rediscover(client.HubName, server, cancellationToken).ConfigureAwait(false);
            if (fresh == null)
                throw;

            this.logger?.LogInformation("{Host} moved to {Address} - retrying there", server.InstanceName, fresh.BaseAddress);
            await this.ConnectHttp(client, fresh, cancellationToken).ConfigureAwait(false);
        }
    }


    async Task<DiscoveredHttpServer?> Rediscover(string hubName, DiscoveredHttpServer stale, CancellationToken cancellationToken)
    {
        // an address the app was given (HubHostInfo.FromUrl) has no advertisement to look up again
        if (stale.TxtRecords.Count == 0 || this.options.RediscoverTimeout <= TimeSpan.Zero || this.locator.Value is not { } wifi)
            return null;

        try
        {
            var fresh = await wifi.FindFirstAsync(
                this.options.ServiceType,
                this.options.RediscoverTimeout,
                x => x.InstanceName.Equals(stale.InstanceName, StringComparison.OrdinalIgnoreCase) && this.Serves(hubName, x),
                cancellationToken
            ).ConfigureAwait(false);

            // the same address again would only fail the same way
            return fresh == null || (fresh.BaseAddress == stale.BaseAddress && fresh.Addresses.SequenceEqual(stale.Addresses)) ? null : fresh;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            this.logger?.LogDebug(ex, "Could not look {Host} up again", stale.InstanceName);
            return null;
        }
    }


    async Task ConnectHttp(HubClientContext client, DiscoveredHttpServer server, CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        foreach (var address in Addresses(server))
        {
            var baseUri = address == null ? server.BaseAddress : server.BaseAddressFor(address);
            var root = baseUri.ToString().TrimEnd('/');
            var hubUri = new Uri($"{root}/{Uri.EscapeDataString(client.HubName)}");
            var filesUri = new Uri($"{root}/files");
            var mediaUri = new Uri($"{root}/media");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(this.options.ConnectTimeout);
            try
            {
                await client.Connect(
                    events => new SwitchboardTransport(hubUri, filesUri, mediaUri, events, this.options, this.automaticReconnect, this.loggerFactory),
                    timeout.Token
                ).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (UniversalHubClientErrors.IsRefusal(ex) || cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "{Uri} did not answer", hubUri);
                errors.Add(ex);
            }
        }
        throw errors.Count == 1 ? errors[0] : new AggregateException("No address of the host answered", errors);
    }


    bool Serves(string hubName, DiscoveredHttpServer server)
    {
        if (!server.TxtRecords.TryGetValue(UniversalHubProtocol.TxtHubs, out var hubs))
            return false;

        if (!hubs.Split(',').Any(x => x.Trim().Equals(hubName, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (server.TxtRecords.TryGetValue(UniversalHubProtocol.TxtProtocol, out var proto) && proto != UniversalHubProtocol.Version.ToString())
        {
            this.logger?.LogInformation("Ignoring {Host}: it speaks UniversalHubs protocol {Protocol}", server.InstanceName, proto);
            return false;
        }
        return true;
    }


    /// <summary>
    /// The advertised base address first, then every other resolved address with IPv4 ahead of IPv6
    /// </summary>
    static IEnumerable<IPAddress?> Addresses(DiscoveredHttpServer server)
    {
        yield return null;
        var others = server.Addresses
            .Where(x => !x.Equals(IPAddress.Loopback) && !x.Equals(IPAddress.IPv6Loopback))
            .Where(x => !server.BaseAddress.Host.Trim('[', ']').Equals(x.ToString(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);

        foreach (var address in others)
            yield return address;
    }
}
