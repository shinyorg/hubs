using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.BluetoothLE.Hubs;

public static class HostServiceCollectionExtensions
{
    /// <summary>
    /// Registers a hub server: every hub it serves, the host settings and the protocol limits, in one call. Start and stop
    /// it with <see cref="IBleHubHost"/> (or one hub with IHubContext&lt;THub&gt;). On Android, iOS and Mac Catalyst it adds
    /// the BLE hosting stack (AddBluetoothLeHosting) too; on plain .NET, register an IBleHostingManager yourself.
    /// <code>
    /// services.AddBleHubServer(server => server
    ///     .ServiceUuid(MyServiceUuid)                       // optional - clients must use the same one
    ///     .Host(o => o.EnableFileTransfers(filesDir))
    ///     .AddHub&lt;GameHub&gt;(GameCharacteristicUuid, o => o.MaxClients = 6)
    ///     .AddHub&lt;ChatHub&gt;(ChatCharacteristicUuid));
    /// </code>
    /// </summary>
    public static IServiceCollection AddBleHubServer(this IServiceCollection services, Action<BleHubServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(x => x.ServiceType == typeof(BleHubServerMarker)))
            throw new InvalidOperationException("AddBleHubServer can only be called once - add every hub inside it");

        var server = new BleHubServerBuilder();
        configure(server);
        if (server.Hubs.Count == 0)
            throw new InvalidOperationException("A hub server needs at least one hub - call AddHub");

        services.AddSingleton<BleHubServerMarker>();
#if ANDROID || IOS || MACCATALYST
        // the platform BLE peripheral stack - a no-op when the app already added it
        services.AddBluetoothLeHosting();
#endif
        services.AddBleHubCore();
        foreach (var c in server.ProtocolActions)
            services.ConfigureBleHubProtocol(c);

        foreach (var c in server.HostActions)
            services.AddSingleton(new ConfigureBleHubHostOptions(c));

        foreach (var hub in server.Hubs)
            hub(services);

        services.TryAddSingleton<BleHubHost>();
        services.TryAddSingleton<IBleHubHost>(sp => sp.GetRequiredService<BleHubHost>());
        services.TryAddSingleton(sp =>
        {
            var o = new BleHubHostOptions();
            foreach (var c in sp.GetServices<ConfigureBleHubHostOptions>())
                c.Configure(o);
            return o;
        });
        return services;
    }
}


/// <summary>
/// Sets up a hub server: its service UUID, host settings, protocol limits and the hubs it serves
/// </summary>
public sealed class BleHubServerBuilder
{
    internal BleHubServerBuilder() { }

    internal List<Action<BleHubHostOptions>> HostActions { get; } = new();
    internal List<Action<BleHubProtocolOptions>> ProtocolActions { get; } = new();
    internal List<Action<IServiceCollection>> Hubs { get; } = new();
    readonly HashSet<Type> hubTypes = new();
    readonly HashSet<string> characteristics = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// The one service every hub lives in - <see cref="BleHubProtocolOptions.DefaultServiceUuid"/> unless set. Clients
    /// scan for it, so theirs must match.
    /// </summary>
    public BleHubServerBuilder ServiceUuid(string serviceUuid)
    {
        var uuid = BleUuid.Normalize(serviceUuid, nameof(serviceUuid));
        return this.Host(o => o.ServiceUuid = uuid);
    }

    /// <summary>
    /// Host settings shared by every hub: LocalName, file transfers, the client sweep
    /// </summary>
    public BleHubServerBuilder Host(Action<BleHubHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.HostActions.Add(configure);
        return this;
    }

    /// <summary>
    /// Protocol limits (payload size, timeouts) - app-wide, so a client in the same app gets them too
    /// </summary>
    public BleHubServerBuilder Protocol(Action<BleHubProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.ProtocolActions.Add(configure);
        return this;
    }

    /// <summary>
    /// Serves <typeparamref name="THub"/> on its own write+notify characteristic inside the server's service
    /// </summary>
    /// <param name="characteristicUuid">One per hub - it's how clients find the hub inside the service</param>
    /// <param name="configure">This hub's rules: MaxClients, ValidateClient</param>
    public BleHubServerBuilder AddHub<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>(
        string characteristicUuid,
        Action<BleHubOptions>? configure = null
    ) where THub : class
    {
        var uuid = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));
        if (!this.hubTypes.Add(typeof(THub)))
            throw new InvalidOperationException($"{typeof(THub).Name} is already added");
        if (!this.characteristics.Add(uuid))
            throw new InvalidOperationException($"Characteristic {uuid} is already used by another hub - each hub needs its own");

        var options = new BleHubOptions();
        configure?.Invoke(options);

        this.Hubs.Add(services =>
        {
            services.AddTransient<THub>();
            services.AddSingleton(new BleHubRegistration(typeof(THub), uuid, options));
            services.AddSingleton<IHubContext<THub>>(sp =>
            {
                var host = sp.GetRequiredService<BleHubHost>();
                return new HubContext<THub>(() => host.GetRuntime(typeof(THub)), host);
            });
        });
        return this;
    }
}


internal sealed class ConfigureBleHubHostOptions(Action<BleHubHostOptions> configure)
{
    public void Configure(BleHubHostOptions options) => configure(options);
}

internal sealed class BleHubServerMarker;
