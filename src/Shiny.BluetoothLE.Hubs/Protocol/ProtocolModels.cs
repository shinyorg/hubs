using System.Text.Json.Serialization;

namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// Sent by the client once it is subscribed to the outbox
/// </summary>
public sealed record HandshakeInfo(
    int ProtocolVersion,
    string? Name,
    string? AppVersion,
    Dictionary<string, string>? Properties
);

/// <summary>
/// The host's answer to a handshake
/// </summary>
/// <param name="Accepted">Whether the client may use the hub</param>
/// <param name="Reason">Why the client was refused</param>
/// <param name="HostName">The host's advertised local name</param>
/// <param name="FileTransferPsm">The L2CAP PSM serving file transfers, 0 when file transfers are not available</param>
/// <param name="FileTransferSecure">Whether the L2CAP channel must be opened secure (Android listens on secure and insecure channels separately)</param>
/// <param name="MediaChannelPsm">The L2CAP PSM serving media channels, 0 when media channels are not available. Absent from hosts older than media channels.</param>
/// <param name="MediaChannelSecure">Whether the media L2CAP channel must be opened secure</param>
/// <param name="MediaToken">This connection's media token - the client's first frame on the media channel (its UTF-8 bytes)</param>
/// <param name="MediaMaxPacketSize">The largest media packet either side may send</param>
public sealed record HandshakeAck(
    bool Accepted,
    string? Reason,
    string? HostName,
    ushort FileTransferPsm,
    bool FileTransferSecure,
    ushort MediaChannelPsm = 0,
    bool MediaChannelSecure = false,
    string? MediaToken = null,
    int MediaMaxPacketSize = 0
);

/// <summary>
/// A handler failure relayed to the caller
/// </summary>
public sealed record RemoteError(
    string ErrorType,
    string Message
);

/// <summary>
/// One side is ending the session. Host to client: the host disconnected the client. Client to host: the client is
/// leaving. <paramref name="Kind"/> is absent from peers older than it - read it through <see cref="ToDisconnect"/>.
/// </summary>
public sealed record DisconnectInfo(string? Reason, HubDisconnectReason? Kind = null)
{
    public static DisconnectInfo From(HubDisconnect disconnect) => new(disconnect.Message, disconnect.Reason);

    /// <summary>
    /// The disconnect this describes - <paramref name="fallback"/> when the sender didn't say why
    /// </summary>
    public HubDisconnect ToDisconnect(HubDisconnectReason fallback) => new(this.Kind ?? fallback, this.Reason);
}


/// <summary>
/// A name changed after the handshake. Client to host (<see cref="FrameKind.Rename"/>): the client's new name - the host
/// answers with a Completion, or an Error when it refuses. Host to client (<see cref="FrameKind.HostRenamed"/>): the
/// host's new name. Peers older than these frames refuse a Rename and ignore a HostRenamed.
/// </summary>
public sealed record RenameInfo(string? Name);


[JsonSerializable(typeof(HandshakeInfo))]
[JsonSerializable(typeof(HandshakeAck))]
[JsonSerializable(typeof(RemoteError))]
[JsonSerializable(typeof(DisconnectInfo))]
[JsonSerializable(typeof(RenameInfo))]
internal partial class BleHubProtocolJsonContext : JsonSerializerContext;
