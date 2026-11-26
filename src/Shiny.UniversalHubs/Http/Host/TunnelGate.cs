namespace Shiny.UniversalHubs;

/// <summary>
/// Decides which UniversalHubs routes a tunnelled (public internet) request may reach
/// </summary>
internal static class TunnelGate
{
    /// <summary>
    /// A hub's routes are open to a tunnel only when that hub allows it. The file and media routes are open when any hub
    /// does - the token still has to belong to a client of a hub (and, for media, of a hub that allows tunnels). Paths
    /// outside UniversalHubs are left to the app - except, with dynamic hubs on, any other path under the base path, which
    /// would reach the dynamic hubs' Switchboard.
    /// </summary>
    public static bool IsAllowed(string path, string basePath, IEnumerable<IHubBinding> hubs, bool dynamicHubs = false)
    {
        var list = hubs.Where(x => x.GetSettings<HttpHubSettings>() != null).ToList();
        foreach (var hub in list)
        {
            if (IsUnder(path, UniversalHubProtocol.HubPath(basePath, hub.Key)))
                return AllowsTunnel(hub);
        }

        if (IsUnder(path, UniversalHubProtocol.FilesPath(basePath)) || IsUnder(path, UniversalHubProtocol.MediaPath(basePath)))
            return list.Any(AllowsTunnel);

        return !dynamicHubs || !IsUnder(path, UniversalHubProtocol.NormalizeBasePath(basePath));
    }


    internal static bool AllowsTunnel(IHubBinding hub) => hub.GetSettings<HttpHubSettings>()?.AllowTunnel == true;


    static bool IsUnder(string path, string root)
        => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
           && (path.Length == root.Length || path[root.Length] == '/');
}
