using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Shiny.BluetoothLE;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// A central whose scan results are pushed by the test - enough to drive BleHubClient.Discover
/// </summary>
public class FakeBleManager : IBleManager
{
    readonly Subject<ScanResult> scan = new();

    public ScanConfig? LastScan { get; private set; }

    /// <summary>
    /// Reports an advertisement to whoever is scanning
    /// </summary>
    public void Advertise(string peripheralId, string? localName, int rssi = -50)
        => this.scan.OnNext(new ScanResult(new FakeScanPeripheral(peripheralId, localName), rssi, new FakeAdvertisementData(localName)));

    public AccessState CurrentAccess => AccessState.Available;
    public IObservable<AccessState> RequestAccess() => Observable.Return(AccessState.Available);
    /// <summary>
    /// The manager's current instance of each device - what Shiny hands back after a new scan has replaced the one
    /// that was discovered
    /// </summary>
    public Dictionary<string, IPeripheral> Known { get; } = new();

    public IPeripheral? GetKnownPeripheral(string peripheralUuid) => this.Known.GetValueOrDefault(peripheralUuid);
    public bool IsScanning { get; private set; }
    public void StopScan() => this.IsScanning = false;
    public IEnumerable<IPeripheral> GetConnectedPeripherals() => [];

    public IObservable<ScanResult> Scan(ScanConfig? scanConfig = null)
    {
        this.LastScan = scanConfig;
        this.IsScanning = true;
        return this.scan;
    }
}


public record FakeAdvertisementData(string? LocalName) : IAdvertisementData
{
    public bool? IsConnectable => true;
    public AdvertisementServiceData[]? ServiceData => null;
    public ManufacturerData? ManufacturerData => null;
    public string[]? ServiceUuids => null;
    public int? TxPower => null;
}


/// <summary>
/// A peripheral that is only ever seen in a scan - never connected to
/// </summary>
public class FakeScanPeripheral(string uuid, string? name, int mtu = BleConstants.DefaultPayloadSize) : IPeripheral
{
    public string Uuid => uuid;
    public string? Name => name;

    /// <summary>
    /// The payload size, as Shiny reports it (ATT MTU minus 3)
    /// </summary>
    public int Mtu { get; protected set; } = mtu;
    public ConnectionState Status => ConnectionState.Disconnected;

    public void Connect(ConnectionConfig? config) => throw new NotSupportedException();
    public void CancelConnection() { }
    public IObservable<ConnectionState> WhenStatusChanged() => Observable.Never<ConnectionState>();
    public IObservable<BleException> WhenConnectionFailed() => Observable.Never<BleException>();
    public IObservable<Unit> WhenServicesChanged() => Observable.Never<Unit>();
    public IObservable<int> ReadRssi() => throw new NotSupportedException();
    public IObservable<BleServiceInfo> GetService(string serviceUuid) => throw new NotSupportedException();
    public IObservable<IReadOnlyList<BleServiceInfo>> GetServices() => throw new NotSupportedException();
    public IObservable<BleCharacteristicInfo> GetCharacteristic(string serviceUuid, string characteristicUuid) => throw new NotSupportedException();
    public IObservable<IReadOnlyList<BleCharacteristicInfo>> GetCharacteristics(string serviceUuid) => throw new NotSupportedException();
    public IObservable<BleCharacteristicResult> NotifyCharacteristic(string serviceUuid, string characteristicUuid, bool useIndicationsIfAvailable = true) => throw new NotSupportedException();
    public IObservable<BleCharacteristicInfo> WhenCharacteristicSubscriptionChanged(string serviceUuid, string characteristicUuid) => throw new NotSupportedException();
    public IObservable<BleCharacteristicResult> ReadCharacteristic(string serviceUuid, string characteristicUuid) => throw new NotSupportedException();
    public IObservable<BleCharacteristicResult> WriteCharacteristic(string serviceUuid, string characteristicUuid, byte[] data, bool withResponse = true) => throw new NotSupportedException();
    public IObservable<BleDescriptorInfo> GetDescriptor(string serviceUuid, string characteristicUuid, string descriptorUuid) => throw new NotSupportedException();
    public IObservable<IReadOnlyList<BleDescriptorInfo>> GetDescriptors(string serviceUuid, string characteristicUuid) => throw new NotSupportedException();
    public IObservable<BleDescriptorResult> ReadDescriptor(string serviceUuid, string characteristicUuid, string descriptorUuid) => throw new NotSupportedException();
    public IObservable<BleDescriptorResult> WriteDescriptor(string serviceUuid, string characteristicUuid, string descriptorUuid, byte[] data) => throw new NotSupportedException();
}


/// <summary>
/// A peripheral that negotiates the ATT MTU like Android: grants up to <paramref name="maxAttMtu"/>, reports the payload size
/// </summary>
public class FakeMtuPeripheral(string uuid, int maxAttMtu = 517, Exception? fail = null) : FakeScanPeripheral(uuid, null), ICanRequestMtu
{
    public int? Requested { get; private set; }

    public IObservable<int> RequestMtu(int requestValue)
    {
        this.Requested = requestValue;
        if (fail != null)
            return Observable.Throw<int>(fail);

        this.Mtu = Math.Min(requestValue, maxAttMtu) - BleConstants.AttHeaderSize;
        return Observable.Return(this.Mtu);
    }
}


/// <summary>
/// A peripheral that records a connect attempt and fails it straight away
/// </summary>
public class FailingConnectPeripheral(string uuid) : IPeripheral
{
    readonly Subject<BleException> failed = new();
    readonly FakeScanPeripheral inner = new(uuid, null);

    public bool ConnectCalled { get; private set; }

    public string Uuid => uuid;
    public string? Name => null;
    public int Mtu => this.inner.Mtu;
    public ConnectionState Status => ConnectionState.Disconnected;

    public void Connect(ConnectionConfig? config)
    {
        this.ConnectCalled = true;
        this.failed.OnNext(new BleException("unreachable"));
    }

    public void CancelConnection() { }
    public IObservable<ConnectionState> WhenStatusChanged() => Observable.Never<ConnectionState>();
    public IObservable<BleException> WhenConnectionFailed() => this.failed;
    public IObservable<Unit> WhenServicesChanged() => this.inner.WhenServicesChanged();
    public IObservable<int> ReadRssi() => this.inner.ReadRssi();
    public IObservable<BleServiceInfo> GetService(string serviceUuid) => this.inner.GetService(serviceUuid);
    public IObservable<IReadOnlyList<BleServiceInfo>> GetServices() => this.inner.GetServices();
    public IObservable<BleCharacteristicInfo> GetCharacteristic(string serviceUuid, string characteristicUuid) => this.inner.GetCharacteristic(serviceUuid, characteristicUuid);
    public IObservable<IReadOnlyList<BleCharacteristicInfo>> GetCharacteristics(string serviceUuid) => this.inner.GetCharacteristics(serviceUuid);
    public IObservable<BleCharacteristicResult> NotifyCharacteristic(string serviceUuid, string characteristicUuid, bool useIndicationsIfAvailable = true) => this.inner.NotifyCharacteristic(serviceUuid, characteristicUuid, useIndicationsIfAvailable);
    public IObservable<BleCharacteristicInfo> WhenCharacteristicSubscriptionChanged(string serviceUuid, string characteristicUuid) => this.inner.WhenCharacteristicSubscriptionChanged(serviceUuid, characteristicUuid);
    public IObservable<BleCharacteristicResult> ReadCharacteristic(string serviceUuid, string characteristicUuid) => this.inner.ReadCharacteristic(serviceUuid, characteristicUuid);
    public IObservable<BleCharacteristicResult> WriteCharacteristic(string serviceUuid, string characteristicUuid, byte[] data, bool withResponse = true) => this.inner.WriteCharacteristic(serviceUuid, characteristicUuid, data, withResponse);
    public IObservable<BleDescriptorInfo> GetDescriptor(string serviceUuid, string characteristicUuid, string descriptorUuid) => this.inner.GetDescriptor(serviceUuid, characteristicUuid, descriptorUuid);
    public IObservable<IReadOnlyList<BleDescriptorInfo>> GetDescriptors(string serviceUuid, string characteristicUuid) => this.inner.GetDescriptors(serviceUuid, characteristicUuid);
    public IObservable<BleDescriptorResult> ReadDescriptor(string serviceUuid, string characteristicUuid, string descriptorUuid) => this.inner.ReadDescriptor(serviceUuid, characteristicUuid, descriptorUuid);
    public IObservable<BleDescriptorResult> WriteDescriptor(string serviceUuid, string characteristicUuid, string descriptorUuid, byte[] data) => this.inner.WriteDescriptor(serviceUuid, characteristicUuid, descriptorUuid, data);
}
