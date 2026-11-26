using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// A hub whose methods aren't compiled into this app - served by a web page, a script, another process. Its methods are
/// dispatched by name, and values stay exactly as the caller's <see cref="IBleHubSerializer"/> wrote them, so nothing here
/// needs their types. UniversalHubs adds them while the app runs (IDynamicHubs).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IBleHubHandler
{
    /// <summary>
    /// Whether <paramref name="method"/> exists, and whether it is a stream
    /// </summary>
    BleHubMethodKind GetMethodKind(string method);

    /// <summary>
    /// Runs a method. Returns its serialized result, or null for a method with none. Throw to fail the call - the caller
    /// gets the exception's type name and message.
    /// </summary>
    Task<byte[]?> Invoke(BleHubHandlerCall call, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a streaming method - each item serialized
    /// </summary>
    IAsyncEnumerable<byte[]> Stream(BleHubHandlerCall call, CancellationToken cancellationToken);

    /// <summary>
    /// A client completed its handshake and isn't told it is connected until this returns. Throw to refuse it - it gets the
    /// exception's message.
    /// </summary>
    Task OnConnectedAsync(BleHubCallerContext context);

    /// <summary>
    /// A client left. Group membership is still intact.
    /// </summary>
    Task OnDisconnectedAsync(BleHubCallerContext context, HubDisconnect disconnect);

    /// <summary>
    /// A client renamed itself (<see cref="BleHubConnectedClient.Name"/> is the new name). Throw to refuse - the name is put back.
    /// </summary>
    Task OnRenamedAsync(BleHubCallerContext context, string? previousName);
}


/// <summary>
/// A call into an <see cref="IBleHubHandler"/>
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BleHubHandlerCall
{
    internal BleHubHandlerCall(BleHubCallerContext context, string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments)
    {
        this.Context = context;
        this.Method = method;
        this.Arguments = arguments;
    }

    /// <summary>
    /// The client making the call - <see cref="BleHubCallerContext.Abort"/> ends its session once the reply is sent
    /// </summary>
    public BleHubCallerContext Context { get; }
    public string Method { get; }

    /// <summary>
    /// Each argument exactly as the caller's serializer wrote it
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Arguments { get; }
}


/// <summary>
/// Stands in for a hub class: the runtime creates one per call, as it does a compiled hub, and it hands everything to the handler
/// </summary>
internal sealed class HandlerHub(IBleHubHandler handler) : IBleHubInternal
{
    public IBleHubHandler Handler => handler;
    public BleHubCallerContext Context { get; private set; } = null!;

    public void Initialize(BleHubCallerContext context, HubRuntime runtime) => this.Context = context;
    public Task OnConnectedAsync() => handler.OnConnectedAsync(this.Context);
    public Task OnDisconnectedAsync(HubDisconnect disconnect) => handler.OnDisconnectedAsync(this.Context, disconnect);
    public Task OnRenamedAsync(string? previousName) => handler.OnRenamedAsync(this.Context, previousName);
}


/// <summary>
/// The dispatcher a handler hub runs under - reads each argument's bytes and passes them through untouched
/// </summary>
internal sealed class HandlerDispatcher(IBleHubHandler handler) : IBleHubDispatcher
{
    public Type HubType => typeof(HandlerHub);
    public Type ContractType => typeof(IBleHubHandler);
    public BleHubMethodKind GetMethodKind(string method) => handler.GetMethodKind(method);

    public Task<byte[]?> Invoke(object hub, string method, BleHubArgumentReader args, IBleHubSerializer serializer, CancellationToken cancellationToken)
        => handler.Invoke(Call(hub, method, args), cancellationToken);

    public async IAsyncEnumerable<byte[]> Stream(object hub, string method, BleHubArgumentReader args, IBleHubSerializer serializer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in handler.Stream(Call(hub, method, args), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    static BleHubHandlerCall Call(object hub, string method, BleHubArgumentReader args)
    {
        var values = new ReadOnlyMemory<byte>[args.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = args.ReadSerialized();

        return new BleHubHandlerCall(((HandlerHub)hub).Context, method, values);
    }
}
