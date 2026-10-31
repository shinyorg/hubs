namespace Shiny.UniversalHubs;

/// <summary>
/// This device's Nearby Connections client, as thin as the platform allows: Google Play services on Android, the
/// NearbyConnections bridge on iOS and macOS, and an in-memory one in tests. Everything above it - channels, framing,
/// file names, hub sessions - is <see cref="NearbyLink"/>, which runs (and is tested) on plain .NET.
/// </summary>
internal interface INearbyRadio
{
    /// <summary>
    /// False where there is no Nearby Connections (Windows, Linux, Mac Catalyst, Android without Google Play services)
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Called once, before anything else, with where every callback goes. Callbacks may come on any thread; the link
    /// puts them in order.
    /// </summary>
    void Start(INearbyRadioListener listener);

    Task StartAdvertising(string serviceId, byte[] endpointInfo, NearbyStrategy strategy);
    void StopAdvertising();

    Task StartDiscovery(string serviceId, NearbyStrategy strategy);
    void StopDiscovery();

    /// <summary>
    /// Asks <paramref name="endpointId"/> for a connection. Completes once asked; <see cref="INearbyRadioListener.ConnectionInitiated"/>
    /// and <see cref="INearbyRadioListener.ConnectionResult"/> follow.
    /// </summary>
    Task RequestConnection(string endpointId, byte[] info);

    Task AcceptConnection(string endpointId);
    void RejectConnection(string endpointId);

    /// <summary>
    /// One BYTES payload, at most <see cref="NearbyProtocol.MaxBytesPayloadSize"/>. Throws when the endpoint is gone.
    /// </summary>
    void SendBytes(string endpointId, byte[] data);

    /// <summary>
    /// Starts sending a file payload and returns its id. How it goes comes through <see cref="INearbyRadioListener.FileTransfer"/>.
    /// </summary>
    long SendFile(string endpointId, string path);

    void CancelFile(long payloadId);
    void Disconnect(string endpointId);
}


/// <summary>
/// What the radio reports
/// </summary>
internal interface INearbyRadioListener
{
    void EndpointFound(string endpointId, byte[] info);
    void EndpointLost(string endpointId);

    /// <summary>
    /// Either end asked for a connection - it goes ahead once both accept
    /// </summary>
    void ConnectionInitiated(string endpointId, byte[] info, bool incoming);

    void ConnectionResult(string endpointId, bool connected, string? error);
    void Disconnected(string endpointId);
    void BytesReceived(string endpointId, byte[] data);

    /// <summary>
    /// A file payload, either way. <paramref name="receivedPath"/> is set once an incoming file is complete: a file the
    /// listener now owns.
    /// </summary>
    void FileTransfer(string endpointId, long payloadId, long transferred, long total, NearbyTransferStatus status, string? receivedPath);
}


internal enum NearbyTransferStatus
{
    InProgress,
    Success,
    Failure,
    Canceled
}


/// <summary>
/// Nearby's connection topology. Host and clients must use the same one.
/// </summary>
public enum NearbyStrategy
{
    /// <summary>
    /// Any device connects to any number of others (P2P_CLUSTER) - a device can host and visit at the same time.
    /// Bluetooth, BLE and Wi-Fi LAN.
    /// </summary>
    Cluster,

    /// <summary>
    /// One host, many clients, and a client connects to one host at a time (P2P_STAR). Higher bandwidth upgrades
    /// (Wi-Fi hotspot, Wi-Fi Direct) in exchange.
    /// </summary>
    Star
}


/// <summary>
/// Where there is no Nearby Connections
/// </summary>
internal sealed class UnsupportedNearbyRadio : INearbyRadio
{
    const string Message = "Nearby Connections is only available on Android (with Google Play services), iOS and macOS";

    public bool IsSupported => false;
    public void Start(INearbyRadioListener listener) { }
    public Task StartAdvertising(string serviceId, byte[] endpointInfo, NearbyStrategy strategy) => throw new PlatformNotSupportedException(Message);
    public void StopAdvertising() { }
    public Task StartDiscovery(string serviceId, NearbyStrategy strategy) => throw new PlatformNotSupportedException(Message);
    public void StopDiscovery() { }
    public Task RequestConnection(string endpointId, byte[] info) => throw new PlatformNotSupportedException(Message);
    public Task AcceptConnection(string endpointId) => throw new PlatformNotSupportedException(Message);
    public void RejectConnection(string endpointId) { }
    public void SendBytes(string endpointId, byte[] data) => throw new PlatformNotSupportedException(Message);
    public long SendFile(string endpointId, string path) => throw new PlatformNotSupportedException(Message);
    public void CancelFile(long payloadId) { }
    public void Disconnect(string endpointId) { }
}
