using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.UniversalHubs.Protocol;

namespace Shiny.UniversalHubs;

public static partial class UniversalServerExtensions
{
    /// <summary>
    /// Registers a hub server: every hub it serves and the transports each one is served over, in one call. A hub is the
    /// same hub on every transport - its clients share Clients, Groups, MaxClients and ValidateClient. Start and stop it
    /// with <see cref="IUniversalHubHost"/> (or one hub with IHubContext&lt;THub&gt;).
    /// <code>
    /// services.AddUniversalServer(server => server
    ///     .Configure(o => o.LocalName = "Allan's iPad")
    ///     .AddHub&lt;GameHub&gt;("game", hub => hub
    ///         .MaxClients(6)
    ///         .UseBluetoothLE(ServiceUuid, GameCharacteristicUuid)
    ///         .UseHttp("_game._tcp")));
    /// </code>
    /// </summary>
    public static IServiceCollection AddUniversalServer(this IServiceCollection services, Action<UniversalServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(x => x.ServiceType == typeof(UniversalServerMarker)))
            throw new InvalidOperationException("AddUniversalServer can only be called once - add every hub inside it");
        services.AddSingleton<UniversalServerMarker>();

        var server = new UniversalServerBuilder();
        configure(server);
        server.Validate();

        services.AddHubCore();
        foreach (var c in server.ProtocolActions)
            services.ConfigureHubProtocol(c);

        var options = new UniversalServerOptions();
        foreach (var c in server.ServerActions)
            c(options);
        services.AddSingleton(options);

        foreach (var hub in server.Hubs)
            hub.Register(services);

        services.TryAddSingleton<UniversalHubHost>();
        services.TryAddSingleton<IUniversalHubHost>(sp => sp.GetRequiredService<UniversalHubHost>());

        if (server.DynamicHubs != null)
        {
            services.AddSingleton(server.DynamicHubs);
            services.TryAddSingleton<DynamicHubRegistry>();
            services.TryAddSingleton<IDynamicHubs>(sp => sp.GetRequiredService<DynamicHubRegistry>());
        }

        foreach (var setup in server.TransportSetups.Values)
            setup(services, server);

        // registrations add-on transports make once the server is set up
        foreach (var c in server.ServiceActions)
            c(services);

        return services;
    }


    /// <summary>
    /// A hub's name: how clients ask for it on every transport, so it travels in URLs and advertisements
    /// </summary>
    internal static void ValidateHubName(string name)
    {
        if (String.IsNullOrWhiteSpace(name) || !HubName().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a valid hub name - use 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit", nameof(name));
    }


    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex HubName();
}


/// <summary>
/// Sets up a hub server: its own settings, the hubs it serves, and the transports they use
/// </summary>
public sealed class UniversalServerBuilder
{
    readonly Dictionary<Type, List<object>> configurations = new();

    internal UniversalServerBuilder() { }

    internal List<Action<UniversalServerOptions>> ServerActions { get; } = new();
    internal List<Action<HubProtocolOptions>> ProtocolActions { get; } = new();
    internal List<UniversalHubBuilder> Hubs { get; } = new();
    internal DynamicHubOptions? DynamicHubs { get; private set; }

    /// <summary>
    /// Each transport the hubs use, by its type, and how it registers itself
    /// </summary>
    internal Dictionary<Type, Action<IServiceCollection, UniversalServerBuilder>> TransportSetups { get; } = new();

    /// <summary>
    /// Registrations an add-on transport makes once the server is set up
    /// </summary>
    internal List<Action<IServiceCollection>> ServiceActions { get; } = new();


    /// <summary>
    /// The server's own settings, shared by every hub and transport: LocalName, HostId, resume, file transfers, media channels
    /// </summary>
    public UniversalServerBuilder Configure(Action<UniversalServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.ServerActions.Add(configure);
        return this;
    }


    /// <summary>
    /// Protocol limits (payload size, timeouts) - app-wide, so a client in the same app gets them too
    /// </summary>
    public UniversalServerBuilder Protocol(Action<HubProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.ProtocolActions.Add(configure);
        return this;
    }


    /// <summary>
    /// Serves <typeparamref name="THub"/> - a <see cref="UniversalHub{TContract}"/> - over
    /// the transports <paramref name="configure"/> picks (UseBluetoothLE, UseHttp, UseMultipeer, UseNearby; at least one)
    /// </summary>
    /// <param name="name">
    /// The hub's name - how clients ask for it on every transport, so they register the same one. 1 to 64 letters, digits,
    /// '.', '_' or '-'.
    /// </param>
    /// <param name="configure">This hub's rules and transports</param>
    public UniversalServerBuilder AddHub<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>(
        string name,
        Action<UniversalHubBuilder> configure
    ) where THub : class
    {
        UniversalServerExtensions.ValidateHubName(name);
        ArgumentNullException.ThrowIfNull(configure);
        if (this.Hubs.Any(x => x.HubType == typeof(THub)))
            throw new InvalidOperationException($"{typeof(THub).Name} is already added");
        if (this.Hubs.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A hub named {name} is already added - each hub needs its own name");

        var hub = new UniversalHubBuilder(this, name, typeof(THub), v => v.Visit<THub>(name), services =>
        {
            services.AddTransient<THub>();
            services.AddSingleton<IHubContext<THub>>(sp =>
            {
                var host = sp.GetRequiredService<UniversalHubHost>();
                return new HubContext<THub>(() => host.Get(typeof(THub)).Runtime, host);
            });
        });
        configure(hub);
        this.Hubs.Add(hub);
        return this;
    }


    /// <summary>
    /// Lets hubs be defined while the app runs - by a downloaded web app, say - through <see cref="IDynamicHubs"/>. They
    /// are served over every transport this server has set up, and the server may start with no compiled hubs at all.
    /// How many there may be is fixed here; what each transport lets them use is set on that transport (eg.
    /// HttpTransportOptions.DynamicServiceTypes).
    /// </summary>
    public UniversalServerBuilder AllowDynamicHubs(Action<DynamicHubOptions>? configure = null)
    {
        var options = this.DynamicHubs ?? new DynamicHubOptions();
        configure?.Invoke(options);
        if (options.MaxHubs < 1)
            throw new InvalidOperationException("MaxHubs must be at least 1");

        this.DynamicHubs = options;
        return this;
    }


    /// <summary>
    /// Sets up a transport of your own on the server: <typeparamref name="TTransport"/> is created by DI, once, however
    /// many hubs use it. Hubs pick it with <see cref="UniversalHubBuilder.UseTransport{TTransport}"/>, which sets it up
    /// too - call this for a server that only has dynamic hubs, which are served over every transport set up here.
    /// </summary>
    public UniversalServerBuilder AddTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransport>()
        where TTransport : class, IHubTransport
    {
        // a first-party transport's own setup (server.BluetoothLE, UseHttp, ...) wins over this one
        this.TransportSetups.TryAdd(typeof(TTransport), static (services, _) =>
        {
            services.TryAddSingleton<TTransport>();
            services.AddSingleton<IHubTransport>(sp => sp.GetRequiredService<TTransport>());
        });
        return this;
    }


    /// <summary>
    /// Sets up a transport with its own registration - once, however many hubs use it. Replaces a plain
    /// <see cref="AddTransport{TTransport}"/> setup of the same type.
    /// </summary>
    internal void SetUpTransport<TTransport>(Action<IServiceCollection, UniversalServerBuilder> setup) where TTransport : IHubTransport
        => this.TransportSetups[typeof(TTransport)] = setup;


    internal bool IsSetUp<TTransport>() where TTransport : IHubTransport => this.TransportSetups.ContainsKey(typeof(TTransport));


    /// <summary>
    /// Keeps a transport's settings for when it registers itself
    /// </summary>
    internal void Configure<TTransport, T>(Action<T> configure) where TTransport : IHubTransport
    {
        if (!this.configurations.TryGetValue(typeof(TTransport), out var list))
            this.configurations[typeof(TTransport)] = list = new();

        list.Add(configure);
    }


    internal IEnumerable<Action<T>> GetConfigurations<TTransport, T>() where TTransport : IHubTransport
        => this.configurations.TryGetValue(typeof(TTransport), out var list) ? list.OfType<Action<T>>() : [];


    /// <summary>
    /// Registration mistakes throw here, at registration
    /// </summary>
    internal void Validate()
    {
        if (this.Hubs.Count == 0 && this.DynamicHubs == null)
            throw new InvalidOperationException("A universal server needs at least one hub - call AddHub (or AllowDynamicHubs to add them while the app runs)");

        var bare = this.Hubs.FirstOrDefault(x => x.Transports.Count == 0);
        if (bare != null)
            throw new InvalidOperationException($"{bare.Name} isn't served over any transport - call UseBluetoothLE, UseHttp, UseMultipeer, UseNearby or UseTransport on it");

        if (this.DynamicHubs == null)
        {
            var unused = this.TransportSetups.Keys.FirstOrDefault(t => !this.Hubs.Any(x => x.Transports.Contains(t)));
            if (unused != null)
                throw new InvalidOperationException($"{unused.Name} is set up but no hub is served over it - add it to a hub");
        }
        else if (this.TransportSetups.Count == 0)
        {
            throw new InvalidOperationException("Dynamic hubs need at least one transport set up on the server");
        }
    }
}


/// <summary>
/// Sets up one hub: its rules and the transports it is served over
/// </summary>
public sealed class UniversalHubBuilder
{
    readonly Action<IHubTypeVisitor> accept;
    readonly Action<IServiceCollection> register;
    readonly Dictionary<Type, object> settings = new();


    internal UniversalHubBuilder(UniversalServerBuilder server, string name, Type hubType, Action<IHubTypeVisitor> accept, Action<IServiceCollection> register)
    {
        this.Server = server;
        this.Name = name;
        this.HubType = hubType;
        this.accept = accept;
        this.register = register;
    }


    /// <summary>
    /// The hub's name - how clients ask for it on every transport
    /// </summary>
    public string Name { get; }

    internal UniversalServerBuilder Server { get; }
    internal Type HubType { get; }
    internal HashSet<Type> Transports { get; } = new();
    internal HubOptions Options { get; } = new();
    internal IHubSerializer? HubSerializer { get; private set; }


    /// <summary>
    /// The serializer this hub's calls, results and pushes use, instead of the app's IHubSerializer. Its clients must use
    /// one with the same name - the handshake refuses any other.
    /// </summary>
    public UniversalHubBuilder Serializer(IHubSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        this.HubSerializer = serializer;
        return this;
    }


    /// <summary>
    /// The most clients connected at once, counted across every transport - 8 unless set, null for no limit
    /// </summary>
    public UniversalHubBuilder MaxClients(int? maxClients)
    {
        if (maxClients < 1)
            throw new ArgumentOutOfRangeException(nameof(maxClients), "MaxClients must be at least 1, or null for no limit");

        this.Options.MaxClients = maxClients;
        return this;
    }


    /// <summary>
    /// A gate for incoming clients, on every transport - return a rejection reason to refuse, or null to accept. It also
    /// sees a connected client's new name when it renames itself.
    /// </summary>
    public UniversalHubBuilder ValidateClient(Func<HandshakeInfo, string?> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        this.Options.ValidateClient = validate;
        return this;
    }


    /// <summary>
    /// Serves the hub over a transport of your own, setting it up on the server too (<see cref="UniversalServerBuilder.AddTransport{TTransport}"/>).
    /// </summary>
    /// <param name="settings">
    /// What the transport needs to know about this hub - it reads it back with <see cref="IHubBinding.GetSettings{T}"/>,
    /// by this object's type
    /// </param>
    public UniversalHubBuilder UseTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransport>(object? settings = null)
        where TTransport : class, IHubTransport
    {
        this.Use<TTransport>(settings);
        this.Server.AddTransport<TTransport>();
        return this;
    }


    /// <summary>
    /// Serves the hub over <typeparamref name="TTransport"/>, with what that transport needs to know about it - the caller
    /// sets the transport up
    /// </summary>
    internal void Use<TTransport>(object? transportSettings) where TTransport : IHubTransport
    {
        if (!this.Transports.Add(typeof(TTransport)))
            throw new InvalidOperationException($"{this.Name} is already served over {typeof(TTransport).Name}");

        if (transportSettings != null)
            this.settings[transportSettings.GetType()] = transportSettings;
    }


    internal T? GetSettings<T>() where T : class => this.settings.TryGetValue(typeof(T), out var value) ? (T)value : null;

    internal void Accept(IHubTypeVisitor visitor) => this.accept(visitor);


    internal void Register(IServiceCollection services)
    {
        this.register(services);
        services.AddSingleton(new HubRegistration(this.HubType, this.Name, this.Options, this.Transports.ToHashSet(), new Dictionary<Type, object>(this.settings), this.HubSerializer));
    }
}


/// <summary>
/// Lets a transport register what it needs per compiled hub type, without reflection
/// </summary>
internal interface IHubTypeVisitor
{
    void Visit<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>(string key) where THub : class;
}


internal sealed class UniversalServerMarker;
