using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hosting;

namespace Shiny.UniversalHubs;

public static class BleConnectionExtensions
{
    /// <summary>
    /// The BLE central the client is connected through - null on the other transports. A client that resumed is on the
    /// central it came back on.
    /// </summary>
    public static IPeripheral? Peripheral(this HubConnection connection) => connection.Get<IPeripheral>();

    /// <inheritdoc cref="Peripheral(HubConnection)"/>
    public static IPeripheral? Peripheral(this HubConnectedClient client) => client.Get<IPeripheral>();
}


public static class BleHostExtensions
{
    /// <summary>
    /// Serves this dynamic hub over Bluetooth LE too, as <paramref name="characteristicUuid"/> - without it, a dynamic hub
    /// isn't served over BLE
    /// </summary>
    /// <param name="serviceUuid">The GATT service - BluetoothLETransportOptions.DynamicServiceUuid, else the compiled hubs' service, unless given</param>
    public static DynamicHubDefinition UseBluetoothLE(this DynamicHubDefinition definition, string characteristicUuid, string? serviceUuid = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var characteristic = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));
        var service = serviceUuid == null ? null : BleUuid.Normalize(serviceUuid, nameof(serviceUuid));
        return definition.With(new BleDynamicHubSettings(service, characteristic));
    }


    /// <summary>
    /// Serves this hub over Bluetooth LE: a write+notify characteristic in a GATT service, with files and media channels
    /// over L2CAP. On Android, iOS and Mac Catalyst it adds the BLE hosting stack (AddBluetoothLeHosting); on plain .NET,
    /// register an IBleHostingManager yourself.
    /// </summary>
    /// <param name="serviceUuid">The GATT service the hub is in - it is advertised, and its clients scan for it</param>
    /// <param name="characteristicUuid">The hub's characteristic - one per hub in a service</param>
    public static UniversalHubBuilder UseBluetoothLE(this UniversalHubBuilder hub, string serviceUuid, string characteristicUuid)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var service = BleUuid.Normalize(serviceUuid, nameof(serviceUuid));
        var characteristic = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));

        hub.Use<BleHostTransport>(new BleHubSettings(service, characteristic));
        hub.Server.SetUpTransport<BleHostTransport>(AddBleTransport);
        return hub;
    }


    /// <summary>
    /// BLE hosting settings beside the server's own: the client sweep
    /// </summary>
    public static UniversalServerBuilder BluetoothLE(this UniversalServerBuilder server, Action<BluetoothLETransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);
        server.Configure<BleHostTransport, BluetoothLETransportOptions>(configure);
        server.SetUpTransport<BleHostTransport>(AddBleTransport);
        return server;
    }


    static void AddBleTransport(IServiceCollection services, UniversalServerBuilder server)
    {
        var hubs = server.Hubs
            .Select(x => (Hub: x, Settings: x.GetSettings<BleHubSettings>()))
            .Where(x => x.Settings != null)
            .ToList();

        // one GATT service for now - every hub is a characteristic in it
        var serviceUuids = hubs.Select(x => x.Settings!.ServiceUuid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (serviceUuids.Count > 1)
            throw new InvalidOperationException($"Every hub served over BLE must use the same service UUID - found {String.Join(", ", serviceUuids)}");

        var duplicate = hubs.GroupBy(x => x.Settings!.CharacteristicUuid, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Hubs {String.Join(", ", duplicate.Select(x => x.Hub.Name))} share characteristic {duplicate.Key} - each hub needs its own");

#if ANDROID || IOS || MACCATALYST
        // the platform BLE peripheral stack - a no-op when the app already added it
        services.AddBluetoothLeHosting();
#endif
        var options = new BluetoothLETransportOptions();
        foreach (var c in server.GetConfigurations<BleHostTransport, BluetoothLETransportOptions>())
            c(options);

        services.AddSingleton(options);
        services.TryAddSingleton(sp => new BleHostTransport(
            sp.GetRequiredService<IBleHostingManager>(),
            sp.GetRequiredService<UniversalServerOptions>(),
            sp.GetRequiredService<BluetoothLETransportOptions>(),
            sp.GetRequiredService<HubProtocolOptions>(),
            sp,
            sp.GetService<ILoggerFactory>(),
            sp.GetService<LocalHubAdvertisement>()
        ));
        services.AddSingleton<IHubTransport>(sp => sp.GetRequiredService<BleHostTransport>());
    }
}
