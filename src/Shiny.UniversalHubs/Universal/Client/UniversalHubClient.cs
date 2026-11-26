using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

namespace Shiny.UniversalHubs;

/// <summary>
/// A hub client that reaches its host over whichever of its transports can - Bluetooth LE, HTTP, Multipeer, Nearby or your
/// own. Everything from IHubProxy works the same on any of them - the generated proxy
/// (<see cref="IHubProxy{TContract}.Hub"/>), events, Disconnected and the file methods.
/// </summary>
public interface IUniversalHubClient<out TContract> : IHubProxy<TContract> where TContract : class
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
    /// Whether <paramref name="transport"/> (a client transport type this client has) may be used - every transport is,
    /// until <see cref="SetEnabled"/> turns it off
    /// </summary>
    bool IsEnabled(Type transport);

    /// <summary>
    /// Turns a transport on or off while the app runs - to test each one on its own, or to let the user pick. A disabled
    /// transport leaves running discoveries (the hosts it saw lose it) and is never connected over. Disabling the
    /// transport the connection is on disconnects it.
    /// </summary>
    Task SetEnabled(Type transport, bool enabled);

    /// <summary>
    /// A transport was enabled or disabled
    /// </summary>
    event EventHandler? EnabledChanged;

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


public static class UniversalHubClientTransportExtensions
{
    /// <summary>
    /// Whether <typeparamref name="TTransport"/> may be used
    /// </summary>
    public static bool IsEnabled<TTransport>(this IUniversalHubClient<object> client) where TTransport : IHubClientTransport
        => client.IsEnabled(typeof(TTransport));

    /// <summary>
    /// Turns <typeparamref name="TTransport"/> back on
    /// </summary>
    public static Task Enable<TTransport>(this IUniversalHubClient<object> client) where TTransport : IHubClientTransport
        => client.SetEnabled(typeof(TTransport), true);

    /// <summary>
    /// Turns <typeparamref name="TTransport"/> off - see <see cref="IUniversalHubClient{TContract}.SetEnabled"/>
    /// </summary>
    public static Task Disable<TTransport>(this IUniversalHubClient<object> client) where TTransport : IHubClientTransport
        => client.SetEnabled(typeof(TTransport), false);

    /// <summary>
    /// Uses <typeparamref name="TTransport"/> and turns every other transport off
    /// </summary>
    public static async Task Solo<TTransport>(this IUniversalHubClient<object> client) where TTransport : IHubClientTransport
    {
        await client.SetEnabled(typeof(TTransport), true).ConfigureAwait(false);
        foreach (var other in client.Transports.Where(x => x.GetType() != typeof(TTransport)))
            await client.SetEnabled(other.GetType(), false).ConfigureAwait(false);
    }
}


internal static class UniversalHubClientErrors
{
    // HubProxy reports a refusal (full, ValidateClient, not running) this way on every transport
    public static bool IsRefusal(Exception ex)
        => ex is HubException && ex.Message.StartsWith("Host refused", StringComparison.Ordinal);
}


internal sealed class UniversalHubClient<TContract> : IUniversalHubClient<TContract> where TContract : class
{
    readonly IHubProxy<TContract> inner;
    readonly HubProxy client;
    readonly Lazy<IReadOnlyList<IHubClientTransport>> transports;
    readonly UniversalHubClientOptions options;
    readonly ILogger? logger;
    readonly HashSet<Type> disabled = new();
    IHubClientTransport? connectedThrough;


    /// <param name="transports">Creates the client's transports, in priority order</param>
    public UniversalHubClient(
        IHubProxy<TContract> inner,
        UniversalHubClientOptions options,
        Func<IReadOnlyList<IHubClientTransport>> transports,
        ILoggerFactory? loggerFactory = null
    )
    {
        this.inner = inner;
        this.client = (HubProxy)(object)inner;
        this.transports = new(transports);
        this.options = options;
        this.logger = loggerFactory?.CreateLogger<UniversalHubClient<TContract>>();
    }


    public IReadOnlyList<IHubClientTransport> Transports => this.transports.Value;

    IReadOnlyList<Type> Priority => this.Transports.Select(x => x.GetType()).ToList();

    public IHubClientTransport? Transport => this.inner.Status is HubClientStatus.Connected or HubClientStatus.Reconnecting
        ? this.connectedThrough
        : null;


    public event EventHandler? EnabledChanged;

    public bool IsEnabled(Type transport)
    {
        this.Validate(transport);
        lock (this.disabled)
            return !this.disabled.Contains(transport);
    }


    public async Task SetEnabled(Type transport, bool enabled)
    {
        this.Validate(transport);
        bool changed;
        lock (this.disabled)
            changed = enabled ? this.disabled.Remove(transport) : this.disabled.Add(transport);

        if (!changed)
            return;

        this.logger?.LogInformation("{Transport} {State}", transport.Name, enabled ? "enabled" : "disabled");
        this.EnabledChanged?.Invoke(this, EventArgs.Empty);

        if (!enabled && this.Transport?.GetType() == transport)
            await this.inner.Disconnect().ConfigureAwait(false);
    }


    void Validate(Type transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (!this.Transports.Any(x => x.GetType() == transport))
            throw new ArgumentException($"{transport.Name} is not a transport this hub client has", nameof(transport));
    }


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
        var scans = new Dictionary<IHubClientTransport, IDisposable>();
        var gate = new object();
        var priority = this.Priority;
        var context = new HubClientContext(this.client, new HubHandshakeOptions());
        var disposed = false;

        void Publish(string name, HubHostInfo updated)
        {
            if (updated.Transports.Count == 0)
                hosts.Remove(name);
            else
                hosts[name] = updated;
            observer.OnNext(updated);
        }

        void Update(IHubClientTransport transport, HubHostSighting seen)
        {
            lock (gate)
            {
                // a scan that was stopped may still report on its way out
                if (!scans.ContainsKey(transport))
                    return;

                var current = hosts.TryGetValue(seen.Name, out var existing) ? existing : new HubHostInfo(seen.Name, seen.Name, null);
                var updated = current.With(transport.GetType(), seen.Endpoint, priority);
                if (seen.HostId != null)
                    updated = updated with { HostId = seen.HostId };

                Publish(seen.Name, updated);
            }
        }

        // under the gate: scan over what is enabled, stop what isn't - the hosts a stopped scan saw lose it
        void Sync()
        {
            if (disposed)
                return;

            foreach (var transport in transports.Where(x => x.IsSupported))
            {
                var enabled = this.IsEnabled(transport.GetType());
                if (enabled && !scans.ContainsKey(transport))
                {
                    try
                    {
                        scans[transport] = Disposable.Empty;
                        scans[transport] = transport.Discover(context).Subscribe(
                            seen => Update(transport, seen),
                            ex => this.logger?.LogWarning(ex, "{Transport} discovery stopped", transport.DisplayName)
                        );
                    }
                    catch (Exception ex)
                    {
                        this.logger?.LogWarning(ex, "{Transport} discovery is unavailable", transport.DisplayName);
                    }
                }
                else if (!enabled && scans.Remove(transport, out var scan))
                {
                    scan.Dispose();
                    var type = transport.GetType();
                    foreach (var host in hosts.Values.Where(x => x.Endpoints.ContainsKey(type)).ToList())
                        Publish(host.Key, host.With(type, null, priority));
                }
            }
        }

        void OnEnabledChanged(object? sender, EventArgs e)
        {
            lock (gate)
                Sync();
        }

        lock (gate)
            Sync();
        this.EnabledChanged += OnEnabledChanged;

        return () =>
        {
            this.EnabledChanged -= OnEnabledChanged;
            lock (gate)
            {
                disposed = true;
                foreach (var scan in scans.Values)
                    scan.Dispose();
                scans.Clear();
            }
        };
    });


    public async Task Connect(HubHostInfo host, HubConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new HubConnectOptions();

        var context = new HubClientContext(this.client, options.ToBle() with { AutomaticReconnect = this.options.AutomaticReconnect });
        var order = options
            .Order(this.Transports)
            .Where(x => x.IsSupported && this.IsEnabled(x.GetType()) && host.Endpoints.ContainsKey(x.GetType()))
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
            0 => new HubException(
                $"{host.Name} is not reachable over any transport this client may use - seen over "
                + (host.Transports.Count == 0 ? "nothing" : String.Join(", ", host.Transports.Select(x => this.Transports.FirstOrDefault(t => t.GetType() == x)?.DisplayName ?? x.Name)))
            ),
            1 => errors[0],
            _ => new HubException($"Could not reach {host.Name}: {String.Join("; ", errors.Select(x => x.Message))}", new AggregateException(errors))
        };
    }


    // ---- IHubProxy<TContract> ----

    public TContract Hub => this.inner.Hub;
    public HubClientStatus Status => this.inner.Status;
    public string? HostName => this.inner.HostName;
    public string? ClientName => this.inner.ClientName;
    public bool CanTransferFiles => this.inner.CanTransferFiles;

    public event EventHandler<HubStatusChangedEventArgs>? StatusChanged
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

    public Task Rename(string? name, CancellationToken cancellationToken = default) => this.inner.Rename(name, cancellationToken);

    public bool CanOpenMediaChannel => this.inner.CanOpenMediaChannel;

    public Task<IHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken = default) => this.inner.OpenMediaChannel(cancellationToken);

    public Task<HubFileTransferResult> UploadFile(string localFilePath, string? remoteFileName = null, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.UploadFile(localFilePath, remoteFileName, progress, cancellationToken);

    public Task<HubFileTransferResult> UploadStream(Stream source, long length, string remoteFileName, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.UploadStream(source, length, remoteFileName, progress, cancellationToken);

    public Task<HubFileTransferResult> DownloadFile(string remoteFileName, string localFilePath, IProgress<HubTransferProgress>? progress = null, CancellationToken cancellationToken = default)
        => this.inner.DownloadFile(remoteFileName, localFilePath, progress, cancellationToken);
}
