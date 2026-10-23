using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.UniversalHubs;

/// <summary>
/// Starts and stops hosting. Every hub at once (<see cref="Start"/> / <see cref="Stop"/>), or one hub at a time
/// (<see cref="StartHub{THub}"/> / <see cref="StopHub{THub}"/>) - an always-on hub beside one that only runs on demand.
/// Either way a hub is the same hub on both transports: BLE and HTTP clients share Clients, Groups, MaxClients and
/// IHubContext.
/// </summary>
public interface IUniversalHubHost
{
    /// <summary>
    /// The transports at least one hub is accepting clients on
    /// </summary>
    HubTransports RunningTransports { get; }

    /// <summary>
    /// The addresses the HTTP server listens on while any hub is hosted over HTTP
    /// </summary>
    IReadOnlyList<string> ListenUrls { get; }

    /// <summary>
    /// Starts every hub on the requested transports. A transport that can't start (Bluetooth off, no network) is skipped
    /// as long as another one starts - the result says which did. Throws when none could.
    /// </summary>
    Task<HubTransports> Start(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops every hub on every transport. Connected clients are told why (<see cref="HubDisconnectReason.ServerShutdown"/>).
    /// </summary>
    Task Stop(string? reason = null);

    /// <summary>
    /// Starts only <typeparamref name="THub"/>, like <see cref="Start"/>. The HTTP server starts with the first hub hosted
    /// over HTTP, and mDNS advertises the hubs that are running.
    /// </summary>
    Task<HubTransports> StartHub<THub>(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default) where THub : class;

    /// <summary>
    /// Stops only <typeparamref name="THub"/> on both transports; its clients are told why. The HTTP server stops with the
    /// last hub hosted over HTTP.
    /// </summary>
    Task StopHub<THub>(string? reason = null) where THub : class;

    /// <summary>
    /// The transports <typeparamref name="THub"/> is accepting clients on
    /// </summary>
    HubTransports GetRunningTransports<THub>() where THub : class;

    /// <summary>
    /// A file finished moving over HTTP. (L2CAP transfers raise IBleHubHost.FileTransferred.)
    /// </summary>
    event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;
}


internal sealed class UniversalHubHost(
    IServiceProvider services,
    IBleHubHost bleHost,
    BleHubHostOptions bleOptions,
    UniversalHubHostOptions options,
    FileTokens fileTokens,
    MediaTokens mediaTokens,
    ILogger<UniversalHubHost>? logger = null
) : IUniversalHubHost
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly List<IHttpServerAdvertiser> advertisers = new();
    bool startedServer;


    public bool IsHttpRunning => this.Hubs.Any(x => x.IsHttpRunning);
    public bool FilesEnabled => bleOptions.FileTransfers != null;

    /// <summary>
    /// The media channels' packet limit, null when they're off (BleHubHostOptions.EnableMediaChannels)
    /// </summary>
    public int? MediaMaxPacketSize => bleOptions.MediaChannels?.MaxPacketSize;
    public string? HostName => bleOptions.LocalName;
    public string HostId => options.HostId;

    public HubTransports RunningTransports =>
        (bleHost.IsRunning ? HubTransports.Ble : HubTransports.None)
        | (this.IsHttpRunning ? HubTransports.Http : HubTransports.None);

    public IReadOnlyList<string> ListenUrls => this.IsHttpRunning ? this.Server.ListenUrls : [];

    public event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;

    HttpServer Server => (HttpServer)(services.GetService(typeof(HttpServer)) ?? throw new InvalidOperationException("No HttpServer is registered - call AddShinyHttpServer"));
    IReadOnlyList<IHubBinding> Hubs => ((IEnumerable<IHubBinding>)services.GetService(typeof(IEnumerable<IHubBinding>))!).ToList();


    public Task<HubTransports> Start(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default)
        => this.StartHubs(this.Hubs, transports, cancellationToken);

    public Task Stop(string? reason = null) => this.StopHubs(this.Hubs, reason, all: true);

    public Task<HubTransports> StartHub<THub>(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default) where THub : class
        => this.StartHubs([this.Find<THub>()], transports, cancellationToken);

    public Task StopHub<THub>(string? reason = null) where THub : class => this.StopHubs([this.Find<THub>()], reason, all: false);

    public HubTransports GetRunningTransports<THub>() where THub : class
    {
        var hub = this.Find<THub>();
        return (hub.IsBleRunning ? HubTransports.Ble : HubTransports.None)
               | (hub.IsHttpRunning ? HubTransports.Http : HubTransports.None);
    }


    IHubBinding Find<THub>() where THub : class
        => this.Hubs.FirstOrDefault(x => x.HubType == typeof(THub))
           ?? throw new InvalidOperationException($"{typeof(THub).Name} is not served by UniversalHubs - add it with AddHub");


    async Task<HubTransports> StartHubs(IReadOnlyList<IHubBinding> hubs, HubTransports transports, CancellationToken cancellationToken)
    {
        if (transports == HubTransports.None)
            throw new ArgumentException("Pick at least one transport", nameof(transports));

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();

            if (transports.HasFlag(HubTransports.Ble))
            {
                foreach (var hub in hubs.Where(x => !x.IsBleRunning))
                {
                    try
                    {
                        await hub.StartBle(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger?.LogWarning(ex, "Bluetooth hosting of {Hub} did not start", hub.HubType.Name);
                        errors.Add(ex);
                        break;  // the radio is shared - one failure is every failure
                    }
                }
            }

            if (transports.HasFlag(HubTransports.Http) && hubs.Any(x => !x.IsHttpRunning))
            {
                try
                {
                    await this.StartHttp(hubs, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "HTTP hosting did not start");
                    errors.Add(ex);
                }
            }

            var running = hubs.Aggregate(
                HubTransports.All,
                (all, hub) => all & ((hub.IsBleRunning ? HubTransports.Ble : HubTransports.None) | (hub.IsHttpRunning ? HubTransports.Http : HubTransports.None))
            );
            if ((running & transports) == HubTransports.None)
            {
                throw errors.Count == 1
                    ? errors[0]
                    : new AggregateException("No transport could start", errors);
            }
            return running & transports;
        }
        finally
        {
            this.gate.Release();
        }
    }


    async Task StopHubs(IReadOnlyList<IHubBinding> hubs, string? reason, bool all)
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var httpHubs = hubs.Where(x => x.IsHttpRunning).ToList();
            if (httpHubs.Count > 0)
                await this.StopHttp(httpHubs, reason).ConfigureAwait(false);

            // stopping a hub over BLE also disconnects anyone left on another transport
            if (all)
            {
                if (bleHost.IsRunning)
                    await bleHost.Stop(reason).ConfigureAwait(false);
            }
            else
            {
                foreach (var hub in hubs.Where(x => x.IsBleRunning))
                    await hub.StopBle(reason).ConfigureAwait(false);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }


    internal void OnFileTransferred(BleHubFileTransferredEventArgs args) => this.FileTransferred?.Invoke(this, args);


    async Task StartHttp(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        var server = this.Server;
        if (options.ManageHttpServer && !server.IsRunning)
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            this.startedServer = true;
        }
        foreach (var hub in hubs)
            hub.IsHttpRunning = true;

        await this.Readvertise(cancellationToken).ConfigureAwait(false);
        logger?.LogInformation("HTTP hosting {Hubs} on {Urls}", String.Join(", ", hubs.Select(x => x.HubType.Name)), String.Join(", ", server.ListenUrls));
    }


    async Task StopHttp(IReadOnlyList<IHubBinding> hubs, string? reason)
    {
        // refuse new handshakes first, then hang up on everyone - the hubs see each line leave
        foreach (var hub in hubs)
            hub.IsHttpRunning = false;

        await this.Readvertise(CancellationToken.None).ConfigureAwait(false);

        foreach (var hub in hubs)
        {
            try
            {
                await hub.HangUpAll(new HubDisconnect(HubDisconnectReason.ServerShutdown, reason)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not hang up {Hub} clients", hub.HubType.Name);
            }
        }

        if (!this.IsHttpRunning)
        {
            fileTokens.Clear();
            mediaTokens.Clear();
            if (this.startedServer)
            {
                this.startedServer = false;
                await this.Server.StopAsync().ConfigureAwait(false);
            }
        }
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
                logger?.LogDebug(ex, "mDNS goodbye failed");
            }
        }
        this.advertisers.Clear();

        var running = this.Hubs.Where(x => x.IsHttpRunning).ToList();
        if (!options.Advertise || running.Count == 0)
            return;

        try
        {
            await this.Advertise(this.Server, running, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // hosting still works for anyone who has the address - only discovery is lost
            logger?.LogWarning(ex, "mDNS advertising failed");
        }
    }


    /// <summary>
    /// One advertisement per mDNS service type, listing the hubs advertised under it
    /// </summary>
    async Task Advertise(HttpServer server, IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken)
    {
        var mdns = (IMdnsManager?)services.GetService(typeof(IMdnsManager)) ?? throw new InvalidOperationException("No IMdnsManager is registered");
        var name = String.IsNullOrWhiteSpace(bleOptions.LocalName) ? "Hub host" : bleOptions.LocalName;

        foreach (var (serviceType, uuids) in GetAdvertisements(hubs, options.ServiceType))
        {
            var advertiser = await server.AdvertiseAsync(mdns, o =>
            {
                o.ServiceType = serviceType;
                o.InstanceName = name;
                o.Path = options.BasePath;
                o.TxtRecords[UniversalHubProtocol.TxtProtocol] = UniversalHubProtocol.Version.ToString();
                o.TxtRecords[UniversalHubProtocol.TxtHostId] = options.HostId;
                o.TxtRecords[UniversalHubProtocol.TxtName] = name;
                o.TxtRecords[UniversalHubProtocol.TxtHubs] = uuids;
            }, cancellationToken).ConfigureAwait(false);
            this.advertisers.Add(advertiser);
        }
    }


    /// <summary>
    /// Groups hubs by the mDNS service type they are advertised under: (service type, comma separated characteristic UUIDs)
    /// </summary>
    internal static IReadOnlyList<(string ServiceType, string Hubs)> GetAdvertisements(IEnumerable<IHubBinding> hubs, string defaultServiceType)
    {
        var list = new List<(string, string)>();
        foreach (var group in hubs.GroupBy(x => x.ServiceType ?? defaultServiceType, StringComparer.OrdinalIgnoreCase))
        {
            var uuids = String.Join(",", group.Select(x => x.CharacteristicUuid.ToLowerInvariant()));

            // a TXT string holds 255 bytes including "hubs=" - about six characteristic UUIDs
            if (uuids.Length + UniversalHubProtocol.TxtHubs.Length + 1 > 255)
                throw new InvalidOperationException($"Too many hubs to advertise as {group.Key} over mDNS - at most 6 hubs fit in one TXT record");

            list.Add((group.Key, uuids));
        }
        return list;
    }
}
