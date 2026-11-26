using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

public static class BleHubExtensions
{
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

        hub.Use(HubTransports.Ble, new BleHubSettings(service, characteristic));
        hub.Server.SetUpTransport(HubTransports.Ble, AddBleTransport);
        return hub;
    }


    /// <summary>
    /// BLE hosting settings beside the server's own: the client sweep
    /// </summary>
    public static UniversalServerBuilder BluetoothLE(this UniversalServerBuilder server, Action<BluetoothLETransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);
        server.Configure(HubTransports.Ble, configure);
        server.SetUpTransport(HubTransports.Ble, AddBleTransport);
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
        foreach (var c in server.GetConfigurations<BluetoothLETransportOptions>(HubTransports.Ble))
            c(options);

        services.AddSingleton(options);
        services.TryAddSingleton<BleHubTransport>();
        services.AddSingleton<IUniversalHubTransport>(sp => sp.GetRequiredService<BleHubTransport>());
    }
}
