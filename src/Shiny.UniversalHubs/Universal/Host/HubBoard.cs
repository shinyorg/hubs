using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;
using Shiny.Net.HttpServer.Switchboard;

namespace Shiny.UniversalHubs;

/// <summary>
/// The Switchboard a hub is served through. It has no methods of its own: the hub's methods are reached through the
/// reserved hub.* methods registered in <see cref="HubBoardRegistration"/>, and run by the generated BLE hub dispatcher.
/// Generic, so the Switchboard source generator leaves it alone.
/// </summary>
internal sealed class HubBoard<THub>(HubBinding<THub> binding) : Switchboard where THub : class
{
    public HubBinding<THub> Binding => binding;

    public override Task OnDisconnectedAsync(DisconnectContext disconnect)
    {
        binding.LineGone(this.Context.LineId, HubLines.ToHubDisconnect(disconnect));
        return Task.CompletedTask;
    }
}


internal static class HubLines
{
    /// <summary>
    /// A hang-up this host made carries its own reason; everything else maps from how the line closed
    /// </summary>
    internal static HubDisconnect ToHubDisconnect(DisconnectContext disconnect)
        => HubJson.ReadHangUp(disconnect.Message) ?? new HubDisconnect(disconnect.Reason switch
        {
            DisconnectReason.ClientClosed => HubDisconnectReason.ClientDisconnect,
            DisconnectReason.Dropped => HubDisconnectReason.ClientTimeout,
            DisconnectReason.HungUp => HubDisconnectReason.ServerDisconnect,
            DisconnectReason.ServerShutdown => HubDisconnectReason.ServerShutdown,
            _ => HubDisconnectReason.ConnectionFailed
        }, disconnect.Message);
}


internal static class HubBoardRegistration
{
    public static void Register<THub>() where THub : class
    {
        if (SwitchboardRegistry.TryGet(typeof(HubBoard<THub>), out _))
            return;

        SwitchboardRegistry.Register(new SwitchboardDescriptor(
            typeof(HubBoard<THub>),
            sp => new HubBoard<THub>(sp.GetRequiredService<HubBinding<THub>>()),
            [],
            [
                new SwitchboardMethod(UniversalHubProtocol.HandshakeMethod, 1, 1, [], (board, call) => ((HubBoard<THub>)board).Binding.Handshake(board, call)),
                new SwitchboardMethod(UniversalHubProtocol.InvokeMethod, 2, 2, [], (board, call) => ((HubBoard<THub>)board).Binding.Invoke(board, call)),
                new SwitchboardMethod(UniversalHubProtocol.StreamMethod, 2, 2, [], (board, call) => ((HubBoard<THub>)board).Binding.Stream(board, call))
            ]
        ));
    }
}


internal interface IHubBinding
{
    string CharacteristicUuid { get; }

    /// <summary>
    /// The hub's class - null for a dynamic hub, which has none
    /// </summary>
    Type? HubType { get; }

    /// <summary>
    /// The hub's name in logs and errors
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The blehubs seam every non-BLE transport serves this hub through
    /// </summary>
    IBleHubTransportEndpoint Endpoint { get; }

    /// <summary>
    /// The mDNS service type this hub is advertised under, null for the host's default
    /// </summary>
    string? ServiceType { get; }

    /// <summary>
    /// Whether clients may reach this hub through a tunnel (the public internet)
    /// </summary>
    bool AllowTunnel { get; }

    /// <summary>
    /// Accepting handshakes over HTTP
    /// </summary>
    bool IsHttpRunning { get; set; }

    /// <summary>
    /// Accepting clients over BLE
    /// </summary>
    bool IsBleRunning { get; }

    Task StartBle(CancellationToken cancellationToken);
    Task StopBle(string? reason);

    /// <summary>
    /// Hangs up on every client of this hub, telling each one why
    /// </summary>
    Task HangUpAll(HubDisconnect disconnect);

    /// <summary>
    /// A Wi-Fi client opened its media channel. False when the client is no longer connected.
    /// </summary>
    bool OpenMedia(string connectionId, IBleHubMediaChannel channel);
}


/// <summary>
/// One hub served over Wi-Fi: admits lines into the hub, runs calls through it, and carries its pushes back out. The typed
/// <see cref="HubBinding{THub}"/> and the runtime-added <see cref="DynamicHubBinding"/> differ only in where the hub and its
/// lines live.
/// </summary>
internal abstract class HubBindingBase : IHubBinding
{
    readonly UniversalHubHost host;
    readonly FileTokens fileTokens;
    readonly MediaTokens mediaTokens;
    readonly ILogger logger;


    protected HubBindingBase(UniversalHubHost host, FileTokens fileTokens, MediaTokens mediaTokens, string? serviceType, bool allowTunnel, ILogger logger)
    {
        this.host = host;
        this.fileTokens = fileTokens;
        this.mediaTokens = mediaTokens;
        this.ServiceType = serviceType;
        this.AllowTunnel = allowTunnel;
        this.logger = logger;
    }


    public abstract IBleHubTransportEndpoint Endpoint { get; }
    public abstract Type? HubType { get; }
    public abstract string Name { get; }
    public string CharacteristicUuid => this.Endpoint.Registration.CharacteristicUuid;
    public string? ServiceType { get; }
    public bool AllowTunnel { get; }
    public bool IsHttpRunning { get; set; }

    public abstract bool IsBleRunning { get; }
    public abstract Task StartBle(CancellationToken cancellationToken);
    public abstract Task StopBle(string? reason);
    public abstract Task HangUpAll(HubDisconnect disconnect);

    /// <summary>
    /// How the hub reaches the Wi-Fi client on <paramref name="lineId"/>
    /// </summary>
    protected abstract IBleHubPeerChannel CreateChannel(string lineId);


    public bool OpenMedia(string connectionId, IBleHubMediaChannel channel) => this.Endpoint.MediaChannelOpened(connectionId, channel);


    public void LineGone(string lineId, HubDisconnect disconnect) => this.Endpoint.Disconnected(HubConnection.HttpPrefix + lineId, disconnect);


    public async ValueTask Handshake(Switchboard board, SwitchboardInvocation call)
    {
        var lineId = board.Context.LineId;
        var connectionId = HubConnection.HttpPrefix + lineId;
        HubHandshake handshake;
        try
        {
            handshake = HubJson.ReadHandshake(call.GetArgument<JsonElement>(0, "handshake"));
        }
        catch (Exception ex) when (ex is JsonException or BleHubProtocolException)
        {
            await call.ReturnAsync(HubJson.Serialize(new HubHandshakeReply(false, "Malformed handshake", null, null, null)));
            return;
        }

        string? rejection;
        if (!this.IsHttpRunning)
            rejection = "Hub is not running";
        else if (handshake.Protocol != UniversalHubProtocol.Version)
            rejection = $"Unsupported protocol version {handshake.Protocol}";
        else
        {
            HttpCall.Current = call.Caller;   // OnConnectedAsync sees the connect request
            rejection = await this.Endpoint.Connect(
                connectionId,
                new HandshakeInfo(FrameCodec.ProtocolVersion, handshake.Name, handshake.AppVersion, handshake.Properties),
                this.CreateChannel(lineId),
                call.Aborted
            );
        }

        string? fileToken = null;
        if (rejection == null && this.host.FilesEnabled)
            fileToken = this.fileTokens.Issue(connectionId, () => this.Endpoint.FindClient(connectionId));

        string? mediaToken = null;
        var mediaMax = this.host.MediaMaxPacketSize;
        if (rejection == null && mediaMax != null)
            mediaToken = this.mediaTokens.Issue(connectionId, this);

        await call.ReturnAsync(HubJson.Serialize(new HubHandshakeReply(
            rejection == null,
            rejection,
            this.host.HostName,
            this.host.HostId,
            fileToken,
            mediaToken,
            mediaToken == null ? null : mediaMax
        )));
    }


    public async ValueTask Invoke(Switchboard board, SwitchboardInvocation call)
    {
        var connectionId = HubConnection.HttpPrefix + board.Context.LineId;
        JsonElement envelope;
        BleHubInvocationResult? result = null;
        try
        {
            var (method, arguments) = this.ReadCall(call, BleHubMethodKind.Invoke);
            HttpCall.Current = call.Caller;
            result = await this.Endpoint.Invoke(connectionId, method, arguments, call.Aborted);
            envelope = HubJson.Result(result.Result, result.Result != null);
        }
        catch (OperationCanceledException) when (call.Aborted.IsCancellationRequested)
        {
            // the caller is gone - nobody is waiting for a reply
            return;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "{Hub} call failed", this.Name);
            envelope = HubJson.Error(ex is HubMethodMismatch ? "HubMethodNotFound" : BleHubRemoteException.TypeOf(ex), ex.Message);
        }

        await call.ReturnAsync(envelope);

        // Context.Abort() takes effect once the reply is out, as it does over BLE
        if (result?.AbortRequested == true)
            await this.Endpoint.Disconnect(connectionId, new HubDisconnect(HubDisconnectReason.ServerDisconnect, result.AbortReason));
    }


    public async ValueTask Stream(Switchboard board, SwitchboardInvocation call)
    {
        var connectionId = HubConnection.HttpPrefix + board.Context.LineId;
        string method;
        byte[] arguments;
        try
        {
            (method, arguments) = this.ReadCall(call, BleHubMethodKind.Stream);
        }
        catch (Exception ex)
        {
            await call.StreamAsync(One(HubJson.Error(ex is HubMethodMismatch ? "HubMethodNotFound" : BleHubRemoteException.TypeOf(ex), ex.Message)));
            return;
        }
        HttpCall.Current = call.Caller;   // flows into the enumeration StreamAsync runs
        await call.StreamAsync(this.StreamItems(connectionId, method, arguments, call.Aborted));
    }


    async IAsyncEnumerable<JsonElement> StreamItems(string connectionId, string method, byte[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // a hub failure ends the stream with an error envelope rather than an SSE error, so its type and message survive
        await using var e = this.Endpoint.Stream(connectionId, method, arguments, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            JsonElement? failure = null;
            var next = false;
            try
            {
                next = await e.MoveNextAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "{Hub} stream failed", this.Name);
                failure = HubJson.Error(BleHubRemoteException.TypeOf(ex), ex.Message);
            }

            if (failure is { } error)
            {
                yield return error;
                yield break;
            }
            if (!next)
                yield break;

            yield return HubJson.Item(e.Current);
        }
    }


    (string Method, byte[] Arguments) ReadCall(SwitchboardInvocation call, BleHubMethodKind expected)
    {
        var method = call.GetArgument<string>(0, "method");
        var kind = this.Endpoint.GetMethodKind(method);
        if (kind != expected)
        {
            throw new HubMethodMismatch(kind == BleHubMethodKind.NotFound
                ? $"{this.Name} has no method '{method}'"
                : $"'{method}' is {(kind == BleHubMethodKind.Stream ? "a stream" : "not a stream")}");
        }
        return (method, HubJson.ArgumentsFromJson(call.GetArgument<JsonElement>(1, "arguments")));
    }


    static async IAsyncEnumerable<JsonElement> One(JsonElement item)
    {
        await Task.CompletedTask;
        yield return item;
    }


    sealed class HubMethodMismatch(string message) : Exception(message);
}


/// <summary>
/// A compiled hub served over Wi-Fi, on its own Switchboard
/// </summary>
internal sealed class HubBinding<THub> : HubBindingBase where THub : class
{
    readonly IServiceProvider services;
    readonly Lazy<IBleHubTransportEndpoint> endpoint;
    readonly Lazy<IOperator<HubBoard<THub>>> board;


    public HubBinding(IServiceProvider services, UniversalHubHost host, FileTokens fileTokens, MediaTokens mediaTokens, string? serviceType, bool allowTunnel, ILoggerFactory? loggerFactory = null)
        : base(host, fileTokens, mediaTokens, serviceType, allowTunnel, (ILogger?)loggerFactory?.CreateLogger($"Shiny.UniversalHubs.{typeof(THub).Name}") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
        this.services = services;
        this.endpoint = new(() =>
        {
            var context = services.GetRequiredService<IHubContext<THub>>();
            context.ClientDisconnected += (_, e) => host.ForgetClient(e.Client.Id);
            return context.TransportEndpoint;
        });
        this.board = new(() => services.GetRequiredService<IOperator<HubBoard<THub>>>());
    }


    public override IBleHubTransportEndpoint Endpoint => this.endpoint.Value;
    public override Type HubType => typeof(THub);
    public override string Name => typeof(THub).Name;

    IHubContext<THub> Context => this.services.GetRequiredService<IHubContext<THub>>();
    public override bool IsBleRunning => this.Context.IsRunning;
    public override Task StartBle(CancellationToken cancellationToken) => this.Context.Start(cancellationToken);
    public override Task StopBle(string? reason) => this.Context.Stop(reason);

    public override Task HangUpAll(HubDisconnect disconnect) => this.board.Value.Lines.HangUpAllAsync(HubJson.HangUpMessage(disconnect), allowReconnect: false);

    protected override IBleHubPeerChannel CreateChannel(string lineId) => new LineChannel(this.board.Value.Clients, this.board.Value.Lines, lineId);
}


/// <summary>
/// How the hub reaches one Wi-Fi client: pushes and hang-ups go through the Switchboard's operator
/// </summary>
internal sealed class LineChannel(ISwitchboardCallerClients clients, ILineManager lines, string lineId) : IBleHubPeerChannel
{
    public Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken)
        => clients.Line(lineId).SendCoreAsync(UniversalHubProtocol.PushMethod, [eventName, HubJson.ArgumentsToJson(arguments)], cancellationToken);

    public Task Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken)
        => lines.HangUpAsync(lineId, HubJson.HangUpMessage(disconnect), allowReconnect: false);
}


/// <summary>
/// File tokens handed out in handshake replies - each resolves to the hub client it was issued to
/// </summary>
internal sealed class FileTokens
{
    readonly ConcurrentDictionary<string, (string ConnectionId, Func<BleHubConnectedClient?> Client)> tokens = new(StringComparer.Ordinal);

    public string Issue(string connectionId, Func<BleHubConnectedClient?> client)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        this.tokens[token] = (connectionId, client);
        return token;
    }

    public BleHubConnectedClient? Find(string? token)
        => token != null && this.tokens.TryGetValue(token, out var entry) ? entry.Client() : null;

    public void Remove(string connectionId)
    {
        foreach (var pair in this.tokens.Where(x => x.Value.ConnectionId == connectionId).ToList())
            this.tokens.TryRemove(pair.Key, out _);
    }

    public void Clear() => this.tokens.Clear();
}
