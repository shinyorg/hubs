using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shiny.UniversalHubs;

[EditorBrowsable(EditorBrowsableState.Never)]
public enum HubMethodKind
{
    NotFound,
    Invoke,
    Stream
}


/// <summary>
/// Implemented by the source generator for every hub - maps method names to typed calls without reflection
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IHubDispatcher
{
    Type HubType { get; }
    Type ContractType { get; }
    HubMethodKind GetMethodKind(string method);
    Task<byte[]?> Invoke(object hub, string method, HubArgumentReader args, IHubSerializer serializer, CancellationToken cancellationToken);
    IAsyncEnumerable<byte[]> Stream(object hub, string method, HubArgumentReader args, IHubSerializer serializer, CancellationToken cancellationToken);
}


[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class HubDispatcher<THub, TContract> : IHubDispatcher where THub : class where TContract : class
{
    public Type HubType => typeof(THub);
    public Type ContractType => typeof(TContract);

    public abstract HubMethodKind GetMethodKind(string method);
    public abstract Task<byte[]?> Invoke(THub hub, string method, HubArgumentReader args, IHubSerializer serializer, CancellationToken cancellationToken);
    public abstract IAsyncEnumerable<byte[]> Stream(THub hub, string method, HubArgumentReader args, IHubSerializer serializer, CancellationToken cancellationToken);

    Task<byte[]?> IHubDispatcher.Invoke(object hub, string method, HubArgumentReader args, IHubSerializer serializer, CancellationToken cancellationToken)
        => this.Invoke((THub)hub, method, args, serializer, cancellationToken);

    IAsyncEnumerable<byte[]> IHubDispatcher.Stream(object hub, string method, HubArgumentReader args, IHubSerializer serializer, CancellationToken cancellationToken)
        => this.Stream((THub)hub, method, args, serializer, cancellationToken);

    protected HubException MethodNotFound(string method) => new($"{typeof(THub).Name} has no method '{method}'");

    protected static async IAsyncEnumerable<byte[]> SerializeStream<T>(IAsyncEnumerable<T> source, IHubSerializer serializer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return serializer.Serialize(item);
    }
}


[EditorBrowsable(EditorBrowsableState.Never)]
public static class HubDispatchers
{
    static readonly ConcurrentDictionary<Type, IHubDispatcher> dispatchers = new();

    public static void Register(IHubDispatcher dispatcher) => dispatchers[dispatcher.HubType] = dispatcher;

    internal static IHubDispatcher Get(Type hubType)
    {
        if (dispatchers.TryGetValue(hubType, out var dispatcher))
            return dispatcher;

        // the generated registration is a module initializer - make sure the hub's assembly has run it
        RuntimeHelpers.RunModuleConstructor(hubType.Module.ModuleHandle);
        if (dispatchers.TryGetValue(hubType, out dispatcher))
            return dispatcher;

        throw new InvalidOperationException(
            $"No generated dispatcher for {hubType.FullName}. It must derive from UniversalHub<TContract> where TContract is an interface " +
            "marked [UniversalHubContract], and the Shiny.UniversalHubs source generator must run on its project (check for SUH diagnostics)."
        );
    }
}
