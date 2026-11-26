using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// A hub client that reaches its host over whichever of its transports can - Bluetooth LE, HTTP, Multipeer, Nearby or your
/// own. Everything from IBleHubClient works the same on any of them - the generated proxy
/// (<see cref="IBleHubClient{TContract}.Hub"/>), events, Disconnected and the file methods.
/// </summary>
public interface IUniversalHubClient<out TContract> : IBleHubClient<TContract> where TContract : class
{
    /// <summary>
    /// Every transport this client has, in priority order - supported here or not (<see cref="IHubClientTransport.IsSupported"/>)
    /// </summary>
    IReadOnlyList<IHubClientTransport> Transports { get; }

    /// <summary>
    /// The transport carrying the current connection, null while disconnected
    /// </summary>
    IHubClientTransport? Transport { get; }

    /// <summary>
    /// Watches for hosts over every transport at once. A host seen several ways is one <see cref="HubHostInfo"/>, raised
    /// again whenever any side changes. Dispose the subscription to stop. A transport that can't scan (Bluetooth off, no
    /// Wi-Fi) is skipped; the others keep going.
    /// </summary>
    IObservable<HubHostInfo> DiscoverAll();

    /// <summary>
    /// Watches for hosts over <paramref name="transports"/> only - client transport types this client has
    /// </summary>
    IObservable<HubHostInfo> DiscoverAll(IEnumerable<Type> transports);

    /// <summary>
    /// Connects over the first transport the host is seen on, in priority order (<see cref="HubConnectOptions.Prefer{TTransport}"/>
    /// first), falling back to the next when one fails. A host refusing the client (full, ValidateClient) is not retried.
    /// </summary>
    Task Connect(HubHostInfo host, HubConnectOptions? options = null, CancellationToken cancellationToken = default);
}


internal static class UniversalHubClientErrors
{
    // BleHubClient reports a refusal (full, ValidateClient, not running) this way on every transport
    public static bool IsRefusal(Exception ex)
        => ex is BleHubException && ex.Message.StartsWith("Host refused", StringComparison.Ordinal);
}


internal sealed class UniversalHubClient<TContract> : IUniversalHubClient<TContract> where TContract : class
{
    readonly IBleHubClient<TContract> inner;
    readonly BleHubClient client;
    readonly Lazy<IReadOnlyList<IHubClientTransport>> transports;
    readonly UniversalHubClientOptions options;
    readonly ILogger? logger;
    IHubClientTransport? connectedThrough;


    /// <param name="transports">Creates the client's transports, in priority order</param>
    public UniversalHubClient(
        IBleHubClient<TContract> inner,
        UniversalHubClientOptions options,
        Func<IReadOnlyList<IHubClientTransport>> transports,
        ILoggerFactory? loggerFactory = null
    )
    {
        this.inner = inner;
        this.client = (BleHubClient)(object)inner;
        this.transports = new(transports);
        this.options = options;
        this.logger = loggerFactory?.CreateLogger<UniversalHubClient<TContract>>();
    }


    public IReadOnlyList<IHubClientTransport> Transports => this.transports.Value;

    IReadOnlyList<Type> Priority => this.Transports.Select(x => x.GetType()).ToList();

    public IHubClientTransport? Transport => this.inner.Status is BleHubClientStatus.Connected or BleHubClientStatus.Reconnecting
        ? this.connectedThrough
        : null;


    public IObservable<HubHostInfo> DiscoverAll() => this.Discover(this.Transports);

    public IObservable<HubHostInfo> DiscoverAll(IEnumerable<Type> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        var wanted = transports.ToList();
        var unknown = wanted.FirstOrDefault(t => !this.Transports.Any(x => x.GetType() == t));
        if (unknown != null)
            throw new ArgumentException($"{unknown.Name} is not a transport this hub client has", nameof(transports));

        return this.Discover(this.Transports.Where(x => wanted.Contains(x.GetType())).ToList());
    }


    IObservable<HubHostInfo> Discover(IReadOnlyList<IHubClientTransport> transports) => Observable.Create<HubHostInfo>(observer =>
    {
        var hosts = new Dictionary<string, HubHostInfo>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        var priority = this.Priority;
        var context = new HubClientContext(this.client, new BleHubConnectOptions());

        void Update(IHubClientTransport transport, HubHostSighting seen)
        {
            HubHostInfo updated;
            lock (gate)
            {
                var current = hosts.TryGetValue(seen.Name, out var existing) ? existing : new HubHostInfo(seen.Name, seen.Name, null);
                updated = current.With(transport.GetType(), seen.Endpoint, priority);
                if (seen.HostId != null)
                    updated = updated with { HostId = seen.HostId };

                if (updated.Transports.Count == 0)
                    hosts.Remove(seen.Name);
                else
                    hosts[seen.Name] = updated;
            }
            observer.OnNext(updated);
        }

        var scans = new List<IDisposable>();
        foreach (var transport in transports.Where(x => x.IsSupported))
        {
            try
            {
                scans.Add(transport.Discover(context).Subscribe(
                    seen => Update(transport, seen),
                    ex => this.logger?.LogWarning(ex, "{Transport} discovery stopped", transport.DisplayName)
                ));
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "{Transport} discovery is unavailable", transport.DisplayName);
            }
        }

        return () =>
        {
            foreach (var scan in scans)
                scan.Dispose();
        };
    });


    public async Task Connect(HubHostInfo host, HubConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new HubConnectOptions();

        var context = new HubClientContext(this.client, options.ToBle() with { AutomaticReconnect = this.options.AutomaticReconnect });
        var order = options
            .Order(this.Transports)
            .Where(x => x.IsSupported && host.Endpoints.ContainsKey(x.GetType()))
            .ToList();

        var errors = new List<Exception>();
        foreach (var transport in order)
        {
            try
            {
                this.connectedThrough = transport;
                await transport.Connect(context, host, cancellationToken).ConfigureAwait(false);
                this.logger?.LogInformation("Connected to {Host} over {Transport}", host.Name, transport.DisplayName);
                return;
            }
            catch (Exception ex) when (UniversalHubClientErrors.IsRefusal(ex) || cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger?.LogInformation(ex, "Could not reach {Host} over {Transport}", host.Name, transport.DisplayName);
                errors.Add(ex);
            }
        }

        throw errors.Count switch
        {
            0 => new BleHubException(
                $"{host.Name} is not reachable over any transport this client may use - seen over "
                + (host.Transports.Count == 0 ? "nothing" : String.Join(", ", host.Transports.Select(x => this.Transports.FirstOrDefault(t => t.GetType() == x)?.DisplayName ?? x.Name)))
            ),
            1 => errors[0],
            _ => new BleHubException($"Could not reach {host.Name}: {String.Join("; ", errors.Select(x => x.Message))}", new AggregateException(errors))
        };
    }


    // ---- IBleHubClient<TContract> ----

    public TContract Hub => this.inner.Hub;
    public BleHubClientStatus Status => this.inner.Status;
    public string? HostName => this.inner.HostName;
    public string? ClientName => this.inner.ClientName;
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

    public event EventHandler<HubDisconnect>? Disconnected
    {
        add => this.inner.Disconnected += value;
        remove => this.inner.Disconnected -= value;
    }

    public event EventHandler<string?>? HostRenamed
    {
        add => this.inner.HostRenamed += value;
        remove => this.inner.HostRenamed -= value;
    }

    public Task Disconnect() => this.inner.Disconnect();

    // over Wi-Fi this is refused (NotSupportedException) until the Switchboard transport carries renames
    public Task Rename(string? name, CancellationToken cancellationToken = default) => this.inner.Rename(name, cancellationToken);

    public bool CanOpenMediaChannel => this.inner.CanOpenMediaChannel;

    public Task<IBleHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken = default) => this.inner.OpenMediaChannel(cancellationToken);

    public Task<L2CapTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.UploadFile(localFilePath, remoteFileName, progress, cancellationToken);

    public Task<L2CapTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.UploadStream(source, length, remoteFileName, progress, cancellationToken);

    public Task<L2CapTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.DownloadFile(remoteFileName, localFilePath, progress, cancellationToken);
}
