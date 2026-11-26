using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Shiny.UniversalHubs;

/// <summary>
/// The HTTP side of the wire: a WebSocket per hub connection at <c>{BasePath}/{hub name}</c> carrying the peer-link hub
/// session (<see cref="PeerProtocol"/>), and the mDNS advertisement clients find hosts by
/// </summary>
public static partial class UniversalHubProtocol
{
    /// <summary>
    /// Bumped on a breaking change to the HTTP routes or the mDNS TXT keys. Clients skip hosts advertising another version.
    /// </summary>
    public const int Version = 3;

    /// <summary>
    /// The mDNS service type hosts advertise and clients browse for
    /// </summary>
    public const string ServiceType = "_shinyhub._tcp";

    public const string DefaultBasePath = "/hubs";

    public const string TxtProtocol = "proto";
    public const string TxtHostId = "hostId";
    public const string TxtName = "name";
    public const string TxtPath = "path";
    public const string TxtHubs = "hubs";

    /// <summary>
    /// Where a hub's WebSocket is
    /// </summary>
    public static string HubPath(string basePath, string hubName) => $"{TrimBase(basePath)}/{hubName}";

    /// <summary>
    /// Throws unless <paramref name="serviceType"/> is an mDNS service type like <c>_myapp._tcp</c>
    /// </summary>
    public static void ValidateServiceType(string serviceType)
    {
        if (String.IsNullOrWhiteSpace(serviceType) || !ServiceTypeRegex().IsMatch(serviceType))
            throw new ArgumentException($"'{serviceType}' is not an mDNS service type - expected something like _myapp._tcp", nameof(serviceType));
    }

    [GeneratedRegex(@"^_[A-Za-z0-9-]{1,15}\._tcp$")]
    private static partial Regex ServiceTypeRegex();

    /// <summary>
    /// A base path as it is served: a leading slash, no trailing one
    /// </summary>
    public static string NormalizeBasePath(string basePath) => TrimBase(basePath);

    static string TrimBase(string basePath)
    {
        var trimmed = basePath.Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}


/// <summary>
/// The client's first message on a peer link (HTTP, Multipeer, Nearby)
/// </summary>
/// <param name="Resumable">The client can resume after a dropped session, so the host may hold it</param>
public sealed record HubHandshake(
    int Protocol,
    string? Name,
    string? AppVersion,
    Dictionary<string, string>? Properties,
    bool Resumable = false,
    string? Serializer = null
);


/// <summary>
/// Why a peer-link session ends: the host's reason and its own text, or the client saying it is leaving
/// </summary>
/// <param name="Reason">A <see cref="Shiny.UniversalHubs.HubDisconnectReason"/></param>
/// <param name="Message">The text passed to Context.Abort, Disconnect or Stop</param>
public sealed record HubHangUp(int Reason, string? Message);


