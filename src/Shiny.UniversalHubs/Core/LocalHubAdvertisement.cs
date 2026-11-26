namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// What this app's own hub host is advertising right now, so its own hub clients can leave it out of discovery - a scan
/// on the device that is advertising can report that advertisement. One per container, shared by the host and clients.
/// The advertisement has no room for an id beside the 128-bit service UUID, so the match is the service UUID plus the
/// advertised local name; an advertisement with no name is never treated as this app's.
/// </summary>
internal sealed class LocalHubAdvertisement
{
    readonly object gate = new();
    string? serviceUuid;
    string? localName;


    /// <summary>
    /// The host started (or restarted) advertising
    /// </summary>
    public void Set(string serviceUuid, string? localName)
    {
        lock (this.gate)
        {
            this.serviceUuid = serviceUuid;
            this.localName = localName;
        }
    }


    /// <summary>
    /// The host stopped advertising
    /// </summary>
    public void Clear()
    {
        lock (this.gate)
        {
            this.serviceUuid = null;
            this.localName = null;
        }
    }


    /// <summary>
    /// True when a scan result for <paramref name="serviceUuid"/> advertising <paramref name="advertisedName"/> is this
    /// app's own advertisement
    /// </summary>
    public bool IsLocal(string serviceUuid, string? advertisedName)
    {
        if (String.IsNullOrEmpty(advertisedName))
            return false;

        lock (this.gate)
        {
            return this.serviceUuid != null
                   && String.Equals(this.serviceUuid, serviceUuid, StringComparison.OrdinalIgnoreCase)
                   && String.Equals(this.localName, advertisedName, StringComparison.Ordinal);
        }
    }
}
