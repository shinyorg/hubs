using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// A transport hubs are served over - BLE, HTTP, Multipeer, Nearby. It reaches each hub through
/// <see cref="IHubBinding.Endpoint"/>, so its clients share Clients, Groups, MaxClients and ValidateClient with everyone
/// else. <see cref="UniversalHubHost"/> starts and stops it, handing it only the hubs set up to use it.
/// </summary>
internal interface IUniversalHubTransport
{
    /// <summary>
    /// The one flag this transport answers to
    /// </summary>
    HubTransports Transport { get; }

    /// <summary>
    /// Whether <paramref name="hub"/> is accepting clients over this transport
    /// </summary>
    bool IsRunning(IHubBinding hub);

    /// <summary>
    /// Starts accepting clients of <paramref name="hubs"/>. Throws when the transport can't run here (no radio, wrong
    /// platform) - the host skips it as long as another transport starts.
    /// </summary>
    Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken);

    /// <summary>
    /// Stops <paramref name="hubs"/> and disconnects their clients on this transport, telling each one why
    /// </summary>
    Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect);

    /// <summary>
    /// The host has a new name - re-advertise under it. Connected clients are told by the hubs themselves.
    /// </summary>
    Task HostRenamed(CancellationToken cancellationToken) => Task.CompletedTask;
}


/// <summary>
/// One hub as the transports see it - whatever it is (a compiled hub class or one added while the app runs) and whichever
/// transports serve it
/// </summary>
internal interface IHubBinding
{
    /// <summary>
    /// How clients ask for the hub on every transport - the HTTP path, the mDNS, Multipeer and Nearby hub lists, the peer
    /// handshake
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The hub's name in logs and errors
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The hub's class - null for a dynamic hub, which has none
    /// </summary>
    Type? HubType { get; }

    /// <summary>
    /// The transports this hub is served over
    /// </summary>
    HubTransports Transports { get; }

    /// <summary>
    /// The hub itself
    /// </summary>
    IBleHubTransportEndpoint Endpoint { get; }

    /// <summary>
    /// What a transport was told about this hub when it was set up to use it (UseBluetoothLE, UseHttp, ...), null when it
    /// wasn't
    /// </summary>
    T? GetSettings<T>() where T : class;
}


/// <summary>
/// A hub the host serves: compiled (AddHub) or added while the app runs (IDynamicHubs)
/// </summary>
internal sealed class HubEntry : IHubBinding
{
    readonly IReadOnlyDictionary<Type, object> settings;

    public HubEntry(string key, string name, Type? hubType, HubTransports transports, IReadOnlyDictionary<Type, object> settings, HubRuntime runtime)
    {
        this.Key = key;
        this.Name = name;
        this.HubType = hubType;
        this.Transports = transports;
        this.settings = settings;
        this.Runtime = runtime;
    }

    public string Key { get; }
    public string Name { get; }
    public Type? HubType { get; }
    public HubTransports Transports { get; }
    public HubRuntime Runtime { get; }
    public IBleHubTransportEndpoint Endpoint => this.Runtime;

    public T? GetSettings<T>() where T : class => this.settings.TryGetValue(typeof(T), out var value) ? (T)value : null;

    public override string ToString() => this.Name;
}


/// <summary>
/// A compiled hub, as AddUniversalServer registered it
/// </summary>
internal sealed record HubRegistration(
    Type HubType,
    string Key,
    BleHubOptions Options,
    HubTransports Transports,
    IReadOnlyDictionary<Type, object> Settings
);
