using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hosting;
using HostedPeripheral = Shiny.BluetoothLE.Hosting.IPeripheral;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// An in-memory GATT server so BleHubHost's orchestration (services, advertising, writes, notifications) can be tested
/// </summary>
public class FakeHostingManager : IBleHostingManager
{
    readonly Dictionary<string, FakeService> services = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Log { get; } = new();
    public string[] AdvertisedServices { get; private set; } = [];
    public string? AdvertisedName { get; private set; }

    public AccessState AdvertisingAccessStatus => AccessState.Available;
    public AccessState GattAccessStatus => AccessState.Available;
    public bool IsAdvertising { get; private set; }
    public IReadOnlyList<IGattService> Services => this.services.Values.ToList();

    public FakeCharacteristic Characteristic(string uuid) => this.services.Values
        .SelectMany(x => x.Characteristics)
        .OfType<FakeCharacteristic>()
        .Single(x => String.Equals(x.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

    public bool HasService(string uuid) => this.services.ContainsKey(uuid);

    public Task<AccessState> RequestAccess(bool advertise = true, bool connect = true) => Task.FromResult(AccessState.Available);

    public Task StartAdvertising(AdvertisementOptions? options = null)
    {
        this.IsAdvertising = true;
        this.AdvertisedServices = options?.ServiceUuids ?? [];
        this.AdvertisedName = options?.LocalName;
        this.Log.Add($"advertise:{String.Join(",", this.AdvertisedServices)}");
        return Task.CompletedTask;
    }

    public void StopAdvertising()
    {
        this.IsAdvertising = false;
        this.AdvertisedServices = [];
        this.Log.Add("stop-advertising");
    }

    readonly Dictionary<ushort, (bool Secure, Action<L2CapChannel> OnOpen)> l2capListeners = new();
    ushort nextPsm = 0x80;

    /// <summary>
    /// When set, OpenL2Cap fails like a device without L2CAP
    /// </summary>
    public bool FailL2Cap { get; set; }

    public IReadOnlyCollection<ushort> L2CapListeners => this.l2capListeners.Keys;

    public Task<L2CapInstance> OpenL2Cap(bool secure, Action<L2CapChannel> onOpen)
    {
        if (this.FailL2Cap)
            throw new NotSupportedException("No L2CAP here");

        var psm = this.nextPsm++;
        this.l2capListeners[psm] = (secure, onOpen);
        this.Log.Add($"open-l2cap:{psm}:{secure}");
        return Task.FromResult(new L2CapInstance(psm, () =>
        {
            this.l2capListeners.Remove(psm);
            this.Log.Add($"close-l2cap:{psm}");
        }));
    }

    /// <summary>
    /// A central opens an L2CAP channel to a listening PSM: the host's end goes to the listener, the central's end is
    /// returned
    /// </summary>
    public FakeL2CapLink ConnectL2Cap(ushort psm, string centralId, bool secure = false)
    {
        if (!this.l2capListeners.TryGetValue(psm, out var listener))
            throw new InvalidOperationException($"Nothing listens on PSM {psm}");
        if (listener.Secure != secure)
            throw new InvalidOperationException($"PSM {psm} listens {(listener.Secure ? "secure" : "insecure")}");

        var link = new FakeL2CapLink(psm, centralId, "host");
        listener.OnOpen(link.Host);
        return link;
    }

    public Task AdvertiseBeacon(Guid uuid, ushort major, ushort minor, sbyte? txpower = null) => throw new NotSupportedException();

    public Task<IGattService> AddService(string uuid, bool primary, Action<IGattServiceBuilder> serviceBuilder)
    {
        var service = new FakeService(uuid, primary);
        serviceBuilder(service);
        this.services[uuid] = service;
        this.Log.Add($"add-service:{uuid}:{service.Characteristics.Count}");
        return Task.FromResult<IGattService>(service);
    }

    public void RemoveService(string serviceUuid)
    {
        if (this.services.Remove(serviceUuid))
            this.Log.Add($"remove-service:{serviceUuid}");
    }

    public void ClearServices() => this.services.Clear();
}


public class FakeService(string uuid, bool primary) : IGattService, IGattServiceBuilder
{
    readonly List<IGattCharacteristic> characteristics = new();

    public string Uuid { get; } = uuid;
    public bool Primary { get; } = primary;
    public IReadOnlyList<IGattCharacteristic> Characteristics => this.characteristics;

    public IGattCharacteristic AddCharacteristic(string uuid, Action<IGattCharacteristicBuilder> characteristicBuilder)
    {
        var ch = new FakeCharacteristic(uuid);
        characteristicBuilder(ch);
        this.characteristics.Add(ch);
        return ch;
    }
}


public class FakeCharacteristic(string uuid) : IGattCharacteristic, IGattCharacteristicBuilder
{
    readonly List<HostedPeripheral> subscribed = new();
    Func<WriteRequest, Task>? onWrite;
    Func<CharacteristicSubscription, Task>? onSubscribe;

    public string Uuid { get; } = uuid;
    public CharacteristicProperties Properties => CharacteristicProperties.Write | CharacteristicProperties.Notify;
    public IReadOnlyList<HostedPeripheral> SubscribedCentrals => this.subscribed;

    /// <summary>
    /// Where notifications go - central id + frame
    /// </summary>
    public Action<string, byte[]>? Notified { get; set; }

    public IGattCharacteristicBuilder SetNotification(Func<CharacteristicSubscription, Task>? onSubscribe = null, NotificationOptions options = NotificationOptions.Notify)
    {
        this.onSubscribe = onSubscribe;
        return this;
    }

    public IGattCharacteristicBuilder SetWrite(Func<WriteRequest, Task> request, WriteOptions options = WriteOptions.Write)
    {
        this.onWrite = request;
        return this;
    }

    public IGattCharacteristicBuilder SetRead(Func<ReadRequest, Task<GattResult>> request, bool encrypted = false) => this;

    public Task Notify(byte[] data, params HostedPeripheral[] centrals) => this.Notify(data, CancellationToken.None, centrals);

    public Task Notify(byte[] data, CancellationToken cancellationToken, params HostedPeripheral[] centrals)
    {
        foreach (var central in centrals.Length == 0 ? this.subscribed.ToArray() : centrals)
            this.Notified?.Invoke(central.Uuid, data);
        return Task.CompletedTask;
    }

    public Task Subscribe(HostedPeripheral central)
    {
        this.subscribed.Add(central);
        return this.onSubscribe?.Invoke(new CharacteristicSubscription(this, central, true)) ?? Task.CompletedTask;
    }

    public Task Unsubscribe(HostedPeripheral central)
    {
        this.subscribed.RemoveAll(x => x.Uuid == central.Uuid);
        return this.onSubscribe?.Invoke(new CharacteristicSubscription(this, central, false)) ?? Task.CompletedTask;
    }

    /// <summary>
    /// The central vanished without the platform reporting an unsubscribe
    /// </summary>
    public void Forget(string uuid) => this.subscribed.RemoveAll(x => x.Uuid == uuid);

    public async Task Write(HostedPeripheral central, byte[] data)
    {
        var state = GattState.Success;
        await this.onWrite!(new WriteRequest(this, central, data, 0, true, s => state = s));
        if (state != GattState.Success)
            throw new InvalidOperationException($"GATT write failed: {state}");
    }
}


public class FakeCentral(string uuid, int mtu = 185) : HostedPeripheral
{
    public string Uuid { get; } = uuid;
    public int Mtu { get; } = mtu;
    public object? Context { get; set; }
}


/// <summary>
/// Both ends of an in-memory L2CAP channel. Like a real one it is a byte stream: each write reaches the other end in pieces
/// of at most <see cref="MaxDelivery"/> bytes. Disposing either end closes both.
/// </summary>
public sealed class FakeL2CapLink
{
    readonly System.Reactive.Subjects.Subject<byte[]> toHost = new();
    readonly System.Reactive.Subjects.Subject<byte[]> toCentral = new();
    readonly object gate = new();
    int closed;

    public FakeL2CapLink(ushort psm, string centralId, string hostId)
    {
        this.Host = new L2CapChannel(psm, centralId, data => this.Deliver(data, this.toCentral, () => this.HostWrites++), this.toHost, this.Close);
        this.Central = new L2CapChannel(psm, hostId, data => this.Deliver(data, this.toHost, () => this.CentralWrites++), this.toCentral, this.Close);
    }

    public L2CapChannel Host { get; }
    public L2CapChannel Central { get; }
    public int MaxDelivery { get; set; } = 251;
    public int HostWrites { get; private set; }
    public int CentralWrites { get; private set; }
    public bool IsClosed => Volatile.Read(ref this.closed) == 1;

    IObservable<System.Reactive.Unit> Deliver(byte[] data, System.Reactive.Subjects.Subject<byte[]> to, Action count)
    {
        lock (this.gate)
        {
            if (this.IsClosed)
                return System.Reactive.Linq.Observable.Throw<System.Reactive.Unit>(new IOException("L2CAP channel closed"));

            count();
            for (var offset = 0; offset < data.Length; offset += this.MaxDelivery)
                to.OnNext(data.AsSpan(offset, Math.Min(this.MaxDelivery, data.Length - offset)).ToArray());
        }
        return System.Reactive.Linq.Observable.Return(System.Reactive.Unit.Default);
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref this.closed, 1) == 1)
            return;

        lock (this.gate)
        {
            this.toHost.OnCompleted();
            this.toCentral.OnCompleted();
        }
    }
}
