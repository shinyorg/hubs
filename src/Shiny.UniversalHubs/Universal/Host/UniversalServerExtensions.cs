using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;

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

        services.AddBleHubCore();
        foreach (var c in server.ProtocolActions)
            services.ConfigureBleHubProtocol(c);

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
    readonly Dictionary<HubTransports, List<object>> configurations = new();

    internal UniversalServerBuilder() { }

    internal List<Action<UniversalServerOptions>> ServerActions { get; } = new();
    internal List<Action<BleHubProtocolOptions>> ProtocolActions { get; } = new();
    internal List<UniversalHubBuilder> Hubs { get; } = new();
    internal DynamicHubOptions? DynamicHubs { get; private set; }

    /// <summary>
    /// Each transport the hubs use, and how it registers itself
    /// </summary>
    internal Dictionary<HubTransports, Action<IServiceCollection, UniversalServerBuilder>> TransportSetups { get; } = new();

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
    public UniversalServerBuilder Protocol(Action<BleHubProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.ProtocolActions.Add(configure);
        return this;
    }


    /// <summary>
    /// Serves <typeparamref name="THub"/> - a <see cref="UniversalHub{TContract}"/> or <see cref="BleHub{TContract}"/> - over
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
    /// What they may use is fixed here, because the platforms fix it when the app is built: the mDNS service types (iOS
    /// lists them in NSBonjourServices) and how many there may be.
    /// </summary>
    public UniversalServerBuilder AllowDynamicHubs(Action<DynamicHubOptions>? configure = null)
    {
        var options = this.DynamicHubs ?? new DynamicHubOptions();
        configure?.Invoke(options);
        foreach (var type in options.ServiceTypes)
            UniversalHubProtocol.ValidateServiceType(type);
        if (options.MaxHubs < 1)
            throw new InvalidOperationException("MaxHubs must be at least 1");

        this.DynamicHubs = options;
        return this;
    }


    /// <summary>
    /// Sets up a transport - once, however many hubs use it
    /// </summary>
    internal void SetUpTransport(HubTransports transport, Action<IServiceCollection, UniversalServerBuilder> setup)
        => this.TransportSetups.TryAdd(transport, setup);


    /// <summary>
    /// Keeps a transport's settings for when it registers itself
    /// </summary>
    internal void Configure<T>(HubTransports transport, Action<T> configure)
    {
        if (!this.configurations.TryGetValue(transport, out var list))
            this.configurations[transport] = list = new();

        list.Add(configure);
    }


    internal IEnumerable<Action<T>> GetConfigurations<T>(HubTransports transport)
        => this.configurations.TryGetValue(transport, out var list) ? list.OfType<Action<T>>() : [];


    /// <summary>
    /// Registration mistakes throw here, at registration
    /// </summary>
    internal void Validate()
    {
        if (this.Hubs.Count == 0 && this.DynamicHubs == null)
            throw new InvalidOperationException("A universal server needs at least one hub - call AddHub (or AllowDynamicHubs to add them while the app runs)");

        foreach (var hub in this.Hubs)
        {
            if (hub.Transports == HubTransports.None)
                throw new InvalidOperationException($"{hub.Name} isn't served over any transport - call UseBluetoothLE, UseHttp, UseMultipeer or UseNearby on it");

            foreach (var transport in Enum.GetValues<HubTransports>().Where(x => x is not (HubTransports.None or HubTransports.All) && hub.Transports.HasFlag(x)))
            {
                if (!this.TransportSetups.ContainsKey(transport))
                    throw new InvalidOperationException($"{hub.Name} is served over {transport}, which isn't set up on the server - call server.{transport}(...)");
            }
        }

        if (this.DynamicHubs == null)
        {
            var unused = this.TransportSetups.Keys.FirstOrDefault(t => !this.Hubs.Any(x => x.Transports.HasFlag(t)));
            if (unused != HubTransports.None)
                throw new InvalidOperationException($"{unused} is set up but no hub is served over it - call Use{unused} on a hub");
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
    internal HubTransports Transports { get; private set; }
    internal BleHubOptions Options { get; } = new();


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
    /// Serves the hub over <paramref name="transport"/>, with what that transport needs to know about it
    /// </summary>
    internal void Use(HubTransports transport, object transportSettings)
    {
        if (this.Transports.HasFlag(transport))
            throw new InvalidOperationException($"{this.Name} is already served over {transport}");

        this.Transports |= transport;
        this.settings[transportSettings.GetType()] = transportSettings;
    }


    internal T? GetSettings<T>() where T : class => this.settings.TryGetValue(typeof(T), out var value) ? (T)value : null;

    internal void Accept(IHubTypeVisitor visitor) => this.accept(visitor);


    internal void Register(IServiceCollection services)
    {
        this.register(services);
        services.AddSingleton(new HubRegistration(this.HubType, this.Name, this.Options, this.Transports, new Dictionary<Type, object>(this.settings)));
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
