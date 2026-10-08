using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;
using Shiny.Net.HttpServer.Switchboard;

namespace Shiny.SwitchboardR;

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
        binding.LineGone(this.Context.LineId, disconnect.Message ?? disconnect.Reason switch
        {
            DisconnectReason.ClientClosed => "Disconnected",
            DisconnectReason.HungUp => "Disconnected by host",
            DisconnectReason.ServerShutdown => "Host stopped",
            _ => "Connection lost"
        });
        return Task.CompletedTask;
    }
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
                new SwitchboardMethod(SwitchboardRProtocol.HandshakeMethod, 1, 1, [], (board, call) => ((HubBoard<THub>)board).Binding.Handshake(board, call)),
                new SwitchboardMethod(SwitchboardRProtocol.InvokeMethod, 2, 2, [], (board, call) => ((HubBoard<THub>)board).Binding.Invoke(board, call)),
                new SwitchboardMethod(SwitchboardRProtocol.StreamMethod, 2, 2, [], (board, call) => ((HubBoard<THub>)board).Binding.Stream(board, call))
            ]
        ));
    }
}


internal interface IHubBinding
{
    string CharacteristicUuid { get; }
    Type HubType { get; }
    Task HangUpAll(string? reason);
}


/// <summary>
/// One hub served over Wi-Fi: admits lines into the hub, runs calls through it, and carries its pushes back out
/// </summary>
internal sealed class HubBinding<THub> : IHubBinding where THub : class
{
    const string ConnectionPrefix = "sb:";

    readonly IServiceProvider services;
    readonly SwitchboardRHost host;
    readonly FileTokens fileTokens;
    readonly ILogger logger;
    readonly Lazy<IBleHubTransportEndpoint> endpoint;
    readonly Lazy<IOperator<HubBoard<THub>>> board;


    public HubBinding(IServiceProvider services, SwitchboardRHost host, FileTokens fileTokens, ILoggerFactory? loggerFactory = null)
    {
        this.services = services;
        this.host = host;
        this.fileTokens = fileTokens;
        this.logger = (ILogger?)loggerFactory?.CreateLogger($"Shiny.SwitchboardR.{typeof(THub).Name}") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        this.endpoint = new(() =>
        {
            var context = services.GetRequiredService<IHubContext<THub>>();
            context.ClientDisconnected += (_, e) => fileTokens.Remove(e.Client.Id);
            return context.TransportEndpoint;
        });
        this.board = new(() => services.GetRequiredService<IOperator<HubBoard<THub>>>());
    }


    public IBleHubTransportEndpoint Endpoint => this.endpoint.Value;
    public string CharacteristicUuid => this.Endpoint.Registration.CharacteristicUuid;
    public Type HubType => typeof(THub);


    public Task HangUpAll(string? reason) => this.board.Value.Lines.HangUpAllAsync(reason, allowReconnect: false);


    public void LineGone(string lineId, string? reason) => this.Endpoint.Disconnected(ConnectionPrefix + lineId, reason);


    public async ValueTask Handshake(Switchboard board, SwitchboardInvocation call)
    {
        var lineId = board.Context.LineId;
        var connectionId = ConnectionPrefix + lineId;
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
        if (!this.host.IsWifiRunning)
            rejection = "Hub is not running";
        else if (handshake.Protocol != SwitchboardRProtocol.Version)
            rejection = $"Unsupported protocol version {handshake.Protocol}";
        else
            rejection = await this.Endpoint.Connect(
                connectionId,
                new HandshakeInfo(FrameCodec.ProtocolVersion, handshake.Name, handshake.AppVersion, handshake.Properties),
                new LineChannel<THub>(this.board.Value, lineId),
                call.Aborted
            );

        string? fileToken = null;
        if (rejection == null && this.host.FilesEnabled)
            fileToken = this.fileTokens.Issue(connectionId, () => this.Endpoint.FindClient(connectionId));

        await call.ReturnAsync(HubJson.Serialize(new HubHandshakeReply(
            rejection == null,
            rejection,
            this.host.HostName,
            this.host.HostId,
            fileToken
        )));
    }


    public async ValueTask Invoke(Switchboard board, SwitchboardInvocation call)
    {
        var connectionId = ConnectionPrefix + board.Context.LineId;
        JsonElement envelope;
        BleHubInvocationResult? result = null;
        try
        {
            var (method, arguments) = this.ReadCall(call, BleHubMethodKind.Invoke);
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
            await this.Endpoint.Disconnect(connectionId, result.AbortReason);
    }


    public async ValueTask Stream(Switchboard board, SwitchboardInvocation call)
    {
        var connectionId = ConnectionPrefix + board.Context.LineId;
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
        => board.Clients.Line(lineId).SendCoreAsync(SwitchboardRProtocol.PushMethod, [eventName, HubJson.ArgumentsToJson(arguments)], cancellationToken);

    public Task Disconnect(string? reason, CancellationToken cancellationToken)
        => board.Lines.HangUpAsync(lineId, reason, allowReconnect: false);
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
