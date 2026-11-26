namespace Shiny.UniversalHubs;

/// <summary>
/// What this app's own hub host is advertising right now, so its own hub clients can leave it out of discovery - a scan
/// on the device that is advertising can report that advertisement. One per container, shared by the host and clients.
/// The advertisement has no room for an id beside the service UUIDs, so the match is a service UUID plus the advertised
/// local name; an advertisement with no name is never treated as this app's.
/// </summary>
internal sealed class LocalHubAdvertisement
{
    readonly object gate = new();
    IReadOnlyList<string> serviceUuids = [];
    string? localName;


    /// <summary>
    /// The host started (or restarted) advertising
    /// </summary>
    public void Set(IReadOnlyList<string> serviceUuids, string? localName)
    {
        lock (this.gate)
        {
            this.serviceUuids = serviceUuids;
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
            this.serviceUuids = [];
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
            return this.serviceUuids.Contains(serviceUuid, StringComparer.OrdinalIgnoreCase)
                   && String.Equals(this.localName, advertisedName, StringComparison.Ordinal);
        }
    }
}
