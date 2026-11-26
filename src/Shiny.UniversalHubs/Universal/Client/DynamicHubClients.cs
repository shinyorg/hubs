using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

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
/// any universal hub client; its <see cref="IBleHubClient{TContract}.Hub"/> calls by name. Register with
/// <c>services.AddDynamicHubClients(o => ...)</c>.
/// </summary>
public interface IDynamicHubClients
{
    /// <summary>
    /// A new, disconnected client for the hub on <paramref name="characteristicUuid"/>, browsing for
    /// <paramref name="mdnsServiceType"/>. Dispose it when done.
    /// </summary>
    IUniversalHubClient<IDynamicHubCalls> Create(string characteristicUuid, string mdnsServiceType);
}


public static class DynamicHubClientExtensions
{
    /// <summary>
    /// Registers <see cref="IDynamicHubClients"/>: hub clients created while the app runs, for hubs it has no compiled
    /// contract for. <paramref name="configure"/> holds what every one shares - the host's BLE ServiceUuid, timeouts,
    /// Multipeer - and runs again for each client; the mDNS service type is given per client. On Android, iOS and Mac
    /// Catalyst it adds the BLE stack too; on plain .NET, register an IBleManager yourself to use BLE.
    /// </summary>
    public static IServiceCollection AddDynamicHubClients(this IServiceCollection services, Action<UniversalHubClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(x => x.ServiceType == typeof(IDynamicHubClients)))
            throw new InvalidOperationException("AddDynamicHubClients can only be called once");

        services.AddHubClientCore();
        services.AddHttpServerLocator();

        // add-on transports (Multipeer) register once, whatever the client
        var shared = new UniversalHubClientOptions();
        configure?.Invoke(shared);
        // and BLE and HTTP, which every dynamic hub client uses
        shared.UseBluetoothLE(shared.DynamicBleServiceUuid, BleHubProtocolOptions.DefaultServiceUuid).UseHttp();
        foreach (var c in shared.ServiceActions)
            c(services);

        services.AddSingleton<IDynamicHubClients>(sp => new DynamicHubClients(sp, configure));
        return services;
    }
}


internal sealed class DynamicHubClients(IServiceProvider services, Action<UniversalHubClientOptions>? configure) : IDynamicHubClients
{
    public IUniversalHubClient<IDynamicHubCalls> Create(string characteristicUuid, string mdnsServiceType)
    {
        UniversalHubProtocol.ValidateServiceType(mdnsServiceType);

        var characteristic = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));
        var options = new UniversalHubClientOptions();
        configure?.Invoke(options);

        // a dynamic hub is served over BLE and HTTP under its characteristic, which is also its name
        options.UseBluetoothLE(options.DynamicBleServiceUuid, characteristic).UseHttp(mdnsServiceType);

        var proxy = new DynamicHubProxy(services.GetRequiredService<BleHubClientServices>(), characteristic);
        return new UniversalHubClient<IDynamicHubCalls>(
            proxy,
            () => services.GetService<IHttpServerLocator>(),
            options,
            services.GetService<ILoggerFactory>(),
            () => options.Transports.Select(x => x(services)).ToList()
        );
    }
}


/// <summary>
/// The proxy a dynamic hub client calls through - what the generator writes for a contract, by name instead
/// </summary>
internal sealed class DynamicHubProxy(BleHubClientServices services, string hubName)
    : BleHubClient(services, hubName), IDynamicHubCalls, IBleHubClient<IDynamicHubCalls>
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

    protected override void OnPush(string name, BleHubArgumentReader args)
    {
        var values = new ReadOnlyMemory<byte>[args.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = args.ReadSerialized().ToArray();

        this.Pushed?.Invoke(name, values);
    }

    BleHubArgumentWriter Write(IReadOnlyList<ReadOnlyMemory<byte>> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var writer = this.CreateArguments();
        foreach (var argument in arguments)
            writer.WriteSerialized(argument.Span);

        return writer;
    }
}
