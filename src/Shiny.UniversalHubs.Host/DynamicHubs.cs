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
    /// <see cref="UniversalHubHostOptions.ServiceType"/>. Hubs that share a type share one advertisement.
    /// </summary>
    public IList<string> ServiceTypes { get; } = new List<string>();

    /// <summary>
    /// How many dynamic hubs may exist at once. An mDNS TXT record lists about six hubs per service type.
    /// </summary>
    public int MaxHubs { get; set; } = 6;
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
    HubTransports RunningTransports { get; }

    /// <summary>
    /// Starts this hub, like IUniversalHubHost.StartHub - a transport that can't start is skipped as long as another does
    /// </summary>
    Task<HubTransports> Start(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default);

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
/// The dynamic hubs and the one Switchboard their Wi-Fi clients reach them through
/// </summary>
internal sealed class DynamicHubRegistry(
    IServiceProvider services,
    IBleHubHandlerHost bleHost,
    UniversalHubHost host,
    UniversalHubHostOptions hostOptions,
    DynamicHubOptions options,
    FileTokens fileTokens,
    MediaTokens mediaTokens,
    ILoggerFactory? loggerFactory = null
) : IDynamicHubs
{
    readonly ConcurrentDictionary<string, DynamicHubBinding> hubs = new(StringComparer.OrdinalIgnoreCase);
    readonly object sync = new();


    public IReadOnlyList<IDynamicHub> Hubs => this.hubs.Values.ToList();
    internal IReadOnlyList<IHubBinding> Bindings => this.hubs.Values.ToList();

    public IDynamicHub? Find(string name) => this.hubs.TryGetValue(name, out var hub) ? hub : null;

    internal DynamicHubBinding? FindByCharacteristic(string? characteristicUuid)
        => characteristicUuid == null ? null : this.hubs.Values.FirstOrDefault(x => x.CharacteristicUuid.Equals(characteristicUuid, StringComparison.OrdinalIgnoreCase));


    public IDynamicHub Add(DynamicHubDefinition definition, IDynamicHubHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        UniversalHubProtocol.ValidateServiceType(definition.ServiceType);

        IList<string> allowed = options.ServiceTypes.Count == 0 ? [hostOptions.ServiceType] : options.ServiceTypes;
        if (!allowed.Contains(definition.ServiceType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{definition.ServiceType} isn't one of this host's dynamic hub service types ({String.Join(", ", allowed)})");

        lock (this.sync)
        {
            if (this.hubs.ContainsKey(definition.Name))
                throw new InvalidOperationException($"A dynamic hub named {definition.Name} already exists");
            if (this.hubs.Count >= options.MaxHubs)
                throw new InvalidOperationException($"This host allows at most {options.MaxHubs} dynamic hubs");
            if (host.Hubs.Any(x => x.CharacteristicUuid.Equals(definition.CharacteristicUuid, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Characteristic {definition.CharacteristicUuid} is already used by another hub - each hub needs its own");

            var handle = bleHost.Add(
                definition.CharacteristicUuid,
                definition.Name,
                new BleHubOptions { MaxClients = definition.MaxClients },
                new HandlerAdapter(handler)
            );
            handle.ClientDisconnected += (_, e) => host.ForgetClient(e.Client.Id);

            var binding = new DynamicHubBinding(
                definition,
                handle,
                host,
                fileTokens,
                mediaTokens,
                services.GetRequiredService<IOperator<DynamicHubBoard>>(),
                (ILogger?)loggerFactory?.CreateLogger($"Shiny.UniversalHubs.{definition.Name}") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            );
            this.hubs[definition.Name] = binding;
            return binding;
        }
    }


    public async Task Remove(string name, string? reason = null)
    {
        if (!this.hubs.TryGetValue(name, out var binding))
            throw new InvalidOperationException($"There is no dynamic hub named {name}");

        await binding.Stop(reason ?? "Hub removed").ConfigureAwait(false);
        await bleHost.Remove(binding.CharacteristicUuid, reason).ConfigureAwait(false);
        this.hubs.TryRemove(name, out _);
    }


    // ---- the Switchboard every dynamic hub is reached through, at {BasePath}/{characteristicUuid} ----

    /// <summary>
    /// The line's hub, which its handshake chose
    /// </summary>
    const string LineHub = "Shiny.UniversalHubs.DynamicHub";


    public ValueTask Handshake(Switchboard board, SwitchboardInvocation call)
    {
        var requested = call.Caller.HttpContext?.Request.RouteValues[DynamicHubBoard.RouteParameter] as string;
        var binding = this.FindByCharacteristic(requested);
        if (binding == null)
            return call.ReturnAsync(HubJson.Serialize(new HubHandshakeReply(false, "Hub not found", null, null, null)));

        if (board.Context.Items.TryGetValue(LineHub, out var existing) && !ReferenceEquals(existing, binding))
            return call.ReturnAsync(HubJson.Serialize(new HubHandshakeReply(false, "This line belongs to another hub", null, null, null)));

        board.Context.Items[LineHub] = binding;
        binding.TrackLine(board.Context.LineId);
        return binding.Handshake(board, call);
    }


    public ValueTask Invoke(Switchboard board, SwitchboardInvocation call)
        => this.LineBinding(board) is { } binding
            ? binding.Invoke(board, call)
            : call.ReturnAsync(HubJson.Error(nameof(BleHubProtocolException), "Handshake required"));


    public ValueTask Stream(Switchboard board, SwitchboardInvocation call)
        => this.LineBinding(board) is { } binding
            ? binding.Stream(board, call)
            : call.StreamAsync(One(HubJson.Error(nameof(BleHubProtocolException), "Handshake required")));


    public void LineGone(Switchboard board, HubDisconnect disconnect)
    {
        if (this.LineBinding(board) is { } binding)
            binding.LineGone(board.Context.LineId, disconnect);
    }


    DynamicHubBinding? LineBinding(Switchboard board)
        => board.Context.Items.TryGetValue(LineHub, out var value) && value is DynamicHubBinding binding && this.hubs.Values.Contains(binding)
            ? binding
            : null;


    static async IAsyncEnumerable<System.Text.Json.JsonElement> One(System.Text.Json.JsonElement item)
    {
        await Task.CompletedTask;
        yield return item;
    }


    /// <summary>
    /// Presents a dynamic hub's handler to blehubs, with UniversalHubs' view of each connection
    /// </summary>
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
/// The one Switchboard dynamic hubs share. It is mapped at <c>{BasePath}/{hub}</c>, behind the compiled hubs' own routes,
/// and each line's handshake picks its hub - so clients reach a dynamic hub at the same path as a compiled one.
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
/// One dynamic hub on every transport: its BLE handle, and its lines on the shared Switchboard
/// </summary>
internal sealed class DynamicHubBinding : HubBindingBase, IDynamicHub
{
    readonly IBleHubHandle handle;
    readonly UniversalHubHost host;
    readonly IOperator<DynamicHubBoard> board;
    readonly ConcurrentDictionary<string, byte> lines = new(StringComparer.Ordinal);


    public DynamicHubBinding(
        DynamicHubDefinition definition,
        IBleHubHandle handle,
        UniversalHubHost host,
        FileTokens fileTokens,
        MediaTokens mediaTokens,
        IOperator<DynamicHubBoard> board,
        ILogger logger
    ) : base(host, fileTokens, mediaTokens, definition.ServiceType, definition.AllowTunnel, logger)
    {
        this.Definition = definition;
        this.handle = handle;
        this.host = host;
        this.board = board;
        handle.ClientDisconnected += (_, e) =>
        {
            if (HubConnection.GetTransport(e.Client.Id) == HubTransports.Http)
                this.lines.TryRemove(e.Client.Id[HubConnection.HttpPrefix.Length..], out byte _);
        };
    }


    public DynamicHubDefinition Definition { get; }
    public override IBleHubTransportEndpoint Endpoint => this.handle.TransportEndpoint;
    public override Type? HubType => null;
    public override string Name => this.Definition.Name;

    public override bool IsBleRunning => this.handle.IsRunning;
    public override Task StartBle(CancellationToken cancellationToken) => this.handle.Start(cancellationToken);
    public override Task StopBle(string? reason) => this.handle.Stop(reason);

    public void TrackLine(string lineId) => this.lines[lineId] = 0;

    public override Task HangUpAll(HubDisconnect disconnect)
    {
        var message = HubJson.HangUpMessage(disconnect);
        var ids = this.lines.Keys.ToList();
        this.lines.Clear();
        return Task.WhenAll(ids.Select(x => this.board.Lines.HangUpAsync(x, message, allowReconnect: false)));
    }

    protected override IBleHubPeerChannel CreateChannel(string lineId) => new LineChannel(this.board.Clients, this.board.Lines, lineId);


    // ---- IDynamicHub ----

    public HubTransports RunningTransports => this.host.RunningOn(this);

    public Task<HubTransports> Start(HubTransports transports = HubTransports.All, CancellationToken cancellationToken = default)
        => this.host.StartHubs([this], transports, cancellationToken);

    public Task Stop(string? reason = null) => this.host.StopHubs([this], reason, all: false);

    public IReadOnlyList<HubConnection> Connections => this.handle.ConnectedClients.Select(HubConnection.From).ToList();
    public IGroupManager Groups => this.handle.Groups;
    public IHubClients<DynamicHubPush> Clients => new PushClients(this.handle.Clients);
    public Task Disconnect(string connectionId, string? reason = null) => this.handle.Disconnect(connectionId, reason);


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
