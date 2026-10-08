using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.SwitchboardR;

/// <summary>
/// Starts and stops hosting on every transport at once. The hubs are the same hubs either way - BLE and Wi-Fi clients
/// share Clients, Groups, MaxClients and IHubContext.
/// </summary>
public interface ISwitchboardRHost
{
    /// <summary>
    /// The transports currently accepting clients
    /// </summary>
    HubTransports RunningTransports { get; }

    /// <summary>
    /// The addresses the HTTP server listens on while Wi-Fi hosting runs
    /// </summary>
    IReadOnlyList<string> ListenUrls { get; }

    /// <summary>
    /// Starts hosting on the requested transports. A transport that can't start (Bluetooth off, no network) is skipped
    /// as long as another one starts - the result says which did. Throws when none could.
    /// </summary>
    Task<HubTransports> Start(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops every transport. Connected clients are told why.
    /// </summary>
    Task Stop(string? reason = null);

    /// <summary>
    /// A file finished moving over Wi-Fi. (L2CAP transfers raise IBleHubHost.FileTransferred.)
    /// </summary>
    event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;
}


internal sealed class SwitchboardRHost(
    IServiceProvider services,
    IBleHubHost bleHost,
    BleHubHostOptions bleOptions,
    SwitchboardRHostOptions options,
    FileTokens fileTokens,
    ILogger<SwitchboardRHost>? logger = null
) : ISwitchboardRHost
{
    readonly SemaphoreSlim gate = new(1, 1);
    IHttpServerAdvertiser? advertiser;
    bool bleRunning;
    bool startedServer;


    public bool IsWifiRunning { get; private set; }
    public bool FilesEnabled => bleOptions.FileTransfers != null;
    public string? HostName => bleOptions.LocalName;
    public string HostId => options.HostId;

    public HubTransports RunningTransports =>
        (this.bleRunning && bleHost.IsRunning ? HubTransports.Ble : HubTransports.None)
        | (this.IsWifiRunning ? HubTransports.Wifi : HubTransports.None);

    public IReadOnlyList<string> ListenUrls => this.IsWifiRunning ? this.Server.ListenUrls : [];

    public event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;

    HttpServer Server => (HttpServer)(services.GetService(typeof(HttpServer)) ?? throw new InvalidOperationException("No HttpServer is registered - call AddShinyHttpServer"));
    IEnumerable<IHubBinding> Hubs => (IEnumerable<IHubBinding>)services.GetService(typeof(IEnumerable<IHubBinding>))!;


    public async Task<HubTransports> Start(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default)
    {
        if (transports == HubTransports.None)
            throw new ArgumentException("Pick at least one transport", nameof(transports));

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();

            if (transports.HasFlag(HubTransports.Ble) && !this.bleRunning)
            {
                try
                {
                    await bleHost.Start(cancellationToken).ConfigureAwait(false);
                    this.bleRunning = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "Bluetooth hosting did not start");
                    errors.Add(ex);
                }
            }

            if (transports.HasFlag(HubTransports.Wifi) && !this.IsWifiRunning)
            {
                try
                {
                    await this.StartWifi(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "Wi-Fi hosting did not start");
                    errors.Add(ex);
                }
            }

            var running = this.RunningTransports;
            if ((running & transports) == HubTransports.None)
            {
                throw errors.Count == 1
                    ? errors[0]
                    : new AggregateException("No transport could start", errors);
            }
            return running;
        }
        finally
        {
            this.gate.Release();
        }
    }


    public async Task Stop(string? reason = null)
    {
        reason ??= "Host stopped";
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.IsWifiRunning)
                await this.StopWifi(reason).ConfigureAwait(false);

            if (this.bleRunning)
            {
                this.bleRunning = false;
                await bleHost.Stop(reason).ConfigureAwait(false);
            }
        }
        finally
        {
            this.gate.Release();
        }
    }


    internal void OnFileTransferred(BleHubFileTransferredEventArgs args) => this.FileTransferred?.Invoke(this, args);


    async Task StartWifi(CancellationToken cancellationToken)
    {
        var server = this.Server;
        if (options.ManageHttpServer && !server.IsRunning)
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            this.startedServer = true;
        }
        this.IsWifiRunning = true;

        if (options.Advertise)
        {
            try
            {
                this.advertiser = await this.Advertise(server, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // hosting still works for anyone who has the address - only discovery is lost
                logger?.LogWarning(ex, "mDNS advertising failed");
            }
        }
        logger?.LogInformation("Wi-Fi hosting on {Urls}", String.Join(", ", server.ListenUrls));
    }


    async Task StopWifi(string reason)
    {
        // refuse new handshakes first, then hang up on everyone - the hubs see each line leave
        this.IsWifiRunning = false;
        if (this.advertiser != null)
        {
            try
            {
                await this.advertiser.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "mDNS goodbye failed");
            }
            this.advertiser = null;
        }

        foreach (var hub in this.Hubs)
        {
            try
            {
                await hub.HangUpAll(reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not hang up {Hub} clients", hub.HubType.Name);
            }
        }
        fileTokens.Clear();

        if (this.startedServer)
        {
            this.startedServer = false;
            await this.Server.StopAsync().ConfigureAwait(false);
        }
    }


    Task<IHttpServerAdvertiser> Advertise(HttpServer server, CancellationToken cancellationToken)
    {
        var mdns = (IMdnsManager?)services.GetService(typeof(IMdnsManager)) ?? throw new InvalidOperationException("No IMdnsManager is registered");
        var name = String.IsNullOrWhiteSpace(bleOptions.LocalName) ? "Hub host" : bleOptions.LocalName;
        var hubs = String.Join(",", this.Hubs.Select(x => x.CharacteristicUuid.ToLowerInvariant()));

        // a TXT string holds 255 bytes including "hubs=" - about six characteristic UUIDs
        if (hubs.Length + SwitchboardRProtocol.TxtHubs.Length + 1 > 255)
            throw new InvalidOperationException("Too many hubs to advertise over mDNS - at most 6 hubs fit in one TXT record");

        return server.AdvertiseAsync(mdns, o =>
        {
            o.ServiceType = options.ServiceType;
            o.InstanceName = name;
            o.Path = options.BasePath;
            o.TxtRecords[SwitchboardRProtocol.TxtProtocol] = SwitchboardRProtocol.Version.ToString();
            o.TxtRecords[SwitchboardRProtocol.TxtHostId] = options.HostId;
            o.TxtRecords[SwitchboardRProtocol.TxtName] = name;
            o.TxtRecords[SwitchboardRProtocol.TxtHubs] = hubs;
        }, cancellationToken);
    }
}
