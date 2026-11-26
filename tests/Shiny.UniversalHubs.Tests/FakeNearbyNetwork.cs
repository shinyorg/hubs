using System.Threading.Channels;
using Shiny.UniversalHubs;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// Nearby Connections in memory: devices advertising, discovering and connecting within one process, with Nearby's own
/// rules - one connection between two devices, both ends accept, payloads of at most 32 KB, endpoint info of at most 131
/// bytes. Each device hears its callbacks on its own queue, one at a time and in order.
/// </summary>
internal sealed class FakeNearbyNetwork
{
    readonly object gate = new();
    readonly List<FakeNearbyRadio> radios = new();
    readonly List<(FakeNearbyRadio A, FakeNearbyRadio B)> connections = new();
    readonly Dictionary<(FakeNearbyRadio, FakeNearbyRadio), (bool A, bool B)> pending = new();
    int nextId;
    long nextPayload;


    public FakeNearbyRadio CreateDevice()
    {
        lock (this.gate)
        {
            var radio = new FakeNearbyRadio(this, $"EP{++this.nextId:00}");
            this.radios.Add(radio);
            return radio;
        }
    }


    public IReadOnlyList<(FakeNearbyRadio A, FakeNearbyRadio B)> Connections
    {
        get
        {
            lock (this.gate)
                return this.connections.ToList();
        }
    }

    /// <summary>
    /// Every BYTES payload sent, for checking the framing
    /// </summary>
    public List<int> PayloadSizes { get; } = new();


    internal void Advertised(FakeNearbyRadio radio)
    {
        List<FakeNearbyRadio> watching;
        lock (this.gate)
            watching = this.radios.Where(x => x != radio && x.DiscoveringService == radio.AdvertisingService).ToList();
        foreach (var other in watching)
            other.Post(l => l.EndpointFound(radio.EndpointId, radio.AdvertisedInfo!));
    }


    internal void StoppedAdvertising(FakeNearbyRadio radio, string serviceId)
    {
        List<FakeNearbyRadio> watching;
        lock (this.gate)
            watching = this.radios.Where(x => x != radio && x.DiscoveringService == serviceId).ToList();
        foreach (var other in watching)
            other.Post(l => l.EndpointLost(radio.EndpointId));
    }


    internal void Discovering(FakeNearbyRadio radio)
    {
        List<FakeNearbyRadio> advertising;
        lock (this.gate)
            advertising = this.radios.Where(x => x != radio && x.AdvertisingService == radio.DiscoveringService).ToList();
        foreach (var other in advertising)
            radio.Post(l => l.EndpointFound(other.EndpointId, other.AdvertisedInfo!));
    }


    /// <summary>
    /// Connection requests fail while set - the host is out of reach
    /// </summary>
    public bool Partitioned { get; set; }


    internal void Request(FakeNearbyRadio from, string endpointId, byte[] info)
    {
        if (this.Partitioned)
            throw new InvalidOperationException("STATUS_ENDPOINT_UNKNOWN");

        FakeNearbyRadio to;
        lock (this.gate)
        {
            to = this.radios.FirstOrDefault(x => x.EndpointId == endpointId) ?? throw new InvalidOperationException($"No endpoint {endpointId}");
            if (this.Find(from, to) != null)
                throw new InvalidOperationException("STATUS_ALREADY_CONNECTED_TO_ENDPOINT");
            if (info.Length > NearbyProtocol.MaxEndpointInfoLength)
                throw new InvalidOperationException("Endpoint info is too long");
            this.pending[Key(from, to)] = (false, false);
        }
        from.Post(l => l.ConnectionInitiated(to.EndpointId, to.AdvertisedInfo ?? [], false));
        to.Post(l => l.ConnectionInitiated(from.EndpointId, info, true));
    }


    internal void Answer(FakeNearbyRadio radio, string endpointId, bool accept)
    {
        FakeNearbyRadio other;
        bool done;
        lock (this.gate)
        {
            other = this.radios.First(x => x.EndpointId == endpointId);
            var key = Key(radio, other);
            if (!this.pending.TryGetValue(key, out var answers))
                return;

            if (!accept)
            {
                this.pending.Remove(key);
                done = false;
            }
            else
            {
                answers = key.Item1 == radio ? (true, answers.B) : (answers.A, true);
                done = answers.A && answers.B;
                if (done)
                {
                    this.pending.Remove(key);
                    this.connections.Add((radio, other));
                }
                else
                {
                    this.pending[key] = answers;
                    return;
                }
            }
        }
        radio.Post(l => l.ConnectionResult(other.EndpointId, done, done ? null : "rejected"));
        other.Post(l => l.ConnectionResult(radio.EndpointId, done, done ? null : "rejected"));
    }


    internal void SendBytes(FakeNearbyRadio from, string endpointId, byte[] data)
    {
        if (data.Length > NearbyProtocol.MaxBytesPayloadSize)
            throw new InvalidOperationException($"A {data.Length} byte payload is over Nearby's limit");

        FakeNearbyRadio to;
        lock (this.gate)
        {
            to = this.radios.First(x => x.EndpointId == endpointId);
            if (this.Find(from, to) == null)
                throw new InvalidOperationException("Not connected");
            this.PayloadSizes.Add(data.Length);
        }
        var copy = data.ToArray();
        to.Post(l => l.BytesReceived(from.EndpointId, copy));
    }


    internal long SendFile(FakeNearbyRadio from, string endpointId, string path)
    {
        FakeNearbyRadio to;
        long id;
        lock (this.gate)
        {
            to = this.radios.First(x => x.EndpointId == endpointId);
            if (this.Find(from, to) == null)
                throw new InvalidOperationException("Not connected");
            id = ++this.nextPayload;
        }

        var length = new FileInfo(path).Length;
        var arrived = Path.Combine(Path.GetTempPath(), "fake-nearby-" + Guid.NewGuid().ToString("N"));
        File.Copy(path, arrived);
        to.Post(l => l.FileTransfer(from.EndpointId, id, length / 2, length, NearbyTransferStatus.InProgress, null));
        to.Post(l => l.FileTransfer(from.EndpointId, id, length, length, NearbyTransferStatus.Success, arrived));
        from.Post(l => l.FileTransfer(to.EndpointId, id, length, length, NearbyTransferStatus.Success, null));
        return id;
    }


    internal void Disconnect(FakeNearbyRadio radio, string endpointId)
    {
        FakeNearbyRadio? other;
        lock (this.gate)
        {
            other = this.radios.FirstOrDefault(x => x.EndpointId == endpointId);
            var connection = other == null ? null : this.Find(radio, other);
            if (connection == null)
                return;
            this.connections.Remove(connection.Value);
        }
        radio.Post(l => l.Disconnected(other!.EndpointId));
        other!.Post(l => l.Disconnected(radio.EndpointId));
    }


    // under the gate
    (FakeNearbyRadio A, FakeNearbyRadio B)? Find(FakeNearbyRadio a, FakeNearbyRadio b)
    {
        foreach (var c in this.connections)
        {
            if ((c.A == a && c.B == b) || (c.A == b && c.B == a))
                return c;
        }
        return null;
    }


    static (FakeNearbyRadio, FakeNearbyRadio) Key(FakeNearbyRadio a, FakeNearbyRadio b)
        => String.CompareOrdinal(a.EndpointId, b.EndpointId) < 0 ? (a, b) : (b, a);
}


internal sealed class FakeNearbyRadio : INearbyRadio
{
    readonly FakeNearbyNetwork network;
    readonly Channel<Action<INearbyRadioListener>> inbox = Channel.CreateUnbounded<Action<INearbyRadioListener>>(new UnboundedChannelOptions { SingleReader = true });
    INearbyRadioListener? listener;


    public FakeNearbyRadio(FakeNearbyNetwork network, string endpointId)
    {
        this.network = network;
        this.EndpointId = endpointId;
    }


    public string EndpointId { get; }
    public string? AdvertisingService { get; private set; }
    public byte[]? AdvertisedInfo { get; private set; }
    public string? DiscoveringService { get; private set; }
    public int AdvertiseCount { get; private set; }

    public bool IsSupported => true;


    public void Start(INearbyRadioListener listener)
    {
        // one Nearby client per app - one container, one link
        if (this.listener != null)
            throw new InvalidOperationException("A device's radio has one link");

        this.listener = listener;
        _ = Task.Run(async () =>
        {
            await foreach (var action in this.inbox.Reader.ReadAllAsync())
                action(listener);
        });
    }


    public Task StartAdvertising(string serviceId, byte[] endpointInfo, NearbyStrategy strategy)
    {
        if (endpointInfo.Length > NearbyProtocol.MaxEndpointInfoLength)
            throw new InvalidOperationException("Endpoint info is too long");

        this.AdvertisingService = serviceId;
        this.AdvertisedInfo = endpointInfo;
        this.AdvertiseCount++;
        this.network.Advertised(this);
        return Task.CompletedTask;
    }


    public void StopAdvertising()
    {
        if (this.AdvertisingService is not { } serviceId)
            return;

        this.AdvertisingService = null;
        this.AdvertisedInfo = null;
        this.network.StoppedAdvertising(this, serviceId);
    }


    public Task StartDiscovery(string serviceId, NearbyStrategy strategy)
    {
        this.DiscoveringService = serviceId;
        this.network.Discovering(this);
        return Task.CompletedTask;
    }


    public void StopDiscovery() => this.DiscoveringService = null;

    public Task RequestConnection(string endpointId, byte[] info)
    {
        this.network.Request(this, endpointId, info);
        return Task.CompletedTask;
    }

    public Task AcceptConnection(string endpointId)
    {
        this.network.Answer(this, endpointId, true);
        return Task.CompletedTask;
    }

    public void RejectConnection(string endpointId) => this.network.Answer(this, endpointId, false);
    public void SendBytes(string endpointId, byte[] data) => this.network.SendBytes(this, endpointId, data);
    public long SendFile(string endpointId, string path) => this.network.SendFile(this, endpointId, path);
    public void CancelFile(long payloadId) { }
    public void Disconnect(string endpointId) => this.network.Disconnect(this, endpointId);

    /// <summary>
    /// The radio itself drops every connection - Bluetooth off, out of range
    /// </summary>
    public void DropAll()
    {
        foreach (var c in this.network.Connections.Where(x => x.A == this || x.B == this))
            this.network.Disconnect(this, c.A == this ? c.B.EndpointId : c.A.EndpointId);
    }

    internal void Post(Action<INearbyRadioListener> action) => this.inbox.Writer.TryWrite(action);
}
