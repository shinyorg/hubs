using System.Collections.Concurrent;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// A client that has completed the handshake with a hub
/// </summary>
public sealed class BleHubConnectedClient
{
    internal BleHubConnectedClient(string id, string? name, string? appVersion, IReadOnlyDictionary<string, string> properties, int mtu)
    {
        this.Id = id;
        this.Name = name;
        this.AppVersion = appVersion;
        this.Properties = properties;
        this.Mtu = mtu;
    }

    /// <summary>
    /// The connection id - over BLE, the platform's identifier for the central the client first connected through. It stays
    /// the same when the client resumes after a dropped link, on whichever central it comes back on.
    /// </summary>
    public string Id { get; }
    /// <summary>
    /// The name the client gave in its handshake, or its latest rename
    /// </summary>
    public string? Name { get; internal set; }
    public string? AppVersion { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }
    /// <summary>
    /// The ATT MTU of the client's BLE link when it connected (frames are this minus 3 bytes), 0 for a client on another
    /// transport
    /// </summary>
    public int Mtu { get; internal set; }

    /// <summary>
    /// The BLE central this client is connected through - null when it is connected over another transport. A client that
    /// resumed after a dropped link is on the central it came back on.
    /// </summary>
    public Shiny.BluetoothLE.Hosting.IPeripheral? Peripheral { get; internal set; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// How the hub reaches the client right now - set by the runtime, changes when the client resumes on a new link
    /// </summary>
    internal IBleHubPeerChannel? Channel { get; set; }

    /// <summary>
    /// Your own per connection state
    /// </summary>
    public ConcurrentDictionary<string, object> Items { get; } = new();

    public override string ToString() => $"{this.Name ?? "(unnamed)"} [{this.Id}]";
}
