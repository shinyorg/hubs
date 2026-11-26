using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.UniversalHubs;

/// <summary>
/// A hub's methods and events by name, with values exactly as each side's serializer wrote them - the counterpart of a
/// generated proxy for a hub whose contract isn't compiled into this app (a dynamic hub, say). JSON with the default
/// serializer.
/// </summary>
public interface IDynamicHubCalls
{
    /// <summary>
    /// Calls a method. The result is its serialized value, empty for a method with none.
    /// </summary>
    Task<ReadOnlyMemory<byte>> Invoke(string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls a streaming method; each item is serialized
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host raised an event: its name and serialized arguments, one at a time in the order they were sent
    /// </summary>
    event Action<string, IReadOnlyList<ReadOnlyMemory<byte>>>? Pushed;
}


/// <summary>
/// Creates hub clients while the app runs, for hubs it has no compiled contract for. Each one discovers and connects like
/// any universal hub client; its <see cref="IHubProxy{TContract}.Hub"/> calls by name. Register with
/// <c>services.AddDynamicHubClients(o => ...)</c>.
/// </summary>
public interface IDynamicHubClients
{
    /// <summary>
    /// A new, disconnected client for the hub named <paramref name="hubName"/>. <paramref name="configure"/> picks its
    /// transports with the hub's own settings (<c>o.UseHttp("_game._tcp").UseBluetoothLE(service, characteristic)</c>) -
    /// replacing the shared ones from AddDynamicHubClients. Dispose it when done.
    /// </summary>
    IUniversalHubClient<IDynamicHubCalls> Create(string hubName, Action<UniversalHubClientOptions>? configure = null);
}


public static class DynamicHubClientExtensions
{
    /// <summary>
    /// Registers <see cref="IDynamicHubClients"/>: hub clients created while the app runs, for hubs it has no compiled
    /// contract for. <paramref name="configure"/> holds what every one shares, and runs again for each client: set up every
    /// transport the clients will use here, so what they need is registered once (<c>o.UseHttp().UseMultipeer("game")</c>);
    /// each client may set one up again with its hub's settings, replacing the shared one.
    /// </summary>
    public static IServiceCollection AddDynamicHubClients(this IServiceCollection services, Action<UniversalHubClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(x => x.ServiceType == typeof(IDynamicHubClients)))
            throw new InvalidOperationException("AddDynamicHubClients can only be called once");

        services.AddHubClientCore();

        // what the shared transports need, registered once whatever the client
        var shared = new UniversalHubClientOptions();
        configure(shared);
        foreach (var c in shared.ProtocolActions)
            services.ConfigureHubProtocol(c);
        foreach (var c in shared.ServiceActions)
            c(services);

        services.AddSingleton<IDynamicHubClients>(sp => new DynamicHubClients(sp, configure));
        return services;
    }
}


internal sealed class DynamicHubClients(IServiceProvider services, Action<UniversalHubClientOptions> configure) : IDynamicHubClients
{
    public IUniversalHubClient<IDynamicHubCalls> Create(string hubName, Action<UniversalHubClientOptions>? perHub = null)
    {
        UniversalServerExtensions.ValidateHubName(hubName);

        var options = new UniversalHubClientOptions { ReplaceTransports = true };
        configure(options);
        perHub?.Invoke(options);
        if (options.TransportCount == 0)
            throw new InvalidOperationException($"The {hubName} client isn't set up with any transport");

        // dynamic hubs are JSON - the values travel exactly as each side wrote them
        var shared = services.GetRequiredService<HubClientServices>();
        var json = shared.Serializer as JsonHubSerializer ?? new JsonHubSerializer(services.GetService<ISerializer>());
        var proxy = new DynamicHubProxy(new HubClientServices(shared.Options, json, shared.LoggerFactory), hubName);
        return new UniversalHubClient<IDynamicHubCalls>(
            proxy,
            options,
            () => options.CreateTransports(services),
            services.GetService<ILoggerFactory>()
        );
    }
}


/// <summary>
/// The proxy a dynamic hub client calls through - what the generator writes for a contract, by name instead
/// </summary>
internal sealed class DynamicHubProxy(HubClientServices services, string hubName)
    : HubProxy(services, hubName), IDynamicHubCalls, IHubProxy<IDynamicHubCalls>
{
    public IDynamicHubCalls Hub => this;

    public event Action<string, IReadOnlyList<ReadOnlyMemory<byte>>>? Pushed;

    public Task<ReadOnlyMemory<byte>> Invoke(string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments, CancellationToken cancellationToken = default)
        => this.InvokeSerializedCore(method, this.Write(arguments), cancellationToken);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, IReadOnlyList<ReadOnlyMemory<byte>> arguments, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in this.StreamSerializedCore(method, this.Write(arguments), cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    protected override void OnPush(string name, HubArgumentReader args)
    {
        var values = new ReadOnlyMemory<byte>[args.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = args.ReadSerialized().ToArray();

        this.Pushed?.Invoke(name, values);
    }

    HubArgumentWriter Write(IReadOnlyList<ReadOnlyMemory<byte>> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var writer = this.CreateArguments();
        foreach (var argument in arguments)
            writer.WriteSerialized(argument.Span);

        return writer;
    }
}
