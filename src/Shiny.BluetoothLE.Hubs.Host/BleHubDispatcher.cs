using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shiny.BluetoothLE.Hubs;

[EditorBrowsable(EditorBrowsableState.Never)]
public enum BleHubMethodKind
{
    NotFound,
    Invoke,
    Stream
}


/// <summary>
/// Implemented by the source generator for every hub - maps method names to typed calls without reflection
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IBleHubDispatcher
{
    Type HubType { get; }
    Type ContractType { get; }
    BleHubMethodKind GetMethodKind(string method);
    Task<byte[]?> Invoke(object hub, string method, BleHubArgumentReader args, IBleHubSerializer serializer, CancellationToken cancellationToken);
    IAsyncEnumerable<byte[]> Stream(object hub, string method, BleHubArgumentReader args, IBleHubSerializer serializer, CancellationToken cancellationToken);
}


[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class BleHubDispatcher<THub, TContract> : IBleHubDispatcher where THub : class where TContract : class
{
    public Type HubType => typeof(THub);
    public Type ContractType => typeof(TContract);

    public abstract BleHubMethodKind GetMethodKind(string method);
    public abstract Task<byte[]?> Invoke(THub hub, string method, BleHubArgumentReader args, IBleHubSerializer serializer, CancellationToken cancellationToken);
    public abstract IAsyncEnumerable<byte[]> Stream(THub hub, string method, BleHubArgumentReader args, IBleHubSerializer serializer, CancellationToken cancellationToken);

    Task<byte[]?> IBleHubDispatcher.Invoke(object hub, string method, BleHubArgumentReader args, IBleHubSerializer serializer, CancellationToken cancellationToken)
        => this.Invoke((THub)hub, method, args, serializer, cancellationToken);

    IAsyncEnumerable<byte[]> IBleHubDispatcher.Stream(object hub, string method, BleHubArgumentReader args, IBleHubSerializer serializer, CancellationToken cancellationToken)
        => this.Stream((THub)hub, method, args, serializer, cancellationToken);

    protected BleHubException MethodNotFound(string method) => new($"{typeof(THub).Name} has no method '{method}'");

    protected static async IAsyncEnumerable<byte[]> SerializeStream<T>(IAsyncEnumerable<T> source, IBleHubSerializer serializer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return serializer.Serialize(item);
    }
}


[EditorBrowsable(EditorBrowsableState.Never)]
public static class BleHubDispatchers
{
    static readonly ConcurrentDictionary<Type, IBleHubDispatcher> dispatchers = new();

    public static void Register(IBleHubDispatcher dispatcher) => dispatchers[dispatcher.HubType] = dispatcher;

    internal static IBleHubDispatcher Get(Type hubType)
    {
        if (dispatchers.TryGetValue(hubType, out var dispatcher))
            return dispatcher;

        // the generated registration is a module initializer - make sure the hub's assembly has run it
        RuntimeHelpers.RunModuleConstructor(hubType.Module.ModuleHandle);
        if (dispatchers.TryGetValue(hubType, out dispatcher))
            return dispatcher;

        throw new InvalidOperationException(
            $"No generated dispatcher for {hubType.FullName}. It must derive from BleHub<TContract> where TContract is an interface " +
            "marked [BleHubClient], and the Shiny.BluetoothLE.Hubs source generator must run on its project (check for SBH diagnostics)."
        );
    }
}
