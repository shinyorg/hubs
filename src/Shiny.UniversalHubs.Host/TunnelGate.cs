namespace Shiny.UniversalHubs;

/// <summary>
/// Decides which UniversalHubs routes a tunnelled (public internet) request may reach
/// </summary>
internal static class TunnelGate
{
    /// <summary>
    /// A hub's routes are open to a tunnel only when that hub allows it. The file routes are open when any hub does - the
    /// file token still has to belong to a client of a hub. Paths outside UniversalHubs are left to the app.
    /// </summary>
    public static bool IsAllowed(string path, string basePath, IEnumerable<IHubBinding> hubs)
    {
        var list = hubs as IReadOnlyCollection<IHubBinding> ?? hubs.ToList();
        foreach (var hub in list)
        {
            if (IsUnder(path, UniversalHubProtocol.HubPath(basePath, hub.CharacteristicUuid)))
                return hub.AllowTunnel;
        }

        if (IsUnder(path, UniversalHubProtocol.FilesPath(basePath)))
            return list.Any(x => x.AllowTunnel);

        return true;
    }


    static bool IsUnder(string path, string root)
        => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && (path.Length == root.Length || path[root.Length] == '/');
}
