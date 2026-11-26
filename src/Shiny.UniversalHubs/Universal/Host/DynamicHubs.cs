using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Switchboard;
using IGroupManager = Shiny.BluetoothLE.Hubs.IGroupManager;

namespace Shiny.UniversalHubs;

/// <summary>
/// What a host lets dynamic hubs use - set once, in the app, because a platform fixes it when the app is built
/// (iOS lists every mDNS type in NSBonjourServices)
/// </summary>
public sealed class DynamicHubOptions
{
    /// <summary>
    /// The mDNS service types a dynamic hub may be advertised under. Empty allows only
    /// <see cref="HttpTransportOptions.ServiceType"/>. Hubs that share a type share one advertisement.
    /// </summary>
    public IList<string> ServiceTypes { get; } = new List<string>();

    /// <summary>
    /// How many dynamic hubs may exist at once. An mDNS TXT record lists about six hubs per service type.
    /// </summary>
    public int MaxHubs { get; set; } = 6;

    /// <summary>
    /// The GATT service dynamic hubs are characteristics in, when the server hosts over BLE - the compiled hubs' service
    /// unless set
    /// </summary>
    public string? BleServiceUuid { get; set; }
}


/// <summary>
/// A hub defined while the app runs
/// </summary>
/// <param name="Name">Unique among dynamic hubs; names the hub in logs and errors</param>
/// <param name="CharacteristicUuid">The BLE characteristic - one per hub, also its HTTP path. Clients use the same one.</param>
/// <param name="ServiceType">The mDNS service type, one of <see cref="DynamicHubOptions.ServiceTypes"/></param>
public sealed record DynamicHubDefinition(string Name, string CharacteristicUuid, string ServiceType)
{
    /// <summary>
    /// Counted across every transport; null for no limit
    /// </summary>
    public int? MaxClients { get; init; } = 8;

    /// <summary>
    /// Let clients reach this hub through a tunnel (the public internet)
    /// </summary>
    public bool AllowTunnel { get; init; }
}


public enum DynamicHubMethodKind
{
    NotFound,
    Invoke,
    Stream
}


/// <summary>
/// Runs a dynamic hub: its methods by name, and its clients coming and going. Values are the bytes the caller's
/// IBleHubSerializer wrote - JSON with the default serializer, which Wi-Fi clients need anyway.
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
    /// A client renamed itself (<see cref="BleHubConnectedClient.Name"/> is the new name). Throw to refuse.
    /// </summary>
    Task OnRenamedAsync(HubConnection connection, string? previousName) => Task.CompletedTask;
}


/// <summary>
/// A call into a dynamic hub
/// </summary>
public sealed class DynamicHubCall
{
    readonly BleHubCallerContext context;

    internal DynamicHubCall(BleHubCallerContext context, string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments)
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
    readonly BleHubPush<IBleHubHandler> push;

    internal DynamicHubPush(BleHubPush<IBleHubHandler> push) => this.push = push;

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
    IServiceProvider services,
    UniversalHubHost host,
    DynamicHubOptions options,
    ILoggerFactory? loggerFactory = null
) : IDynamicHubs
{
    readonly ConcurrentDictionary<string, DynamicHub> hubs = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<HubEntry, DynamicHttpHub> httpHubs = new();
    readonly object sync = new();


    public IReadOnlyList<IDynamicHub> Hubs => this.hubs.Values.ToList();

    public IDynamicHub? Find(string name) => this.hubs.TryGetValue(name, out var hub) ? hub : null;

    DynamicHub? FindByKey(string? key)
        => key == null ? null : this.hubs.Values.FirstOrDefault(x => x.Entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));


    public IDynamicHub Add(DynamicHubDefinition definition, IDynamicHubHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        UniversalHubProtocol.ValidateServiceType(definition.ServiceType);
        var characteristic = BleUuid.Normalize(definition.CharacteristicUuid, nameof(definition.CharacteristicUuid));

        var httpOptions = services.GetService<HttpTransportOptions>();
        IList<string> allowed = options.ServiceTypes.Count == 0 ? [httpOptions?.ServiceType ?? UniversalHubProtocol.ServiceType] : options.ServiceTypes;
        if (!allowed.Contains(definition.ServiceType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{definition.ServiceType} isn't one of this host's dynamic hub service types ({String.Join(", ", allowed)})");

        lock (this.sync)
        {
            if (this.hubs.ContainsKey(definition.Name))
                throw new InvalidOperationException($"A dynamic hub named {definition.Name} already exists");
            if (this.hubs.Count >= options.MaxHubs)
                throw new InvalidOperationException($"This host allows at most {options.MaxHubs} dynamic hubs");
            if (host.Hubs.Any(x => x.GetSettings<BleHubSettings>()?.CharacteristicUuid.Equals(characteristic, StringComparison.OrdinalIgnoreCase) == true))
                throw new InvalidOperationException($"Characteristic {characteristic} is already used by another hub - each hub needs its own");

            // every transport the server has
            var transports = host.Transports.Select(x => x.GetType()).ToHashSet();
            var bleService = options.BleServiceUuid
                ?? host.Hubs.Select(x => x.GetSettings<BleHubSettings>()?.ServiceUuid).FirstOrDefault(x => x != null)
                ?? BleHubProtocolOptions.DefaultServiceUuid;
            var settings = new Dictionary<Type, object>
            {
                [typeof(BleHubSettings)] = new BleHubSettings(BleUuid.Normalize(bleService, nameof(options.BleServiceUuid)), characteristic),
                [typeof(HttpHubSettings)] = new HttpHubSettings(definition.ServiceType, definition.AllowTunnel)
            };
            if (!transports.Contains(typeof(BleHostTransport)))
                settings.Remove(typeof(BleHubSettings));
            if (!transports.Contains(typeof(HttpHostTransport)))
                settings.Remove(typeof(HttpHubSettings));

            // the characteristic stays the hub's key for now - it is how dynamic clients ask for it
            var entry = host.AddDynamic(
                characteristic,
                definition.Name,
                new BleHubOptions { MaxClients = definition.MaxClients },
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
        this.httpHubs.TryRemove(hub.Entry, out _);
    }


    // ---- HTTP: the Switchboard every dynamic hub is reached through, at {BasePath}/{key} ----

    const string LineHub = "Shiny.UniversalHubs.DynamicHub";


    /// <summary>
    /// The Switchboard side of a dynamic hub, made the first time HTTP needs it
    /// </summary>
    internal HttpHub? FindHttp(IHubBinding hub)
        => hub is HubEntry entry && this.hubs.Values.Any(x => ReferenceEquals(x.Entry, entry)) ? this.GetHttp(entry) : null;


    DynamicHttpHub GetHttp(HubEntry entry) => this.httpHubs.GetOrAdd(entry, e => new DynamicHttpHub(
        e,
        host,
        services.GetRequiredService<HttpHostTransport>(),
        services.GetRequiredService<FileTokens>(),
        services.GetRequiredService<MediaTokens>(),
        services.GetRequiredService<IOperator<DynamicHubBoard>>(),
        (ILogger?)loggerFactory?.CreateLogger($"Shiny.UniversalHubs.{e.Name}") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
    ));


    public ValueTask Handshake(Switchboard board, SwitchboardInvocation call)
    {
        var requested = call.Caller.HttpContext?.Request.RouteValues[DynamicHubBoard.RouteParameter] as string;
        if (this.FindByKey(requested) is not { } hub)
            return call.ReturnAsync(HubJson.Serialize(new HubHandshakeReply(false, "Hub not found", null, null, null)));

        var http = this.GetHttp(hub.Entry);
        if (board.Context.Items.TryGetValue(LineHub, out var existing) && !ReferenceEquals(existing, http))
            return call.ReturnAsync(HubJson.Serialize(new HubHandshakeReply(false, "This line belongs to another hub", null, null, null)));

        board.Context.Items[LineHub] = http;
        http.TrackLine(board.Context.LineId);
        return http.Handshake(board, call);
    }


    public ValueTask Invoke(Switchboard board, SwitchboardInvocation call)
        => this.LineHttp(board) is { } http
            ? http.Invoke(board, call)
            : call.ReturnAsync(HubJson.Error(nameof(BleHubProtocolException), "Handshake required"));


    public ValueTask Stream(Switchboard board, SwitchboardInvocation call)
        => this.LineHttp(board) is { } http
            ? http.Stream(board, call)
            : call.StreamAsync(One(HubJson.Error(nameof(BleHubProtocolException), "Handshake required")));


    public void LineGone(Switchboard board, HubDisconnect disconnect)
    {
        if (this.LineHttp(board) is { } http)
            http.LineGone(board.Context.LineId, disconnect);
    }


    DynamicHttpHub? LineHttp(Switchboard board)
        => board.Context.Items.TryGetValue(LineHub, out var value) && value is DynamicHttpHub http && this.httpHubs.Values.Contains(http)
            ? http
            : null;


    static async IAsyncEnumerable<System.Text.Json.JsonElement> One(System.Text.Json.JsonElement item)
    {
        await Task.CompletedTask;
        yield return item;
    }


    sealed class HandlerAdapter(IDynamicHubHandler handler) : IBleHubHandler
    {
        public BleHubMethodKind GetMethodKind(string method) => handler.GetMethodKind(method) switch
        {
            DynamicHubMethodKind.Invoke => BleHubMethodKind.Invoke,
            DynamicHubMethodKind.Stream => BleHubMethodKind.Stream,
            _ => BleHubMethodKind.NotFound
        };

        public Task<byte[]?> Invoke(BleHubHandlerCall call, CancellationToken cancellationToken)
            => handler.Invoke(new DynamicHubCall(call.Context, call.Method, call.Arguments), cancellationToken);

        public async IAsyncEnumerable<byte[]> Stream(BleHubHandlerCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in handler.Stream(new DynamicHubCall(call.Context, call.Method, call.Arguments), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }

        public Task OnConnectedAsync(BleHubCallerContext context) => handler.OnConnectedAsync(HubConnection.From(context.Client));

        public Task OnDisconnectedAsync(BleHubCallerContext context, HubDisconnect disconnect)
        {
            // the line's requests are over - nothing current to show, whatever the async flow still holds
            HttpCall.Current = null;
            return handler.OnDisconnectedAsync(HubConnection.From(context.Client), disconnect);
        }

        public Task OnRenamedAsync(BleHubCallerContext context, string? previousName)
            => handler.OnRenamedAsync(HubConnection.From(context.Client), previousName);
    }
}


/// <summary>
/// The one Switchboard every dynamic hub is reached through - the route says which hub
/// </summary>
internal sealed class DynamicHubBoard(DynamicHubRegistry registry) : Switchboard
{
    public const string RouteParameter = "hub";

    public DynamicHubRegistry Registry => registry;

    public override Task OnDisconnectedAsync(DisconnectContext disconnect)
    {
        registry.LineGone(this, HubLines.ToHubDisconnect(disconnect));
        return Task.CompletedTask;
    }


    public static void Register()
    {
        if (SwitchboardRegistry.TryGet(typeof(DynamicHubBoard), out _))
            return;

        SwitchboardRegistry.Register(new SwitchboardDescriptor(
            typeof(DynamicHubBoard),
            sp => new DynamicHubBoard(sp.GetRequiredService<DynamicHubRegistry>()),
            [],
            [
                new SwitchboardMethod(UniversalHubProtocol.HandshakeMethod, 1, 1, [], (board, call) => ((DynamicHubBoard)board).Registry.Handshake(board, call)),
                new SwitchboardMethod(UniversalHubProtocol.InvokeMethod, 2, 2, [], (board, call) => ((DynamicHubBoard)board).Registry.Invoke(board, call)),
                new SwitchboardMethod(UniversalHubProtocol.StreamMethod, 2, 2, [], (board, call) => ((DynamicHubBoard)board).Registry.Stream(board, call))
            ]
        ));
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
        new HubClients<BleHubPush<IBleHubHandler>>(entry.Runtime, target => new BleHubPush<IBleHubHandler>(target, entry.Runtime.Serializer))
    );

    public Task Disconnect(string connectionId, string? reason = null)
        => entry.Runtime.Disconnect(connectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, reason));


    sealed class PushClients(IHubClients<BleHubPush<IBleHubHandler>> inner) : IHubClients<DynamicHubPush>
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


/// <summary>
/// A dynamic hub served over HTTP - its lines share the one dynamic Switchboard, so it keeps track of its own
/// </summary>
internal sealed class DynamicHttpHub : HttpHub
{
    readonly IOperator<DynamicHubBoard> board;
    readonly ConcurrentDictionary<string, byte> lines = new(StringComparer.Ordinal);


    public DynamicHttpHub(
        HubEntry entry,
        UniversalHubHost host,
        HttpHostTransport transport,
        FileTokens fileTokens,
        MediaTokens mediaTokens,
        IOperator<DynamicHubBoard> board,
        ILogger logger
    ) : base(host, transport, fileTokens, mediaTokens, logger)
    {
        this.Binding = entry;
        this.board = board;
        entry.Runtime.ClientDisconnected += (_, e) =>
        {
            if (e.Client.Id.StartsWith(HubConnection.HttpPrefix, StringComparison.Ordinal))
                this.lines.TryRemove(e.Client.Id[HubConnection.HttpPrefix.Length..], out byte _);
        };
    }


    public override IHubBinding Binding { get; }

    public void TrackLine(string lineId) => this.lines[lineId] = 0;

    public override Task HangUpAll(HubDisconnect disconnect)
    {
        var message = HubJson.HangUpMessage(disconnect);
        var ids = this.lines.Keys.ToList();
        this.lines.Clear();
        return Task.WhenAll(ids.Select(x => this.board.Lines.HangUpAsync(x, message, allowReconnect: false)));
    }

    protected override IBleHubPeerChannel CreateChannel(string lineId) => new LineChannel(this.board.Clients, this.board.Lines, lineId, this.Transport);
}
