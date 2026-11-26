using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.UniversalHubs;

/// <summary>
/// A host found over one transport or several. <see cref="IUniversalHubClient{TContract}.DiscoverAll()"/> raises a fresh
/// snapshot whenever any transport's view changes; when <see cref="Transports"/> is empty the host has gone.
/// </summary>
/// <param name="Key">Stable for one host across every transport - its advertised name</param>
/// <param name="Name">The host's name</param>
/// <param name="HostId">The host's id, once a transport that carries it (mDNS, Multipeer, Nearby) has seen it</param>
public sealed record HubHostInfo(string Key, string Name, string? HostId)
{
    /// <summary>
    /// What each transport saw of the host, by the client transport's type
    /// </summary>
    public IReadOnlyDictionary<Type, HubEndpoint> Endpoints { get; init; } = ImmutableDictionary<Type, HubEndpoint>.Empty;

    /// <summary>
    /// The types of the client transports the host is seen on, in the client's priority order - the order a connect tries
    /// them in, unless <see cref="HubConnectOptions"/> says otherwise
    /// </summary>
    public IReadOnlyList<Type> Transports { get; init; } = [];

    /// <summary>
    /// What a transport saw of the host, by the endpoint's type (eg. <c>host.Endpoint&lt;BleHubEndpoint&gt;()?.Rssi</c>) -
    /// null when the host isn't seen over that transport
    /// </summary>
    public TEndpoint? Endpoint<TEndpoint>() where TEndpoint : HubEndpoint
        => this.Endpoints.Values.OfType<TEndpoint>().FirstOrDefault();

    /// <summary>
    /// Whether the host is seen over <typeparamref name="TTransport"/>
    /// </summary>
    public bool IsSeenOver<TTransport>() where TTransport : IHubClientTransport => this.Endpoints.ContainsKey(typeof(TTransport));


    /// <summary>
    /// This host with <paramref name="transport"/>'s view replaced - <paramref name="endpoint"/> null when it no longer
    /// sees the host - and <see cref="Transports"/> in <paramref name="priority"/> order
    /// </summary>
    internal HubHostInfo With(Type transport, HubEndpoint? endpoint, IReadOnlyList<Type> priority)
    {
        var endpoints = this.Endpoints.ToDictionary();
        if (endpoint == null)
            endpoints.Remove(transport);
        else
            endpoints[transport] = endpoint;

        int Rank(Type type)
        {
            for (var i = 0; i < priority.Count; i++)
            {
                if (priority[i] == type)
                    return i;
            }
            return Int32.MaxValue;
        }

        return this with
        {
            Endpoints = endpoints,
            Transports = endpoints.Keys.OrderBy(Rank).ToList()
        };
    }
}


/// <summary>
/// What one client transport saw of a host - each transport has its own (BleHubEndpoint, HttpHubEndpoint, ...)
/// </summary>
public abstract record HubEndpoint;


/// <summary>
/// What the client tells the host about itself, and which transports it may use and in what order
/// </summary>
public sealed record HubConnectOptions(
    string? Name = null,
    string? AppVersion = null,
    Dictionary<string, string>? Properties = null
)
{
    /// <summary>
    /// Client transport types tried first, in this order; the rest follow in the client's priority
    /// (UniversalHubClientOptions.Priority, else the order its transports were added in)
    /// </summary>
    public IReadOnlyList<Type> Preferred { get; init; } = [];

    /// <summary>
    /// The only client transport types that may be used - null for any
    /// </summary>
    public IReadOnlySet<Type>? Allowed { get; init; }

    /// <summary>
    /// Tries <typeparamref name="TTransport"/> before the others (after any preferred already)
    /// </summary>
    public HubConnectOptions Prefer<TTransport>() where TTransport : IHubClientTransport => this.Prefer(typeof(TTransport));

    /// <summary>
    /// Tries <paramref name="transports"/> before the others, in this order (after any preferred already)
    /// </summary>
    public HubConnectOptions Prefer(params IEnumerable<Type> transports)
        => this with { Preferred = [.. this.Preferred, .. transports.Where(x => !this.Preferred.Contains(x))] };

    /// <summary>
    /// Lets <typeparamref name="TTransport"/> be used - once anything is allowed, only what is allowed is used
    /// </summary>
    public HubConnectOptions Allow<TTransport>() where TTransport : IHubClientTransport => this.Allow(typeof(TTransport));

    /// <summary>
    /// Lets <paramref name="transports"/> be used - once anything is allowed, only what is allowed is used
    /// </summary>
    public HubConnectOptions Allow(params IEnumerable<Type> transports)
        => this with { Allowed = (this.Allowed ?? ImmutableHashSet<Type>.Empty).Concat(transports).ToHashSet() };

    /// <summary>
    /// Uses <typeparamref name="TTransport"/> and nothing else
    /// </summary>
    public HubConnectOptions Only<TTransport>() where TTransport : IHubClientTransport
        => this with { Preferred = [typeof(TTransport)], Allowed = new HashSet<Type> { typeof(TTransport) } };


    /// <summary>
    /// The order <paramref name="transports"/> (in the client's priority) are tried in: <see cref="Preferred"/> first, then
    /// the rest, leaving out what isn't <see cref="Allowed"/>
    /// </summary>
    internal IReadOnlyList<IHubClientTransport> Order(IReadOnlyList<IHubClientTransport> transports)
    {
        var unknown = this.Preferred.Concat(this.Allowed ?? Enumerable.Empty<Type>()).FirstOrDefault(t => !transports.Any(x => x.GetType() == t));
        if (unknown != null)
            throw new ArgumentException($"{unknown.Name} is not a transport this hub client has");

        var preferred = this.Preferred.Select(t => transports.First(x => x.GetType() == t));
        return preferred
            .Concat(transports.Where(x => !this.Preferred.Contains(x.GetType())))
            .Where(x => this.Allowed == null || this.Allowed.Contains(x.GetType()))
            .ToList();
    }

    internal HubHandshakeOptions ToBle() => new(this.Name, this.AppVersion, this.Properties);
}


/// <summary>
/// How a hub client reaches its hub: the transports it uses (at least one), the order it tries them in, and how it connects
/// </summary>
public class UniversalHubClientOptions
{
    readonly List<(Type Type, Func<IServiceProvider, IHubClientTransport> Factory)> transports = new();
    readonly List<Type> priority = new();

    internal List<Action<HubProtocolOptions>> ProtocolActions { get; } = new();

    /// <summary>
    /// Registrations the transports need
    /// </summary>
    internal List<Action<IServiceCollection>> ServiceActions { get; } = new();

    /// <summary>
    /// A transport set up again replaces the earlier one (in its place) instead of throwing - dynamic hub clients, whose
    /// shared setup each hub may override
    /// </summary>
    internal bool ReplaceTransports { get; init; }

    /// <summary>
    /// Resume after a dropped link instead of disconnecting - the host keeps the client, its groups and Items, and replays
    /// missed pushes. Over HTTP the line resumes on its own; over BLE, Multipeer and Nearby the client reconnects
    /// (<see cref="HubClientStatus.Reconnecting"/>) within the host's resume window (UniversalServerOptions.ResumeWindow).
    /// </summary>
    public bool AutomaticReconnect { get; set; } = true;


    /// <summary>
    /// Reaches the hub over a transport of your own. Each transport type once per hub client; transports are tried in the
    /// order they are added unless <see cref="Priority(Type[])"/> says otherwise.
    /// </summary>
    /// <param name="create">Creates the transport - once per hub client</param>
    /// <param name="services">Registrations the transport needs, if any</param>
    public UniversalHubClientOptions UseTransport<TTransport>(Func<IServiceProvider, TTransport> create, Action<IServiceCollection>? services = null)
        where TTransport : class, IHubClientTransport
    {
        ArgumentNullException.ThrowIfNull(create);
        var existing = this.transports.FindIndex(x => x.Type == typeof(TTransport));
        if (existing >= 0 && !this.ReplaceTransports)
            throw new InvalidOperationException($"{typeof(TTransport).Name} is already set up for this hub client");

        if (existing >= 0)
            this.transports[existing] = (typeof(TTransport), create);
        else
            this.transports.Add((typeof(TTransport), create));
        if (services != null)
            this.ServiceActions.Add(services);
        return this;
    }


    /// <summary>
    /// The order transports are tried in when a host is seen on several - by client transport type. Transports left out
    /// follow in the order they were added.
    /// </summary>
    public UniversalHubClientOptions Priority(params Type[] transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        var duplicate = transports.GroupBy(x => x).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException($"{duplicate.Key.Name} is in the priority more than once", nameof(transports));

        this.priority.Clear();
        this.priority.AddRange(transports);
        return this;
    }

    /// <inheritdoc cref="Priority(Type[])"/>
    public UniversalHubClientOptions Priority<T1, T2>()
        where T1 : IHubClientTransport where T2 : IHubClientTransport
        => this.Priority(typeof(T1), typeof(T2));

    /// <inheritdoc cref="Priority(Type[])"/>
    public UniversalHubClientOptions Priority<T1, T2, T3>()
        where T1 : IHubClientTransport where T2 : IHubClientTransport where T3 : IHubClientTransport
        => this.Priority(typeof(T1), typeof(T2), typeof(T3));

    /// <inheritdoc cref="Priority(Type[])"/>
    public UniversalHubClientOptions Priority<T1, T2, T3, T4>()
        where T1 : IHubClientTransport where T2 : IHubClientTransport where T3 : IHubClientTransport where T4 : IHubClientTransport
        => this.Priority(typeof(T1), typeof(T2), typeof(T3), typeof(T4));


    internal IHubSerializer? HubSerializer { get; private set; }

    /// <summary>
    /// The serializer this client's calls, results and pushes use, instead of the app's IHubSerializer - the hub's own
    /// (its Serializer(...)), by name
    /// </summary>
    public UniversalHubClientOptions Serializer(IHubSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        this.HubSerializer = serializer;
        return this;
    }


    /// <summary>
    /// Protocol limits (payload size, timeouts) - app-wide, shared with every other hub client and host in the app
    /// </summary>
    public UniversalHubClientOptions Protocol(Action<HubProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.ProtocolActions.Add(configure);
        return this;
    }


    internal bool Has<TTransport>() where TTransport : IHubClientTransport => this.transports.Any(x => x.Type == typeof(TTransport));

    internal int TransportCount => this.transports.Count;

    /// <summary>
    /// The transport types in the order they're tried: <see cref="Priority(Type[])"/>, then the rest as they were added.
    /// Naming a transport that isn't set up is a mistake.
    /// </summary>
    internal IReadOnlyList<Type> ResolvePriority()
    {
        var unknown = this.priority.FirstOrDefault(t => !this.transports.Any(x => x.Type == t));
        if (unknown != null)
            throw new InvalidOperationException($"{unknown.Name} is in the priority but isn't set up for this hub client");

        return [.. this.priority, .. this.transports.Select(x => x.Type).Where(x => !this.priority.Contains(x))];
    }

    /// <summary>
    /// Creates the transports, in priority order
    /// </summary>
    internal IReadOnlyList<IHubClientTransport> CreateTransports(IServiceProvider services)
        => this.ResolvePriority().Select(t => this.transports.First(x => x.Type == t).Factory(services)).ToList();
}


/// <summary>
/// A transport a hub client finds hosts over and connects through - Bluetooth LE, HTTP, Multipeer, Nearby, or one of your
/// own. Its type is its identity: <see cref="HubHostInfo.Endpoints"/>, <see cref="HubConnectOptions.Prefer{TTransport}"/>
/// and <see cref="UniversalHubClientOptions.Priority(Type[])"/> all go by it.
/// </summary>
public interface IHubClientTransport
{
    /// <summary>
    /// The transport's name in logs, errors and UI - not its identity, which is its type
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this transport can run on this device at all (Multipeer is Apple only). One that can't is left out of
    /// discovery and connecting, so one registration works in a cross-platform app.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Hosts of <paramref name="client"/>'s hub as they come and go on this transport. Errors end only this transport's
    /// discovery.
    /// </summary>
    IObservable<HubHostSighting> Discover(HubClientContext client);

    /// <summary>
    /// Connects <paramref name="client"/> to <paramref name="host"/> (seen over this transport: its endpoint is in
    /// <see cref="HubHostInfo.Endpoints"/>) through <see cref="HubClientContext.Connect"/>
    /// </summary>
    Task Connect(HubClientContext client, HubHostInfo host, CancellationToken cancellationToken);
}


/// <summary>
/// The hub client a transport is discovering for or connecting
/// </summary>
public sealed class HubClientContext
{
    internal HubClientContext(HubProxy client, HubHandshakeOptions connectOptions)
    {
        this.Client = client;
        this.ConnectOptions = connectOptions;
    }

    internal HubProxy Client { get; }

    /// <summary>
    /// The name of the hub - what hosts list and handshakes ask for
    /// </summary>
    public string HubName => this.Client.HubName;

    public ILogger? Logger => this.Client.Logger;

    /// <summary>
    /// What the client tells the host about itself, and whether it resumes after a dropped link
    /// (<see cref="HubHandshakeOptions.AutomaticReconnect"/> - a transport that can, reopens its link to the same host)
    /// </summary>
    public HubHandshakeOptions ConnectOptions { get; }

    /// <summary>
    /// Connects through the link <paramref name="createTransport"/> returns: it is handed the callbacks for pushes and
    /// closes, and carries this one connection
    /// </summary>
    public Task Connect(Func<IHubConnectionTransportEvents, IHubConnectionTransport> createTransport, CancellationToken cancellationToken)
        => this.Client.ConnectExternal(createTransport, this.ConnectOptions, cancellationToken);
}


/// <summary>
/// A host appearing on, changing on, or leaving one transport
/// </summary>
/// <param name="Name">The host's name - how it is merged with what the other transports see</param>
/// <param name="HostId">The host's id, when the transport knows it</param>
/// <param name="Endpoint">What the transport sees of the host - null when it no longer sees it</param>
public sealed record HubHostSighting(string Name, string? HostId, HubEndpoint? Endpoint);
