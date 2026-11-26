using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;

namespace Shiny.UniversalHubs;

public static class BleClientExtensions
{
    /// <summary>
    /// Reaches the hub over Bluetooth LE: scans for hosts advertising <paramref name="serviceUuid"/> and connects to the
    /// hub's characteristic in it. On Android, iOS and Mac Catalyst it adds the BLE stack (AddBluetoothLE); on plain .NET,
    /// register an IBleManager yourself.
    /// </summary>
    /// <param name="serviceUuid">The GATT service the host gave the hub's UseBluetoothLE</param>
    /// <param name="characteristicUuid">The hub's characteristic, as the host set it</param>
    public static UniversalHubClientOptions UseBluetoothLE(this UniversalHubClientOptions options, string serviceUuid, string characteristicUuid)
    {
        ArgumentNullException.ThrowIfNull(options);
        var service = BleUuid.Normalize(serviceUuid, nameof(serviceUuid));
        var characteristic = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));

        return options.UseTransport(
            sp => new BleClientTransport(
                service,
                characteristic,
                sp.GetService<IBleManager>(),
                sp.GetRequiredService<BleConnectionTracker>(),
                sp.GetService<LocalHubAdvertisement>()
            ),
            AddBluetoothLE
        );
    }


    static void AddBluetoothLE(IServiceCollection services)
    {
#if IOS || MACCATALYST
        // the platform BLE central stack - a no-op when the app already added it. Hubs are a foreground thing, so iOS isn't
        // asked to alert the user about connections and notifications while the app is in the background (Shiny's
        // defaults). Call AddBluetoothLE(config) before this to use your own AppleBleConfiguration.
        services.AddBluetoothLE(new AppleBleConfiguration
        {
            NotifyOnConnection = false,
            NotifyOnDisconnection = false,
            NotifyOnNotification = false
        });
#elif ANDROID
        // the platform BLE central stack - a no-op when the app already added it
        services.AddBluetoothLE();
#endif
        services.TryAddSingleton<BleConnectionTracker>();
    }
}


/// <summary>
/// A host seen over Bluetooth LE
/// </summary>
/// <param name="Peripheral">The host's peripheral</param>
/// <param name="Name">Its advertised local name, else the peripheral's name</param>
/// <param name="Rssi">Signal strength when it was last seen</param>
public sealed record BleHubEndpoint(IPeripheral Peripheral, string? Name, int Rssi) : HubEndpoint
{
    public string Id => this.Peripheral.Uuid;
}


/// <summary>
/// A hub client's BLE side: scans for the hub's service, and connects to its characteristic over GATT. Added with
/// <c>o.UseBluetoothLE(...)</c>.
/// </summary>
public sealed class BleClientTransport : IHubClientTransport
{
    readonly string serviceUuid;
    readonly string characteristicUuid;
    readonly IBleManager? ble;
    readonly BleConnectionTracker connections;
    readonly LocalHubAdvertisement? localAdvertisement;


    internal BleClientTransport(
        string serviceUuid,
        string characteristicUuid,
        IBleManager? ble,
        BleConnectionTracker connections,
        LocalHubAdvertisement? localAdvertisement
    )
    {
        this.serviceUuid = serviceUuid;
        this.characteristicUuid = characteristicUuid;
        this.ble = ble;
        this.connections = connections;
        this.localAdvertisement = localAdvertisement;
    }


    public string DisplayName => "Bluetooth LE";
    public bool IsSupported => true;

    IBleManager Ble => ble ?? throw new InvalidOperationException("IBleManager is not registered - call AddBluetoothLE()");


    public IObservable<HubHostSighting> Discover(HubClientContext client) => Observable
        .FromAsync(() => this.Ble.RequestAccessAsync())
        .SelectMany(access => access == AccessState.Available
            ? this.Ble.Scan(new ScanConfig(serviceUuid))
            : Observable.Throw<ScanResult>(new HubException($"Bluetooth is not available ({access})"))
        )
        // a scan on the device that is hosting can report the device's own advertisement - checked per result, since
        // this app's host can start or stop advertising mid-scan
        .Where(x => localAdvertisement?.IsLocal(serviceUuid, x.AdvertisementData?.LocalName) != true)
        .Select(x =>
        {
            var found = new BleHubEndpoint(x.Peripheral, x.AdvertisementData?.LocalName ?? x.Peripheral.Name, x.Rssi);
            return new HubHostSighting(found.Name ?? found.Id, null, found);
        });


    public Task Connect(HubClientContext client, HubHostInfo host, CancellationToken cancellationToken)
    {
        var found = host.Endpoint<BleHubEndpoint>() ?? throw new HubException($"{host.Name} was not seen over Bluetooth LE");
        var ble = this.Ble;
        if (ble.IsScanning)
            ble.StopScan();

        // A scan started since this host was discovered drops it from the manager's cache (Shiny does that on Apple), and
        // the connection's events then go to the manager's new instance - connecting the old one waits forever. Connect
        // the manager's current instance for the same device.
        var peripheral = found.Peripheral;
        if (ble.GetKnownPeripheral(peripheral.Uuid) is { } current && !ReferenceEquals(current, peripheral))
        {
            client.Logger?.LogDebug("Using the current instance of {Peripheral} - the discovered one went stale", peripheral.Uuid);
            peripheral = current;
        }

        return client.Connect(events =>
        {
            this.connections.Acquire(peripheral.Uuid);
            var link = new BleFrameLink(peripheral, this.serviceUuid, this.characteristicUuid, this.connections, client.Logger);
            return new FrameClientTransport(client.Client.Services.Options, link, events, client.Logger);
        }, cancellationToken);
    }
}


/// <summary>
/// Several hub clients can share one BLE connection to a host - only the last one out disconnects
/// </summary>
internal sealed class BleConnectionTracker
{
    readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public void Acquire(string peripheralId)
    {
        lock (this.counts)
            this.counts[peripheralId] = this.counts.TryGetValue(peripheralId, out var c) ? c + 1 : 1;
    }

    /// <summary>
    /// Returns true when nobody else is using the connection
    /// </summary>
    public bool Release(string peripheralId)
    {
        lock (this.counts)
        {
            if (!this.counts.TryGetValue(peripheralId, out var c) || c <= 1)
            {
                this.counts.Remove(peripheralId);
                return true;
            }
            this.counts[peripheralId] = c - 1;
            return false;
        }
    }
}
