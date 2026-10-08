using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.SwitchboardR;

/// <summary>
/// Which links a hub is served over or reached through
/// </summary>
[Flags]
public enum HubTransports
{
    None = 0,
    Ble = 1,
    Wifi = 2,
    All = Ble | Wifi
}


/// <summary>
/// The Wi-Fi side of the wire: one Switchboard per hub at <c>{BasePath}/{characteristicUuid}</c>, called through a few
/// reserved methods, plus plain HTTP routes for files. Method and event names travel inside these calls, exactly as they do
/// over BLE.
/// </summary>
public static class SwitchboardRProtocol
{
    /// <summary>
    /// Bumped on a breaking change to anything in this class. The host refuses a handshake from another version.
    /// </summary>
    public const int Version = 1;

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
/// <param name="FileToken">Send as <see cref="SwitchboardRProtocol.FileTokenHeader"/>; null when the host serves no files</param>
public sealed record HubHandshakeReply(
    bool Accepted,
    string? Reason,
    string? HostName,
    string? HostId,
    string? FileToken
);


[JsonSerializable(typeof(HubHandshake))]
[JsonSerializable(typeof(HubHandshakeReply))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal partial class SwitchboardRJsonContext : JsonSerializerContext;
