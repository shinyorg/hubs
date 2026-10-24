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
    /// The connection id - the platform's identifier for the central
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
    public int Mtu { get; }

    /// <summary>
    /// The BLE central this client is connected through - null when it is connected over another transport
    /// </summary>
    public Shiny.BluetoothLE.Hosting.IPeripheral? Peripheral { get; internal init; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Your own per connection state
    /// </summary>
    public ConcurrentDictionary<string, object> Items { get; } = new();

    public override string ToString() => $"{this.Name ?? "(unnamed)"} [{this.Id}]";
}
