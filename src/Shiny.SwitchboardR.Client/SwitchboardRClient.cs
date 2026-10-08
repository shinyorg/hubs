using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.SwitchboardR;

/// <summary>
/// A hub client that reaches its host over Wi-Fi or BLE. Everything from IBleHubClient works the same on either - the
/// generated proxy (<see cref="IBleHubClient{TContract}.Hub"/>), events, Disconnected and the file methods.
/// </summary>
public interface ISwitchboardRClient<out TContract> : IBleHubClient<TContract> where TContract : class
{
    /// <summary>
    /// The transport carrying the current connection, None while disconnected
    /// </summary>
    HubTransports Transport { get; }

    /// <summary>
    /// Watches for hosts over BLE and mDNS at once. A host seen both ways is one <see cref="HubHostInfo"/>, raised again
    /// whenever either side changes. Dispose the subscription to stop.
    /// A transport that can't scan (Bluetooth off, no Wi-Fi) is skipped; the other keeps going.
    /// </summary>
    IObservable<HubHostInfo> DiscoverAll(HubTransports transports = HubTransports.All);

    /// <summary>
    /// Connects over <see cref="HubConnectOptions.Prefer"/> when the host is reachable that way, and falls back to the other
    /// transport when that fails. A host refusing the client (full, ValidateClient) is not retried.
    /// </summary>
    Task Connect(HubHostInfo host, HubConnectOptions? options = null, CancellationToken cancellationToken = default);
}


internal sealed class SwitchboardRClient<TContract> : ISwitchboardRClient<TContract> where TContract : class
{
    readonly IBleHubClient<TContract> inner;
    readonly BleHubClient client;
    readonly Lazy<IHttpServerLocator?> locator;
    readonly SwitchboardRClientOptions options;
    readonly ILoggerFactory? loggerFactory;
    readonly ILogger? logger;


    public SwitchboardRClient(
        IBleHubClient<TContract> inner,
        Func<IHttpServerLocator?> locator,
        SwitchboardRClientOptions options,
        ILoggerFactory? loggerFactory = null
    )
    {
        this.inner = inner;
        this.client = (BleHubClient)(object)inner;
        this.locator = new(locator);
        this.options = options;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory?.CreateLogger<SwitchboardRClient<TContract>>();
    }


    public HubTransports Transport => this.inner.Status != BleHubClientStatus.Connected
        ? HubTransports.None
        : this.client.ExternalTransport != null ? HubTransports.Wifi : HubTransports.Ble;


    public IObservable<HubHostInfo> DiscoverAll(HubTransports transports = HubTransports.All) => Observable.Create<HubHostInfo>(async (observer, ct) =>
    {
        var hosts = new Dictionary<string, HubHostInfo>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        void Update(string key, string name, Func<HubHostInfo, HubHostInfo> change)
        {
            HubHostInfo updated;
            lock (gate)
            {
                var current = hosts.TryGetValue(key, out var existing) ? existing : new HubHostInfo(key, name, null, null, null);
                updated = change(current);
                if (updated.Transports == HubTransports.None)
                    hosts.Remove(key);
                else
                    hosts[key] = updated;
            }
            observer.OnNext(updated);
        }

        IDisposable? bleScan = null;
        if (transports.HasFlag(HubTransports.Ble))
        {
            try
            {
                bleScan = this.inner.Discover().Subscribe(
                    found =>
                    {
                        var name = found.Name ?? found.Id;
                        Update(name, name, x => x with { Ble = found });
                    },
                    ex => this.logger?.LogWarning(ex, "BLE discovery stopped")
                );
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "BLE discovery is unavailable");
            }
        }

        try
        {
            if (transports.HasFlag(HubTransports.Wifi) && this.locator.Value is { } wifi)
            {
                try
                {
                    await foreach (var e in wifi.WatchAsync(this.options.ServiceType, ct).ConfigureAwait(false))
                    {
                        if (!this.Serves(e.Server))
                            continue;

                        var name = e.Server.TxtRecords.TryGetValue(SwitchboardRProtocol.TxtName, out var n) ? n : e.Server.InstanceName;
                        var hostId = e.Server.TxtRecords.TryGetValue(SwitchboardRProtocol.TxtHostId, out var id) ? id : null;
                        Update(name, name, x => e.Found
                            ? x with { Wifi = e.Server, HostId = hostId ?? x.HostId }
                            : x with { Wifi = null });
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex, "Wi-Fi discovery is unavailable");
                }
            }

            // BLE alone (or Wi-Fi gave up) - stay subscribed until the caller disposes
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            bleScan?.Dispose();
        }
    });


    public async Task Connect(HubHostInfo host, HubConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new HubConnectOptions();

        var order = options.Prefer == HubTransports.Ble
            ? new[] { HubTransports.Ble, HubTransports.Wifi }
            : [HubTransports.Wifi, HubTransports.Ble];

        var errors = new List<Exception>();
        foreach (var transport in order)
        {
            if (!options.Allowed.HasFlag(transport) || !host.Transports.HasFlag(transport))
                continue;

            try
            {
                if (transport == HubTransports.Wifi)
                    await this.ConnectWifi(host.Wifi!, options, cancellationToken).ConfigureAwait(false);
                else
                    await this.inner.Connect(host.Ble!, options.ToBle(), cancellationToken).ConfigureAwait(false);

                this.logger?.LogInformation("Connected to {Host} over {Transport}", host.Name, transport);
                return;
            }
            catch (Exception ex) when (IsRefusal(ex) || cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger?.LogInformation(ex, "Could not reach {Host} over {Transport}", host.Name, transport);
                errors.Add(ex);
            }
        }

        throw errors.Count switch
        {
            0 => new BleHubException($"{host.Name} is not reachable over {options.Allowed}"),
            1 => errors[0],
            _ => new BleHubException($"Could not reach {host.Name}: {String.Join("; ", errors.Select(x => x.Message))}", new AggregateException(errors))
        };
    }


    async Task ConnectWifi(DiscoveredHttpServer server, HubConnectOptions options, CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        foreach (var address in Addresses(server))
        {
            var baseUri = address == null ? server.BaseAddress : server.BaseAddressFor(address);
            var root = baseUri.ToString().TrimEnd('/');
            var hubUri = new Uri($"{root}/{this.client.CharacteristicUuid.ToLowerInvariant()}");
            var filesUri = new Uri($"{root}/files");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(this.options.WifiConnectTimeout);
            try
            {
                await this.client.ConnectExternal(
                    events => new SwitchboardTransport(hubUri, filesUri, events, this.options, this.loggerFactory),
                    options.ToBle(),
                    timeout.Token
                ).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsRefusal(ex) || cancellationToken.IsCancellationRequested)
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


    bool Serves(DiscoveredHttpServer server)
    {
        if (!server.TxtRecords.TryGetValue(SwitchboardRProtocol.TxtHubs, out var hubs))
            return false;

        var mine = this.client.CharacteristicUuid;
        if (!hubs.Split(',').Any(x => x.Trim().Equals(mine, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (server.TxtRecords.TryGetValue(SwitchboardRProtocol.TxtProtocol, out var proto) && proto != SwitchboardRProtocol.Version.ToString())
        {
            this.logger?.LogInformation("Ignoring {Host}: it speaks SwitchboardR protocol {Protocol}", server.InstanceName, proto);
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
        foreach (var address in server.Addresses
                     .Where(x => !x.Equals(IPAddress.Loopback) && !x.Equals(IPAddress.IPv6Loopback))
                     .OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            if (!server.BaseAddress.Host.Trim('[', ']').Equals(address.ToString(), StringComparison.OrdinalIgnoreCase))
                yield return address;
        }
    }


    // BleHubClient reports a refusal (full, ValidateClient, not running) this way on either transport
    static bool IsRefusal(Exception ex)
        => ex is BleHubException && ex.Message.StartsWith("Host refused", StringComparison.Ordinal);


    // ---- IBleHubClient<TContract> ----

    public TContract Hub => this.inner.Hub;
    public BleHubClientStatus Status => this.inner.Status;
    public BleHubHostInfo? Host => this.inner.Host;
    public string? HostName => this.inner.HostName;
    public bool CanTransferFiles => this.inner.CanTransferFiles;

    public event EventHandler<BleHubStatusChangedEventArgs>? StatusChanged
    {
        add => this.inner.StatusChanged += value;
        remove => this.inner.StatusChanged -= value;
    }

    public event EventHandler? Connected
    {
        add => this.inner.Connected += value;
        remove => this.inner.Connected -= value;
    }

    public event EventHandler<string?>? Disconnected
    {
        add => this.inner.Disconnected += value;
        remove => this.inner.Disconnected -= value;
    }

    public IObservable<BleHubHostInfo> Discover() => this.inner.Discover();

    public Task Connect(BleHubHostInfo host, BleHubConnectOptions? options = null, CancellationToken cancellationToken = default)
        => this.inner.Connect(host, options, cancellationToken);

    public Task Disconnect() => this.inner.Disconnect();

    public Task<L2CapTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.UploadFile(localFilePath, remoteFileName, progress, cancellationToken);

    public Task<L2CapTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.UploadStream(source, length, remoteFileName, progress, cancellationToken);

    public Task<L2CapTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.DownloadFile(remoteFileName, localFilePath, progress, cancellationToken);
}
