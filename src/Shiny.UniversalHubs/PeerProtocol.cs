using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// The wire shared by the peer-to-peer transports (Shiny.UniversalHubs.Multipeer, Shiny.UniversalHubs.Nearby). One radio
/// session per hub connection, carrying reliable, ordered messages:
/// <code>
/// [0]     protocol version
/// [1]     kind
/// [2..5]  message id (little endian) - correlates replies, stream items and cancels with their call
/// [6]     name length
/// [7..]   name (UTF-8): the hub's characteristic UUID, method or event
/// [...]   payload: hub arguments and results exactly as IBleHubSerializer wrote them, or a control record as JSON
/// </code>
/// Unlike Wi-Fi, values are never re-framed as JSON, so any IBleHubSerializer works. Files travel as the radio's own
/// file transfers, named <see cref="FileResourcePrefix"/> + the request's message id.
/// </summary>
public static class PeerProtocol
{
    /// <summary>
    /// Bumped on a breaking change to anything here. The host refuses a handshake from another version, and each radio
    /// advertises it so clients skip hosts they can't talk to.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// File transfers are named with the file request's message id
    /// </summary>
    public const string FileResourcePrefix = "hubfile-";

    const int HeaderSize = 7;


    internal static byte[] Encode(PeerMessageKind kind, uint id, string? name, ReadOnlySpan<byte> payload)
    {
        var nameBytes = name == null ? [] : Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > byte.MaxValue)
            throw new ArgumentException($"'{name}' is longer than {byte.MaxValue} UTF-8 bytes", nameof(name));

        var message = new byte[HeaderSize + nameBytes.Length + payload.Length];
        message[0] = Version;
        message[1] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(2), id);
        message[6] = (byte)nameBytes.Length;
        nameBytes.CopyTo(message, HeaderSize);
        payload.CopyTo(message.AsSpan(HeaderSize + nameBytes.Length));
        return message;
    }


    internal static byte[] Encode<T>(PeerMessageKind kind, uint id, string? name, T value, JsonTypeInfo<T> type)
        => Encode(kind, id, name, JsonSerializer.SerializeToUtf8Bytes(value, type));


    internal static PeerMessage Decode(ReadOnlyMemory<byte> message)
    {
        var span = message.Span;
        if (span.Length < HeaderSize)
            throw new BleHubProtocolException($"Peer-link message is too short ({span.Length} bytes)");
        if (span[0] != Version)
            throw new BleHubProtocolException($"Unsupported peer-link protocol version {span[0]}");

        var nameLength = span[6];
        if (span.Length < HeaderSize + nameLength)
            throw new BleHubProtocolException("Peer-link message is shorter than its name");

        return new PeerMessage(
            (PeerMessageKind)span[1],
            BinaryPrimitives.ReadUInt32LittleEndian(span[2..]),
            nameLength == 0 ? null : Encoding.UTF8.GetString(span.Slice(HeaderSize, nameLength)),
            message[(HeaderSize + nameLength)..]
        );
    }


    internal static T Read<T>(this PeerMessage message, JsonTypeInfo<T> type)
        => JsonSerializer.Deserialize(message.Payload.Span, type) ?? throw new BleHubProtocolException($"{message.Kind} has no body");

    internal static string FileResourceName(uint id) => FileResourcePrefix + id;

    internal static uint? ReadFileResourceId(string resourceName)
        => resourceName.StartsWith(FileResourcePrefix, StringComparison.Ordinal) && UInt32.TryParse(resourceName.AsSpan(FileResourcePrefix.Length), out var id) ? id : null;
}


/// <summary>
/// What a peer-link message is. Values travel on the wire - never renumber.
/// </summary>
public enum PeerMessageKind : byte
{
    // client -> host
    Handshake = 0x01,           // name = characteristic UUID, payload = HubHandshake
    Invoke = 0x10,              // name = method, payload = arguments
    Stream = 0x11,              // name = method, payload = arguments
    Cancel = 0x12,              // id = the call to cancel
    Rename = 0x13,              // payload = PeerRename
    Resume = 0x14,              // name = characteristic UUID, payload = PeerResume - a dropped client back on a new session; answered with a HandshakeReply
    FileRequest = 0x50,         // payload = PeerFileRequest
    MediaOpen = 0x60,

    // host -> client
    HandshakeReply = 0x02,      // payload = PeerHandshakeReply
    Completion = 0x20,          // payload = the result, empty for Task
    StreamItem = 0x21,          // payload = the item
    StreamEnd = 0x22,
    Error = 0x23,               // payload = PeerError
    Push = 0x30,                // name = event, payload = arguments
    HostRenamed = 0x31,         // payload = PeerRename
    FileReply = 0x51,           // payload = PeerFileReply - id = the request's
    FileDone = 0x52,            // payload = PeerFileDone - id = the request's
    MediaOpened = 0x61,

    // both ways
    Disconnect = 0x40,          // payload = HubHangUp. Host: the hub ended the session. Client: it is leaving.
    MediaPacket = 0x62,         // payload = one packet
    MediaClose = 0x63
}


internal sealed record PeerMessage(PeerMessageKind Kind, uint Id, string? Name, ReadOnlyMemory<byte> Payload);


/// <summary>
/// The host's answer to a handshake
/// </summary>
/// <param name="Accepted">Whether the client may use the hub</param>
/// <param name="Reason">Why it was refused</param>
/// <param name="HostName">The host's name - the same local name it advertises over BLE</param>
/// <param name="HostId">Identifies the host across transports</param>
/// <param name="Files">Whether the host serves file transfers</param>
/// <param name="MediaMaxPacketSize">The largest media packet, null when the host has no media channels</param>
/// <param name="ResumeToken">Secret proof of this connection, sent back in a <see cref="PeerResume"/> after the session drops. Null when the host won't hold the client (it doesn't hold dropped clients, or the client didn't say it could resume). Absent from hosts older than peer-link resume.</param>
/// <param name="ResumeWindowSeconds">How long the host holds a dropped client</param>
public sealed record PeerHandshakeReply(bool Accepted, string? Reason, string? HostName, string? HostId, bool Files, int? MediaMaxPacketSize, string? ResumeToken = null, int? ResumeWindowSeconds = null);

/// <summary>
/// A client whose session dropped, back on a new one (<see cref="PeerMessageKind.Resume"/>). Accepted, it carries on as the
/// same connection - same id, groups and Items - and the pushes it missed follow the reply.
/// </summary>
public sealed record PeerResume(string Token);

/// <summary>
/// A hub failure relayed to the caller - becomes BleHubRemoteException on the client
/// </summary>
public sealed record PeerError(string Type, string Message);

/// <summary>
/// A client's new name (Rename) or the host's (HostRenamed)
/// </summary>
public sealed record PeerRename(string? Name);

/// <summary>
/// The client wants to move a file. Uploads follow as a file (a Multipeer resource, a Nearby file payload) once the host accepts.
/// </summary>
public sealed record PeerFileRequest(bool Upload, string Name, long? Size);

/// <summary>
/// The host's answer to a file request. <paramref name="Status"/> uses HTTP's codes so both Wi-Fi transports read alike:
/// 200 accepted, 400 bad name, 403 refused, 404 missing or no files, 409 exists, 413 too big.
/// </summary>
public sealed record PeerFileReply(int Status, string? Message, long? Length);

/// <summary>
/// An upload finished on the host - stored, or why not
/// </summary>
public sealed record PeerFileDone(bool Succeeded, string? Message, long Bytes);


[JsonSerializable(typeof(HubHandshake))]
[JsonSerializable(typeof(HubHangUp))]
[JsonSerializable(typeof(PeerHandshakeReply))]
[JsonSerializable(typeof(PeerError))]
[JsonSerializable(typeof(PeerRename))]
[JsonSerializable(typeof(PeerResume))]
[JsonSerializable(typeof(PeerFileRequest))]
[JsonSerializable(typeof(PeerFileReply))]
[JsonSerializable(typeof(PeerFileDone))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal partial class PeerJsonContext : JsonSerializerContext;
