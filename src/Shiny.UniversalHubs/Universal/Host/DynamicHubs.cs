using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IGroupManager = Shiny.UniversalHubs.IGroupManager;

namespace Shiny.UniversalHubs;

/// <summary>
/// What a host lets dynamic hubs use - set once, in the app. Each transport has its own say too (eg. the mDNS service types
/// in HttpTransportOptions.DynamicServiceTypes, the GATT service in BluetoothLETransportOptions.DynamicServiceUuid).
/// </summary>
public sealed class DynamicHubOptions
{
    /// <summary>
    /// How many dynamic hubs may exist at once. An mDNS TXT record lists about six hubs per service type.
    /// </summary>
    public int MaxHubs { get; set; } = 6;
}


/// <summary>
/// A hub defined while the app runs. It is served over every transport the server has set up that can serve it: one that
/// needs per-hub settings (a BLE characteristic) only when they are given - <c>definition.UseBluetoothLE(...)</c> - and
/// one with defaults (HTTP's service type) either way.
/// </summary>
/// <param name="name">The hub's name - how clients ask for it on every transport, unique among all hubs</param>
public sealed class DynamicHubDefinition(string name)
{
    readonly Dictionary<Type, object> settings = new();

    public string Name { get; } = name;

    /// <summary>
    /// Counted across every transport; null for no limit
    /// </summary>
    public int? MaxClients { get; init; } = 8;

    /// <summary>
    /// What a transport is told about this hub, by the settings' type - transports add theirs with extension methods
    /// (<c>UseHttp</c>, <c>UseBluetoothLE</c>)
    /// </summary>
    public DynamicHubDefinition With(object transportSettings)
    {
        ArgumentNullException.ThrowIfNull(transportSettings);
        this.settings[transportSettings.GetType()] = transportSettings;
        return this;
    }

    public T? GetSettings<T>() where T : class => this.settings.TryGetValue(typeof(T), out var value) ? (T)value : null;
}


/// <summary>
/// A transport that can serve hubs added while the app runs. One that doesn't implement this serves them with no per-hub
/// settings.
/// </summary>
public interface IDynamicHubTransport : IHubTransport
{
    /// <summary>
    /// What this transport needs to know about the new hub - null to leave it off this transport. Throw to refuse the hub
    /// (a setting this host doesn't allow, a clash with another hub).
    /// </summary>
    object? CreateSettings(DynamicHubDefinition definition);
}


public enum DynamicHubMethodKind
{
    NotFound,
    Invoke,
    Stream
}


/// <summary>
/// Runs a dynamic hub: its methods by name, and its clients coming and going. Values are the bytes the caller's
/// IHubSerializer wrote - JSON with the default serializer, which Wi-Fi clients need anyway.
/// </summary>
public interface IDynamicHubHandler
{
    DynamicHubMethodKind GetMethodKind(string method);

    /// <summary>
    /// Runs a method; returns its serialized result, or null for none. Throw to fail the call - the caller gets the
    /// exception's type name and message.
    /// </summary>
    Task<byte[]?> Invoke(DynamicHubCall call, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a streaming method, each item serialized
    /// </summary>
    IAsyncEnumerable<byte[]> Stream(DynamicHubCall call, CancellationToken cancellationToken);

    /// <summary>
    /// A client completed its handshake, over any transport. It isn't told it is in until this returns - throw to refuse it
    /// with the exception's message.
    /// </summary>
    Task OnConnectedAsync(HubConnection connection);

    /// <summary>
    /// A client left. Group membership is still intact.
    /// </summary>
    Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect);

    /// <summary>
    /// A client renamed itself (<see cref="HubConnectedClient.Name"/> is the new name). Throw to refuse.
    /// </summary>
    Task OnRenamedAsync(HubConnection connection, string? previousName) => Task.CompletedTask;
}


/// <summary>
/// A call into a dynamic hub
/// </summary>
public sealed class DynamicHubCall
{
    readonly HubCallerContext context;

    internal DynamicHubCall(HubCallerContext context, string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments)
    {
        this.context = context;
        this.Method = method;
        this.Arguments = arguments;
        this.Connection = HubConnection.From(context.Client);
    }

    /// <summary>
    /// The caller, and the transport it is on
    /// </summary>
    public HubConnection Connection { get; }
    public string Method { get; }

    /// <summary>
    /// Each argument exactly as the caller serialized it
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Arguments { get; }

    /// <summary>
    /// Cancelled when the caller goes away
    /// </summary>
    public CancellationToken ConnectionAborted => this.context.ConnectionAborted;

    /// <summary>
    /// Ends the caller's session once this call's reply has been sent
    /// </summary>
    public void Abort(string? reason = null) => this.context.Abort(reason);
}


/// <summary>
/// A dynamic hub: started, stopped and pushed to like a compiled one
/// </summary>
public interface IDynamicHub
{
    DynamicHubDefinition Definition { get; }

    /// <summary>
    /// The transports this hub is accepting clients on
    /// </summary>
    IReadOnlyList<IHubTransport> RunningTransports { get; }

    /// <summary>
    /// Starts this hub on every transport the server has, like IUniversalHubHost.StartHub - a transport that can't start is
    /// skipped as long as another does
    /// </summary>
    Task<IReadOnlyList<IHubTransport>> Start(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts this hub on <paramref name="transports"/> only - the types of transports set up on the server
    /// </summary>
    Task<IReadOnlyList<IHubTransport>> Start(IEnumerable<Type> transports, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops this hub on every transport; its clients are told why
    /// </summary>
    Task Stop(string? reason = null);

    IReadOnlyList<HubConnection> Connections { get; }
    IGroupManager Groups { get; }

    /// <summary>
    /// Push targets - <c>hub.Clients.All.Send("Chat", [json])</c>, with each argument already serialized
    /// </summary>
    IHubClients<DynamicHubPush> Clients { get; }

    /// <summary>
    /// Ends a client's session, telling it why
    /// </summary>
    Task Disconnect(string connectionId, string? reason = null);
}


/// <summary>
/// A resolved set of a dynamic hub's clients
/// </summary>
public readonly struct DynamicHubPush
{
    readonly HubPush<IHubHandler> push;

    internal DynamicHubPush(HubPush<IHubHandler> push) => this.push = push;

    /// <summary>
    /// Raises <paramref name="eventName"/> on each client, with arguments already serialized
    /// </summary>
    public Task Send(string eventName, IReadOnlyList<ReadOnlyMemory<byte>> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(arguments);

        var writer = this.push.CreateArguments();
        foreach (var argument in arguments)
            writer.WriteSerialized(argument.Span);

        return this.push.Send(eventName, writer, cancellationToken);
    }
}


/// <summary>
/// Adds, finds and removes dynamic hubs: hubs defined while the app runs (by a downloaded web app, say), served over
/// every transport beside the compiled ones. Turn them on with <c>AddUniversalServer(server => server.AllowDynamicHubs(...))</c>.
/// </summary>
public interface IDynamicHubs
{
    IReadOnlyList<IDynamicHub> Hubs { get; }

    /// <summary>
    /// A dynamic hub by name, null when there is none
    /// </summary>
    IDynamicHub? Find(string name);

    /// <summary>
    /// Adds a hub. It doesn't start until <see cref="IDynamicHub.Start"/>. Throws when the name or characteristic is taken,
    /// the service type isn't allowed, or <see cref="DynamicHubOptions.MaxHubs"/> are already added.
    /// </summary>
    IDynamicHub Add(DynamicHubDefinition definition, IDynamicHubHandler handler);

    /// <summary>
    /// Stops a hub on every transport (its clients are told why) and forgets it
    /// </summary>
    Task Remove(string name, string? reason = null);
}


/// <summary>
/// The dynamic hubs: each one a hub of the host's, served over every transport the server has set up
/// </summary>
internal sealed class DynamicHubRegistry(
    UniversalHubHost host,
    DynamicHubOptions options,
    ILoggerFactory? loggerFactory = null
) : IDynamicHubs
{
    readonly ConcurrentDictionary<string, DynamicHub> hubs = new(StringComparer.OrdinalIgnoreCase);
    readonly object sync = new();


    public IReadOnlyList<IDynamicHub> Hubs => this.hubs.Values.ToList();

    public IDynamicHub? Find(string name) => this.hubs.TryGetValue(name, out var hub) ? hub : null;

    public IDynamicHub Add(DynamicHubDefinition definition, IDynamicHubHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        UniversalServerExtensions.ValidateHubName(definition.Name);

        lock (this.sync)
        {
            if (this.hubs.Count >= options.MaxHubs)
                throw new InvalidOperationException($"This host allows at most {options.MaxHubs} dynamic hubs");
            if (host.FindByKey(definition.Name) != null)
                throw new InvalidOperationException($"A hub named {definition.Name} already exists");

            // each transport decides whether, and how, it serves the hub
            var transports = new HashSet<Type>();
            var settings = new Dictionary<Type, object>();
            foreach (var transport in host.Transports)
            {
                if (transport is IDynamicHubTransport dynamic)
                {
                    if (dynamic.CreateSettings(definition) is { } set)
                    {
                        transports.Add(transport.GetType());
                        settings[set.GetType()] = set;
                    }
                }
                else
                {
                    transports.Add(transport.GetType());
                }
            }
            if (transports.Count == 0)
                throw new InvalidOperationException($"No transport on this server can serve {definition.Name}");

            var entry = host.AddDynamic(
                definition.Name,
                definition.Name,
                new HubOptions { MaxClients = definition.MaxClients },
                new HandlerAdapter(handler),
                transports,
                settings
            );
            var hub = new DynamicHub(definition, entry, host);
            this.hubs[definition.Name] = hub;
            return hub;
        }
    }


    public async Task Remove(string name, string? reason = null)
    {
        if (!this.hubs.TryGetValue(name, out var hub))
            throw new InvalidOperationException($"There is no dynamic hub named {name}");

        await host.RemoveDynamic(hub.Entry, reason).ConfigureAwait(false);
        this.hubs.TryRemove(name, out _);
    }


    sealed class HandlerAdapter(IDynamicHubHandler handler) : IHubHandler
    {
        public HubMethodKind GetMethodKind(string method) => handler.GetMethodKind(method) switch
        {
            DynamicHubMethodKind.Invoke => HubMethodKind.Invoke,
            DynamicHubMethodKind.Stream => HubMethodKind.Stream,
            _ => HubMethodKind.NotFound
        };

        public Task<byte[]?> Invoke(HubHandlerCall call, CancellationToken cancellationToken)
            => handler.Invoke(new DynamicHubCall(call.Context, call.Method, call.Arguments), cancellationToken);

        public async IAsyncEnumerable<byte[]> Stream(HubHandlerCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in handler.Stream(new DynamicHubCall(call.Context, call.Method, call.Arguments), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }

        public Task OnConnectedAsync(HubCallerContext context) => handler.OnConnectedAsync(HubConnection.From(context.Client));

        public Task OnDisconnectedAsync(HubCallerContext context, HubDisconnect disconnect)
            => handler.OnDisconnectedAsync(HubConnection.From(context.Client), disconnect);

        public Task OnRenamedAsync(HubCallerContext context, string? previousName)
            => handler.OnRenamedAsync(HubConnection.From(context.Client), previousName);
    }
}


/// <summary>
/// A dynamic hub, as the app sees it
/// </summary>
internal sealed class DynamicHub(DynamicHubDefinition definition, HubEntry entry, UniversalHubHost host) : IDynamicHub
{
    public DynamicHubDefinition Definition => definition;
    public HubEntry Entry => entry;

    public IReadOnlyList<IHubTransport> RunningTransports => host.RunningOn(entry);

    public Task<IReadOnlyList<IHubTransport>> Start(CancellationToken cancellationToken = default)
        => host.StartHubs([entry], null, cancellationToken);

    public Task<IReadOnlyList<IHubTransport>> Start(IEnumerable<Type> transports, CancellationToken cancellationToken = default)
        => host.StartHubs([entry], host.Resolve(transports), cancellationToken);

    public Task Stop(string? reason = null)
        => host.StopHubs([entry], null, new HubDisconnect(HubDisconnectReason.ServerShutdown, reason ?? "Hub stopped"));

    public IReadOnlyList<HubConnection> Connections => entry.Runtime.Clients.Select(HubConnection.From).ToList();
    public IGroupManager Groups => entry.Runtime;

    public IHubClients<DynamicHubPush> Clients => new PushClients(
        new HubClients<HubPush<IHubHandler>>(entry.Runtime, target => new HubPush<IHubHandler>(target, entry.Runtime.Serializer))
    );

    public Task Disconnect(string connectionId, string? reason = null)
        => entry.Runtime.Disconnect(connectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, reason));


    sealed class PushClients(IHubClients<HubPush<IHubHandler>> inner) : IHubClients<DynamicHubPush>
    {
        public DynamicHubPush All => new(inner.All);
        public DynamicHubPush AllExcept(params IEnumerable<string> excludedConnectionIds) => new(inner.AllExcept(excludedConnectionIds));
        public DynamicHubPush Client(string connectionId) => new(inner.Client(connectionId));
        public DynamicHubPush Clients(params IEnumerable<string> connectionIds) => new(inner.Clients(connectionIds));
        public DynamicHubPush Group(string groupName) => new(inner.Group(groupName));
        public DynamicHubPush Groups(params IEnumerable<string> groupNames) => new(inner.Groups(groupNames));
        public DynamicHubPush GroupExcept(string groupName, params IEnumerable<string> excludedConnectionIds) => new(inner.GroupExcept(groupName, excludedConnectionIds));
    }
}
