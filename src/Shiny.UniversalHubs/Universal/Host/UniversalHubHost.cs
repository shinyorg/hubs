using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// Every transport set up on this server, supported here or not (<see cref="IHubTransport.IsSupported"/>)
    /// </summary>
    IReadOnlyList<IHubTransport> Transports { get; }

    /// <summary>
    /// The transports at least one hub is accepting clients on
    /// </summary>
    IReadOnlyList<IHubTransport> RunningTransports { get; }

    /// <summary>
    /// The addresses the HTTP server listens on while any hub is hosted over HTTP
    /// </summary>
    IReadOnlyList<string> ListenUrls { get; }

    /// <summary>
    /// Starts every hub on every transport it was set up with. A transport that can't start (Bluetooth off, no network,
    /// not this platform) is skipped as long as another one starts - the result says which did. Throws when none could.
    /// </summary>
    Task<IReadOnlyList<IHubTransport>> Start(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts every hub on <paramref name="transports"/> only (each hub only on those it was set up with) - the types of
    /// transports set up on this server. See <see cref="Start(CancellationToken)"/>.
    /// </summary>
    Task<IReadOnlyList<IHubTransport>> Start(IEnumerable<Type> transports, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops every hub on every transport. Connected clients are told why (<see cref="HubDisconnectReason.ServerShutdown"/>).
    /// </summary>
    Task Stop(string? reason = null);

    /// <summary>
    /// Stops every hub on <paramref name="transports"/> only; their clients on those transports are told why. A hub left
    /// running on another transport keeps its other clients.
    /// </summary>
    Task Stop(IEnumerable<Type> transports, string? reason = null);

    /// <summary>
    /// Starts only <typeparamref name="THub"/>, like <see cref="Start(CancellationToken)"/>
    /// </summary>
    Task<IReadOnlyList<IHubTransport>> StartHub<THub>(CancellationToken cancellationToken = default) where THub : class;

    /// <summary>
    /// Starts only <typeparamref name="THub"/> on <paramref name="transports"/> only
    /// </summary>
    Task<IReadOnlyList<IHubTransport>> StartHub<THub>(IEnumerable<Type> transports, CancellationToken cancellationToken = default) where THub : class;

    /// <summary>
    /// Stops only <typeparamref name="THub"/> on every transport; its clients are told why
    /// </summary>
    Task StopHub<THub>(string? reason = null) where THub : class;

    /// <summary>
    /// Stops only <typeparamref name="THub"/> on <paramref name="transports"/> only
    /// </summary>
    Task StopHub<THub>(IEnumerable<Type> transports, string? reason = null) where THub : class;

    /// <summary>
    /// The transports <typeparamref name="THub"/> is accepting clients on
    /// </summary>
    IReadOnlyList<IHubTransport> GetRunningTransports<THub>() where THub : class;

    /// <summary>
    /// A hub started or stopped on a transport - <see cref="RunningTransports"/> may have changed
    /// </summary>
    event EventHandler? RunningChanged;

    /// <summary>
    /// Changes the host's name without stopping anything: sets <see cref="UniversalServerOptions.LocalName"/>, re-advertises
    /// under it on the transports that advertise, and tells every connected client (<see cref="IHubConnection.HostRenamed"/>).
    /// </summary>
    Task Rename(string? localName, CancellationToken cancellationToken = default);

    /// <summary>
    /// A file finished moving, over any transport
    /// </summary>
    event EventHandler<HubFileTransferredEventArgs>? FileTransferred;

    /// <summary>
    /// Progress of a file moving over L2CAP
    /// </summary>
    event EventHandler<HubFileProgressEventArgs>? FileTransferProgress;
}


public static class UniversalHubHostExtensions
{
    /// <summary>
    /// Starts every hub set up with <typeparamref name="TTransport"/> on it
    /// </summary>
    public static Task<IReadOnlyList<IHubTransport>> Start<TTransport>(this IUniversalHubHost host, CancellationToken cancellationToken = default)
        where TTransport : IHubTransport
        => host.Start([typeof(TTransport)], cancellationToken);

    /// <summary>
    /// Stops every hub on <typeparamref name="TTransport"/>
    /// </summary>
    public static Task Stop<TTransport>(this IUniversalHubHost host, string? reason = null) where TTransport : IHubTransport
        => host.Stop([typeof(TTransport)], reason);

    /// <summary>
    /// Starts <typeparamref name="THub"/> on <typeparamref name="TTransport"/>
    /// </summary>
    public static Task<IReadOnlyList<IHubTransport>> StartHub<THub, TTransport>(this IUniversalHubHost host, CancellationToken cancellationToken = default)
        where THub : class
        where TTransport : IHubTransport
        => host.StartHub<THub>([typeof(TTransport)], cancellationToken);

    /// <summary>
    /// Stops <typeparamref name="THub"/> on <typeparamref name="TTransport"/>
    /// </summary>
    public static Task StopHub<THub, TTransport>(this IUniversalHubHost host, string? reason = null)
        where THub : class
        where TTransport : IHubTransport
        => host.StopHub<THub>([typeof(TTransport)], reason);

    /// <summary>
    /// Whether any hub is accepting clients on <typeparamref name="TTransport"/>
    /// </summary>
    public static bool IsRunning<TTransport>(this IUniversalHubHost host) where TTransport : IHubTransport
        => host.RunningTransports.Any(x => x.GetType() == typeof(TTransport));

    /// <summary>
    /// The transport of type <typeparamref name="TTransport"/> (exactly - a transport's type is its identity), null when it
    /// isn't set up on this server
    /// </summary>
    public static TTransport? GetTransport<TTransport>(this IUniversalHubHost host) where TTransport : class, IHubTransport
        => (TTransport?)host.Transports.FirstOrDefault(x => x.GetType() == typeof(TTransport));
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
    readonly HubProtocolOptions protocolOptions;
    readonly IHubSerializer serializer;
    readonly ILoggerFactory? loggerFactory;
    readonly ILogger? logger;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly List<HubEntry> compiled = new();
    readonly ConcurrentDictionary<string, HubEntry> dynamic = new(StringComparer.OrdinalIgnoreCase);
    readonly object dynamicGate = new();


    public UniversalHubHost(
        IServiceProvider services,
        UniversalServerOptions options,
        HubProtocolOptions protocolOptions,
        IHubSerializer serializer,
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
            var runtime = this.CreateRuntime(new HubDefinition(reg.HubType, reg.Key, reg.Options), HubDispatchers.Get(reg.HubType), reg.HubType.Name, null);
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
    public IReadOnlyList<IHubTransport> Transports => this.transports ??= this.services.GetServices<IHubTransport>().ToList();
    IReadOnlyList<IHubTransport>? transports;

    public IReadOnlyList<IHubTransport> RunningTransports
    {
        get
        {
            var hubs = this.Hubs;
            return this.Transports.Where(x => hubs.Any(x.IsRunning)).ToList();
        }
    }

    public IReadOnlyList<string> ListenUrls => this.Transports.OfType<IListeningTransport>().FirstOrDefault()?.ListenUrls ?? [];

    public event EventHandler<HubFileTransferredEventArgs>? FileTransferred;
    public event EventHandler<HubFileProgressEventArgs>? FileTransferProgress;
    public event EventHandler? RunningChanged;

    /// <summary>
    /// A client left its hub, on any transport - transports drop what they issued it (file and media tokens)
    /// </summary>
    public event Action<string>? ClientGone;


    public HubEntry Get(Type hubType)
        => this.compiled.FirstOrDefault(x => x.HubType == hubType)
           ?? throw new InvalidOperationException($"{hubType.Name} is not registered - add it with AddUniversalServer(server => server.AddHub<{hubType.Name}>(...))");

    public HubEntry? FindByKey(string? key)
        => key == null ? null : this.Hubs.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));


    public Task<IReadOnlyList<IHubTransport>> Start(CancellationToken cancellationToken = default)
        => this.StartAll(null, cancellationToken);

    public Task<IReadOnlyList<IHubTransport>> Start(IEnumerable<Type> transports, CancellationToken cancellationToken = default)
        => this.StartAll(this.Resolve(transports), cancellationToken);

    Task<IReadOnlyList<IHubTransport>> StartAll(IReadOnlyList<IHubTransport>? transports, CancellationToken cancellationToken)
    {
        var hubs = this.Hubs;
        if (hubs.Count == 0)
            throw new InvalidOperationException("No hubs to start - add them with AddUniversalServer, or add dynamic hubs first");

        return this.StartHubs(hubs, transports, cancellationToken);
    }

    public Task Stop(string? reason = null)
        => this.StopHubs(this.Hubs, null, new HubDisconnect(HubDisconnectReason.ServerShutdown, reason));

    public Task Stop(IEnumerable<Type> transports, string? reason = null)
        => this.StopHubs(this.Hubs, this.Resolve(transports), new HubDisconnect(HubDisconnectReason.ServerShutdown, reason));

    public Task<IReadOnlyList<IHubTransport>> StartHub<THub>(CancellationToken cancellationToken = default) where THub : class
        => this.StartHubs([this.Get(typeof(THub))], null, cancellationToken);

    public Task<IReadOnlyList<IHubTransport>> StartHub<THub>(IEnumerable<Type> transports, CancellationToken cancellationToken = default) where THub : class
        => this.StartHubs([this.Get(typeof(THub))], this.Resolve(transports), cancellationToken);

    public Task StopHub<THub>(string? reason = null) where THub : class
        => this.StopHubs([this.Get(typeof(THub))], null, new HubDisconnect(HubDisconnectReason.ServerShutdown, reason ?? "Hub stopped"));

    public Task StopHub<THub>(IEnumerable<Type> transports, string? reason = null) where THub : class
        => this.StopHubs([this.Get(typeof(THub))], this.Resolve(transports), new HubDisconnect(HubDisconnectReason.ServerShutdown, reason ?? "Hub stopped"));

    public IReadOnlyList<IHubTransport> GetRunningTransports<THub>() where THub : class => this.RunningOn(this.Get(typeof(THub)));


    public IReadOnlyList<IHubTransport> RunningOn(IHubBinding hub) => this.Transports.Where(x => x.IsRunning(hub)).ToList();


    /// <summary>
    /// The transports set up on this server of the requested types - asking for one that isn't is a mistake
    /// </summary>
    public IReadOnlyList<IHubTransport> Resolve(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var wanted = types.Distinct().ToList();
        if (wanted.Count == 0)
            throw new ArgumentException("Pick at least one transport", nameof(types));

        var missing = wanted.FirstOrDefault(t => !this.Transports.Any(x => x.GetType() == t));
        if (missing != null)
            throw new ArgumentException($"{missing.Name} is not a transport set up on this server", nameof(types));

        return this.Transports.Where(x => wanted.Contains(x.GetType())).ToList();
    }


    /// <param name="transports">The transports to start on - null for every one</param>
    public async Task<IReadOnlyList<IHubTransport>> StartHubs(IReadOnlyList<HubEntry> hubs, IReadOnlyList<IHubTransport>? transports, CancellationToken cancellationToken)
    {
        var asked = transports ?? this.Transports;
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();
            var toStart = asked
                .Select(x => (Transport: x, Hubs: hubs.Where(h => h.UsesTransport(x) && !x.IsRunning(h)).ToList<IHubBinding>()))
                .Where(x => x.Hubs.Count > 0);

            foreach (var (transport, stopped) in toStart)
            {
                if (transport.IsSupported)
                {
                    try
                    {
                        await transport.Start(stopped, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // a transport this platform doesn't have is expected, not a problem
                        if (ex is PlatformNotSupportedException)
                            this.logger?.LogDebug("{Transport} hosting is not available here: {Message}", transport.DisplayName, ex.Message);
                        else
                            this.logger?.LogWarning(ex, "{Transport} hosting did not start", transport.DisplayName);
                        errors.Add(ex);
                    }
                }
                else
                {
                    this.logger?.LogDebug("{Transport} hosting is not available here", transport.DisplayName);
                    errors.Add(new PlatformNotSupportedException($"{transport.DisplayName} is not available on this device"));
                }
            }
            this.UpdateRunning(hubs);

            var running = asked.Where(x => hubs.Any(x.IsRunning)).ToList();
            if (running.Count == 0)
            {
                throw errors.Count switch
                {
                    // eg. Multipeer alone, when no hub was set up with it
                    0 => new InvalidOperationException($"None of {String.Join(", ", asked.Select(x => x.DisplayName))} is set up for these hubs"),
                    1 => errors[0],
                    _ => new AggregateException("No transport could start", errors)
                };
            }
            return running;
        }
        finally
        {
            this.gate.Release();
            this.RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }


    /// <param name="transports">The transports to stop on - null for every one</param>
    public async Task StopHubs(IReadOnlyList<HubEntry> hubs, IReadOnlyList<IHubTransport>? transports, HubDisconnect disconnect)
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var toStop = (transports ?? this.Transports)
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
                    this.logger?.LogWarning(ex, "{Transport} hosting did not stop cleanly", transport.DisplayName);
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
            this.RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }


    // IHubContext.IsRunning and the runtime's own check: running on any transport
    void UpdateRunning(IEnumerable<HubEntry> hubs)
    {
        foreach (var hub in hubs)
            hub.Runtime.IsRunning = this.Transports.Any(x => x.IsRunning(hub));
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
                    this.logger?.LogWarning(ex, "{Transport} could not re-advertise under the new name", transport.DisplayName);
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
    public HubEntry AddDynamic(string key, string name, HubOptions options, IHubHandler handler, IReadOnlySet<Type> transports, IReadOnlyDictionary<Type, object> settings)
    {
        lock (this.dynamicGate)
        {
            if (this.FindByKey(key) != null)
                throw new InvalidOperationException($"A hub named {key} already exists");

            var runtime = this.CreateRuntime(
                new HubDefinition(typeof(HandlerHub), key, options),
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
        await this.StopHubs([entry], null, disconnect).ConfigureAwait(false);
        await entry.Runtime.DisconnectAll(disconnect).ConfigureAwait(false);
        this.dynamic.TryRemove(new KeyValuePair<string, HubEntry>(entry.Key, entry));
        this.logger?.LogInformation("Removed hub {Hub}", entry.Name);
    }


    // ---- what the transports report ----

    internal void OnFileTransferred(HubFileTransferredEventArgs args) => this.FileTransferred?.Invoke(this, args);
    internal void OnFileProgress(HubFileProgressEventArgs args) => this.FileTransferProgress?.Invoke(this, args);


    HubRuntime CreateRuntime(HubDefinition registration, IHubDispatcher dispatcher, string name, Func<object>? createHub)
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
        };
        return runtime;
    }
}
