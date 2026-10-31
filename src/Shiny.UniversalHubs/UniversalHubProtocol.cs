using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Shiny.UniversalHubs;

/// <summary>
/// Which links a hub is served over or reached through
/// </summary>
[Flags]
public enum HubTransports
{
    None = 0,
    Ble = 1,
    Http = 2,

    /// <summary>
    /// Apple's MultipeerConnectivity (Wi-Fi, peer-to-peer Wi-Fi and Bluetooth, with no network needed) - Apple devices
    /// only, and only once the Shiny.UniversalHubs.Multipeer package is set up. Without it, asking for it does nothing.
    /// </summary>
    Multipeer = 4,

    All = Ble | Http | Multipeer
}


/// <summary>
/// The Wi-Fi side of the wire: one Switchboard per hub at <c>{BasePath}/{characteristicUuid}</c>, called through a few
/// reserved methods, plus plain HTTP routes for files. Method and event names travel inside these calls, exactly as they do
/// over BLE.
/// </summary>
public static partial class UniversalHubProtocol
{
    /// <summary>
    /// Bumped on a breaking change to anything in this class. The host refuses a handshake from another version.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// The mDNS service type hosts advertise and clients browse for
    /// </summary>
    public const string ServiceType = "_shinyhub._tcp";

    public const string DefaultBasePath = "/hubs";

    /// <summary>
    /// <c>hub.handshake(handshake)</c> → <c>reply</c>. Must be the first call on a line.
    /// </summary>
    public const string HandshakeMethod = "hub.handshake";

    /// <summary>
    /// <c>hub.invoke(method, [args...])</c> → result envelope
    /// </summary>
    public const string InvokeMethod = "hub.invoke";

    /// <summary>
    /// <c>hub.stream(method, [args...])</c> → a stream of item envelopes
    /// </summary>
    public const string StreamMethod = "hub.stream";

    /// <summary>
    /// Host → client: <c>hub.push(event, [args...])</c>
    /// </summary>
    public const string PushMethod = "hub.push";

    /// <summary>
    /// Sent with every file request - the token comes from the handshake reply
    /// </summary>
    public const string FileTokenHeader = "X-Hub-File-Token";

    /// <summary>
    /// Sent with the media channel's WebSocket upgrade - the token comes from the handshake reply
    /// </summary>
    public const string MediaTokenHeader = "X-Hub-Media-Token";

    // mDNS TXT keys
    public const string TxtProtocol = "proto";
    public const string TxtHostId = "hostId";
    public const string TxtName = "name";
    public const string TxtPath = "path";
    public const string TxtHubs = "hubs";

    /// <summary>
    /// Where a hub's Switchboard is mapped
    /// </summary>
    public static string HubPath(string basePath, string characteristicUuid) => $"{TrimBase(basePath)}/{characteristicUuid.ToLowerInvariant()}";

    /// <summary>
    /// Where files are served: <c>PUT</c> / <c>GET {FilesPath}/{name}</c>
    /// </summary>
    public static string FilesPath(string basePath) => $"{TrimBase(basePath)}/files";

    /// <summary>
    /// Where media channels are opened: a WebSocket upgrade on <c>GET {MediaPath}</c>
    /// </summary>
    public static string MediaPath(string basePath) => $"{TrimBase(basePath)}/media";

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
/// The client's first call on a line
/// </summary>
public sealed record HubHandshake(
    int Protocol,
    string? Name,
    string? AppVersion,
    Dictionary<string, string>? Properties
);


/// <summary>
/// The host's answer to <see cref="HubHandshake"/>
/// </summary>
/// <param name="Accepted">Whether the client may use the hub</param>
/// <param name="Reason">Why it was refused</param>
/// <param name="HostName">The host's name - the same local name it advertises over BLE</param>
/// <param name="HostId">Identifies the host across transports</param>
/// <param name="FileToken">Send as <see cref="UniversalHubProtocol.FileTokenHeader"/>; null when the host serves no files</param>
/// <param name="MediaToken">Send as <see cref="UniversalHubProtocol.MediaTokenHeader"/>; null when the host has no media channels</param>
/// <param name="MediaMaxPacketSize">The largest packet the host's media channels carry</param>
public sealed record HubHandshakeReply(
    bool Accepted,
    string? Reason,
    string? HostName,
    string? HostId,
    string? FileToken,
    string? MediaToken = null,
    int? MediaMaxPacketSize = null
);


/// <summary>
/// The message a host hangs up a line with: why, and the host's own text. It travels as the Switchboard hang-up message,
/// so it arrives with the close itself.
/// </summary>
/// <param name="Reason">A <see cref="Shiny.BluetoothLE.Hubs.HubDisconnectReason"/></param>
/// <param name="Message">The text passed to Context.Abort, Disconnect or Stop</param>
public sealed record HubHangUp(int Reason, string? Message);


[JsonSerializable(typeof(HubHangUp))]
[JsonSerializable(typeof(HubHandshake))]
[JsonSerializable(typeof(HubHandshakeReply))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal partial class UniversalHubJsonContext : JsonSerializerContext;
