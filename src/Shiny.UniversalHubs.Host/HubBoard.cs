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
        binding.LineGone(this.Context.LineId, ToHubDisconnect(disconnect));
        return Task.CompletedTask;
    }


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
    Type HubType { get; }

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
/// One hub served over Wi-Fi: admits lines into the hub, runs calls through it, and carries its pushes back out
/// </summary>
internal sealed class HubBinding<THub> : IHubBinding where THub : class
{
    readonly IServiceProvider services;
    readonly UniversalHubHost host;
    readonly FileTokens fileTokens;
    readonly MediaTokens mediaTokens;
    readonly ILogger logger;
    readonly Lazy<IBleHubTransportEndpoint> endpoint;
    readonly Lazy<IOperator<HubBoard<THub>>> board;


    public HubBinding(IServiceProvider services, UniversalHubHost host, FileTokens fileTokens, MediaTokens mediaTokens, string? serviceType, bool allowTunnel, ILoggerFactory? loggerFactory = null)
    {
        this.mediaTokens = mediaTokens;
        this.services = services;
        this.ServiceType = serviceType;
        this.AllowTunnel = allowTunnel;
        this.host = host;
        this.fileTokens = fileTokens;
        this.logger = (ILogger?)loggerFactory?.CreateLogger($"Shiny.UniversalHubs.{typeof(THub).Name}") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        this.endpoint = new(() =>
        {
            var context = services.GetRequiredService<IHubContext<THub>>();
            context.ClientDisconnected += (_, e) =>
            {
                fileTokens.Remove(e.Client.Id);
                mediaTokens.Remove(e.Client.Id);
            };
            return context.TransportEndpoint;
        });
        this.board = new(() => services.GetRequiredService<IOperator<HubBoard<THub>>>());
    }


    public IBleHubTransportEndpoint Endpoint => this.endpoint.Value;
    public string CharacteristicUuid => this.Endpoint.Registration.CharacteristicUuid;
    public Type HubType => typeof(THub);
    public string? ServiceType { get; }
    public bool AllowTunnel { get; }
    public bool IsHttpRunning { get; set; }

    IHubContext<THub> Context => this.services.GetRequiredService<IHubContext<THub>>();
    public bool IsBleRunning => this.Context.IsRunning;
    public Task StartBle(CancellationToken cancellationToken) => this.Context.Start(cancellationToken);
    public Task StopBle(string? reason) => this.Context.Stop(reason);


    public Task HangUpAll(HubDisconnect disconnect) => this.board.Value.Lines.HangUpAllAsync(HubJson.HangUpMessage(disconnect), allowReconnect: false);


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
                new LineChannel<THub>(this.board.Value, lineId),
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
            this.logger.LogWarning(ex, "{Hub} call failed", typeof(THub).Name);
            envelope = HubJson.Error(ex is HubMethodMismatch ? "HubMethodNotFound" : ex.GetType().Name, ex.Message);
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
            await call.StreamAsync(One(HubJson.Error(ex is HubMethodMismatch ? "HubMethodNotFound" : ex.GetType().Name, ex.Message)));
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
                this.logger.LogWarning(ex, "{Hub} stream failed", typeof(THub).Name);
                failure = HubJson.Error(ex.GetType().Name, ex.Message);
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
                ? $"{typeof(THub).Name} has no method '{method}'"
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
/// How the hub reaches one Wi-Fi client: pushes and hang-ups go through the Switchboard's operator
/// </summary>
internal sealed class LineChannel<THub>(IOperator<HubBoard<THub>> board, string lineId) : IBleHubPeerChannel where THub : class
{
    public Task Push(string eventName, byte[] arguments, CancellationToken cancellationToken)
        => board.Clients.Line(lineId).SendCoreAsync(UniversalHubProtocol.PushMethod, [eventName, HubJson.ArgumentsToJson(arguments)], cancellationToken);

    public Task Disconnect(HubDisconnect disconnect, CancellationToken cancellationToken)
        => board.Lines.HangUpAsync(lineId, HubJson.HangUpMessage(disconnect), allowReconnect: false);
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
