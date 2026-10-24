namespace Shiny.BluetoothLE.Hubs;

public interface IHubClients<T>
{
    T All { get; }
    T AllExcept(params IEnumerable<string> excludedConnectionIds);
    T Client(string connectionId);
    T Clients(params IEnumerable<string> connectionIds);
    T Group(string groupName);
    T Groups(params IEnumerable<string> groupNames);
    T GroupExcept(string groupName, params IEnumerable<string> excludedConnectionIds);
}


public interface IHubCallerClients<T> : IHubClients<T>
{
    T Caller { get; }
    T Others { get; }
    T OthersInGroup(string groupName);
}


public interface IGroupManager
{
    Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default);
    Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connection ids currently in a group
    /// </summary>
    IReadOnlyList<string> GetMembers(string groupName);
}


/// <summary>
/// A resolved set of recipients. The generated push extensions (eg. push.StateChanged(state)) send through this.
/// </summary>
public readonly struct BleHubPush<TContract> where TContract : class
{
    readonly PushTarget target;
    readonly IBleHubSerializer serializer;

    internal BleHubPush(PushTarget target, IBleHubSerializer serializer)
    {
        this.target = target;
        this.serializer = serializer;
    }

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public BleHubArgumentWriter CreateArguments() => new(this.serializer);

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public Task Send(string eventName, BleHubArgumentWriter arguments, CancellationToken cancellationToken = default)
        => this.target.Send(eventName, arguments.ToArray(), cancellationToken);
}


internal sealed class PushTarget(HubRuntime runtime, Func<IEnumerable<HostPeer>> recipients)
{
    public Task Send(string eventName, byte[] payload, CancellationToken cancellationToken)
        => runtime.Push(eventName, payload, recipients(), cancellationToken);
}


internal class HubClients<T>(HubRuntime runtime, Func<PushTarget, T> factory) : IHubClients<T>
{
    protected HubRuntime Runtime => runtime;
    protected T Create(Func<IEnumerable<HostPeer>> recipients) => factory(new PushTarget(runtime, recipients));

    public T All => this.Create(() => runtime.ReadyPeers);

    public T AllExcept(params IEnumerable<string> excludedConnectionIds)
    {
        var excluded = ToSet(excludedConnectionIds);
        return this.Create(() => runtime.ReadyPeers.Where(x => !excluded.Contains(x.Id)));
    }

    public T Client(string connectionId) => this.Create(() => runtime.ReadyPeers.Where(x => Same(x.Id, connectionId)));

    public T Clients(params IEnumerable<string> connectionIds)
    {
        var ids = ToSet(connectionIds);
        return this.Create(() => runtime.ReadyPeers.Where(x => ids.Contains(x.Id)));
    }

    public T Group(string groupName) => this.Create(() => runtime.PeersInGroups([groupName]));

    public T Groups(params IEnumerable<string> groupNames)
    {
        var names = groupNames.ToList();
        return this.Create(() => runtime.PeersInGroups(names));
    }

    public T GroupExcept(string groupName, params IEnumerable<string> excludedConnectionIds)
    {
        var excluded = ToSet(excludedConnectionIds);
        return this.Create(() => runtime.PeersInGroups([groupName]).Where(x => !excluded.Contains(x.Id)));
    }

    protected static HashSet<string> ToSet(IEnumerable<string> ids) => new(ids, StringComparer.OrdinalIgnoreCase);
    protected static bool Same(string a, string b) => String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}


internal sealed class HubCallerClients<T>(HubRuntime runtime, Func<PushTarget, T> factory, string callerId)
    : HubClients<T>(runtime, factory), IHubCallerClients<T>
{
    public T Caller => this.Client(callerId);
    public T Others => this.AllExcept(callerId);
    public T OthersInGroup(string groupName) => this.GroupExcept(groupName, callerId);
}
