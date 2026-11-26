using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// Starts and stops hosting. Every hub at once (<see cref="Start"/> / <see cref="Stop"/>), or one hub at a time
/// (<see cref="StartHub{THub}"/> / <see cref="StopHub{THub}"/>) - an always-on hub beside one that only runs on demand.
/// Either way a hub is the same hub on every transport it is served over: its clients share Clients, Groups, MaxClients and
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
    /// Starts every hub on the requested transports (each hub only on those it was set up with). A transport that can't
    /// start (Bluetooth off, no network, not this platform) is skipped as long as another one starts - the result says
    /// which did. Throws when none could.
    /// </summary>
    Task<HubTransports> Start(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops every hub on every transport. Connected clients are told why (<see cref="HubDisconnectReason.ServerShutdown"/>).
    /// </summary>
    Task Stop(string? reason = null);

    /// <summary>
    /// Starts only <typeparamref name="THub"/>, like <see cref="Start"/>
    /// </summary>
    Task<HubTransports> StartHub<THub>(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default) where THub : class;

    /// <summary>
    /// Stops only <typeparamref name="THub"/> on every transport; its clients are told why
    /// </summary>
    Task StopHub<THub>(string? reason = null) where THub : class;

    /// <summary>
    /// The transports <typeparamref name="THub"/> is accepting clients on
    /// </summary>
    HubTransports GetRunningTransports<THub>() where THub : class;

    /// <summary>
    /// Changes the host's name without stopping anything: sets <see cref="UniversalServerOptions.LocalName"/>, re-advertises
    /// under it on the transports that advertise, and tells every connected client (<see cref="IBleHubConnection.HostRenamed"/>).
    /// </summary>
    Task Rename(string? localName, CancellationToken cancellationToken = default);

    /// <summary>
    /// A file finished moving, over any transport
    /// </summary>
    event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;

    /// <summary>
    /// Progress of a file moving over L2CAP
    /// </summary>
    event EventHandler<BleHubFileProgressEventArgs>? FileTransferProgress;
}


/// <summary>
/// A transport that listens on addresses - HTTP
/// </summary>
internal interface IListeningTransport
{
    IReadOnlyList<string> ListenUrls { get; }
}


internal sealed class UniversalHubHost : IUniversalHubHost
{
    readonly IServiceProvider services;
    readonly UniversalServerOptions options;
    readonly BleHubProtocolOptions protocolOptions;
    readonly IBleHubSerializer serializer;
    readonly ILoggerFactory? loggerFactory;
    readonly ILogger? logger;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly List<HubEntry> compiled = new();
    readonly ConcurrentDictionary<string, HubEntry> dynamic = new(StringComparer.OrdinalIgnoreCase);
    readonly object dynamicGate = new();


    public UniversalHubHost(
        IServiceProvider services,
        UniversalServerOptions options,
        BleHubProtocolOptions protocolOptions,
        IBleHubSerializer serializer,
        IEnumerable<HubRegistration> registrations,
        ILoggerFactory? loggerFactory = null
    )
    {
        this.services = services;
        this.options = options;
        this.protocolOptions = protocolOptions;
        this.serializer = serializer;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory?.CreateLogger<UniversalHubHost>();

        foreach (var reg in registrations)
        {
            var runtime = this.CreateRuntime(new BleHubRegistration(reg.HubType, reg.Key, reg.Options), BleHubDispatchers.Get(reg.HubType), reg.HubType.Name, null);
            this.compiled.Add(new HubEntry(reg.Key, reg.HubType.Name, reg.HubType, reg.Transports, reg.Settings, runtime));
        }
    }


    public UniversalServerOptions Options => this.options;
    public bool FilesEnabled => this.options.FileTransfers != null;

    /// <summary>
    /// The media channels' packet limit, null when they're off (<see cref="UniversalServerOptions.EnableMediaChannels"/>)
    /// </summary>
    public int? MediaMaxPacketSize => this.options.MediaChannels?.MaxPacketSize;
    public string? HostName => this.options.LocalName;
    public string HostId => this.options.HostId;

    /// <summary>
    /// Every hub: the compiled ones, then any dynamic hubs added while the app runs
    /// </summary>
    public IReadOnlyList<HubEntry> Hubs => this.compiled.Concat(this.dynamic.Values).ToList();

    /// <summary>
    /// Every transport set up on this server
    /// </summary>
    public IReadOnlyList<IUniversalHubTransport> Transports => this.services.GetServices<IUniversalHubTransport>().ToList();

    public HubTransports RunningTransports => this.Transports.Aggregate(
        HubTransports.None,
        (all, transport) => this.Hubs.Any(transport.IsRunning) ? all | transport.Transport : all
    );

    public IReadOnlyList<string> ListenUrls => this.Transports.OfType<IListeningTransport>().FirstOrDefault()?.ListenUrls ?? [];

    public event EventHandler<BleHubFileTransferredEventArgs>? FileTransferred;
    public event EventHandler<BleHubFileProgressEventArgs>? FileTransferProgress;

    /// <summary>
    /// A client left its hub, on any transport - transports drop what they issued it (file and media tokens)
    /// </summary>
    public event Action<string>? ClientGone;


    public HubEntry Get(Type hubType)
        => this.compiled.FirstOrDefault(x => x.HubType == hubType)
           ?? throw new InvalidOperationException($"{hubType.Name} is not registered - add it with AddUniversalServer(server => server.AddHub<{hubType.Name}>(...))");

    public HubEntry? FindByKey(string? key)
        => key == null ? null : this.Hubs.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));


    public Task<HubTransports> Start(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default)
    {
        var hubs = this.Hubs;
        if (hubs.Count == 0)
            throw new InvalidOperationException("No hubs to start - add them with AddUniversalServer, or add dynamic hubs first");

        return this.StartHubs(hubs, transports, cancellationToken);
    }

    public Task Stop(string? reason = null) => this.StopHubs(this.Hubs, new HubDisconnect(HubDisconnectReason.ServerShutdown, reason));

    public Task<HubTransports> StartHub<THub>(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default) where THub : class
        => this.StartHubs([this.Get(typeof(THub))], transports, cancellationToken);

    public Task StopHub<THub>(string? reason = null) where THub : class
        => this.StopHubs([this.Get(typeof(THub))], new HubDisconnect(HubDisconnectReason.ServerShutdown, reason ?? "Hub stopped"));

    public HubTransports GetRunningTransports<THub>() where THub : class => this.RunningOn(this.Get(typeof(THub)));


    public HubTransports RunningOn(IHubBinding hub) => this.Transports.Aggregate(
        HubTransports.None,
        (all, transport) => transport.IsRunning(hub) ? all | transport.Transport : all
    );


    public async Task<HubTransports> StartHubs(IReadOnlyList<HubEntry> hubs, HubTransports transports, CancellationToken cancellationToken)
    {
        if (transports == HubTransports.None)
            throw new ArgumentException("Pick at least one transport", nameof(transports));

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();
            var wanted = hubs.Aggregate(HubTransports.None, (all, hub) => all | hub.Transports) & transports;

            var toStart = this.Transports
                .Where(x => wanted.HasFlag(x.Transport))
                .Select(x => (Transport: x, Hubs: hubs.Where(h => h.Transports.HasFlag(x.Transport) && !x.IsRunning(h)).ToList<IHubBinding>()))
                .Where(x => x.Hubs.Count > 0);

            foreach (var (transport, stopped) in toStart)
            {
                try
                {
                    await transport.Start(stopped, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // a transport this platform doesn't have (Multipeer on Android) is expected, not a problem
                    if (ex is PlatformNotSupportedException)
                        this.logger?.LogDebug("{Transport} hosting is not available here: {Message}", transport.Transport, ex.Message);
                    else
                        this.logger?.LogWarning(ex, "{Transport} hosting did not start", transport.Transport);
                    errors.Add(ex);
                }
            }
            this.UpdateRunning(hubs);

            var running = hubs.Aggregate(HubTransports.None, (all, hub) => all | this.RunningOn(hub)) & transports;
            if (running == HubTransports.None)
            {
                throw errors.Count switch
                {
                    // eg. Multipeer alone, when no hub was set up with it
                    0 => new InvalidOperationException($"None of {transports} is set up for these hubs"),
                    1 => errors[0],
                    _ => new AggregateException("No transport could start", errors)
                };
            }
            return running;
        }
        finally
        {
            this.gate.Release();
        }
    }


    public async Task StopHubs(IReadOnlyList<HubEntry> hubs, HubDisconnect disconnect)
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var toStop = this.Transports
                .Select(x => (Transport: x, Hubs: hubs.Where(x.IsRunning).ToList<IHubBinding>()))
                .Where(x => x.Hubs.Count > 0);

            foreach (var (transport, running) in toStop)
            {
                try
                {
                    await transport.Stop(running, disconnect).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger?.LogWarning(ex, "{Transport} hosting did not stop cleanly", transport.Transport);
                }
            }
            this.UpdateRunning(hubs);

            // a hub running nowhere keeps nobody - anyone a transport didn't account for goes too
            foreach (var hub in hubs.Where(x => !x.Runtime.IsRunning))
                await hub.Runtime.DisconnectAll(disconnect).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }


    // IHubContext.IsRunning and the runtime's own check: running on any transport
    void UpdateRunning(IEnumerable<HubEntry> hubs)
    {
        foreach (var hub in hubs)
            hub.Runtime.IsRunning = this.RunningOn(hub) != HubTransports.None;
    }


    public async Task Rename(string? localName, CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (String.Equals(this.options.LocalName, localName, StringComparison.Ordinal))
                return;

            this.options.LocalName = localName;
            foreach (var transport in this.Transports)
            {
                try
                {
                    await transport.HostRenamed(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger?.LogWarning(ex, "{Transport} could not re-advertise under the new name", transport.Transport);
                }
            }

            // every hub, running or not - clients are told on whichever transport they're on
            await Task.WhenAll(this.Hubs.Select(x => x.Runtime.SetHostName(localName, cancellationToken))).ConfigureAwait(false);
            this.logger?.LogInformation("Host renamed to '{Name}'", localName);
        }
        finally
        {
            this.gate.Release();
        }
    }


    // ---- dynamic hubs ----

    /// <summary>
    /// Adds a hub while the app runs. It doesn't start until asked.
    /// </summary>
    public HubEntry AddDynamic(string key, string name, BleHubOptions options, IBleHubHandler handler, HubTransports transports, IReadOnlyDictionary<Type, object> settings)
    {
        lock (this.dynamicGate)
        {
            if (this.FindByKey(key) != null)
                throw new InvalidOperationException($"A hub named {key} already exists");

            var runtime = this.CreateRuntime(
                new BleHubRegistration(typeof(HandlerHub), key, options),
                new HandlerDispatcher(handler),
                name,
                () => new HandlerHub(handler)
            );
            var entry = new HubEntry(key, name, null, transports, settings, runtime);
            this.dynamic[key] = entry;
            this.logger?.LogInformation("Added hub {Hub}", name);
            return entry;
        }
    }


    /// <summary>
    /// Stops a dynamic hub on every transport, disconnects anyone left, and forgets it
    /// </summary>
    public async Task RemoveDynamic(HubEntry entry, string? reason)
    {
        var disconnect = new HubDisconnect(HubDisconnectReason.ServerShutdown, reason ?? "Hub removed");
        await this.StopHubs([entry], disconnect).ConfigureAwait(false);
        await entry.Runtime.DisconnectAll(disconnect).ConfigureAwait(false);
        this.dynamic.TryRemove(new KeyValuePair<string, HubEntry>(entry.Key, entry));
        this.logger?.LogInformation("Removed hub {Hub}", entry.Name);
    }


    // ---- what the transports report ----

    internal void OnFileTransferred(BleHubFileTransferredEventArgs args) => this.FileTransferred?.Invoke(this, args);
    internal void OnFileProgress(BleHubFileProgressEventArgs args) => this.FileTransferProgress?.Invoke(this, args);


    HubRuntime CreateRuntime(BleHubRegistration registration, IBleHubDispatcher dispatcher, string name, Func<object>? createHub)
    {
        var runtime = new HubRuntime(
            registration,
            dispatcher,
            this.services,
            this.protocolOptions,
            this.serializer,
            this.loggerFactory?.CreateLogger($"Shiny.UniversalHubs.{name}"),
            name,
            createHub
        )
        {
            HostName = this.options.LocalName,
            ResumeWindow = this.options.ResumeWindow,
            ResumeBufferSize = this.options.ResumeBufferSize
        };
        runtime.ClientDisconnected += (_, e) =>
        {
            this.ClientGone?.Invoke(e.Client.Id);
            PeerLinkPeers.Remove(e.Client.Id);   // after OnDisconnectedAsync, which still sees the peer
        };
        return runtime;
    }
}
