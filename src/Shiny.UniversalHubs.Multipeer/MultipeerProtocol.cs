using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// The Multipeer side of the wire. One MCSession per hub connection, carrying reliable, ordered messages:
/// <code>
/// [0]     protocol version
/// [1]     kind
/// [2..5]  message id (little endian) - correlates replies, stream items and cancels with their call
/// [6]     name length
/// [7..]   name (UTF-8): the hub's characteristic UUID, method or event
/// [...]   payload: hub arguments and results exactly as IBleHubSerializer wrote them, or a control record as JSON
/// </code>
/// Unlike Wi-Fi, values are never re-framed as JSON, so any IBleHubSerializer works. Files ride Multipeer resources.
/// </summary>
public static partial class MultipeerProtocol
{
    /// <summary>
    /// Bumped on a breaking change to anything here. The host refuses a handshake from another version.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Multipeer resource names carry the file request's message id
    /// </summary>
    public const string FileResourcePrefix = "hubfile-";

    // advertisement (discoveryInfo) keys - the same as the mDNS TXT keys
    public const string InfoProtocol = UniversalHubProtocol.TxtProtocol;
    public const string InfoHostId = UniversalHubProtocol.TxtHostId;
    public const string InfoName = UniversalHubProtocol.TxtName;
    public const string InfoHubs = UniversalHubProtocol.TxtHubs;

    const int HeaderSize = 7;


    /// <summary>
    /// Throws unless <paramref name="serviceType"/> is a Multipeer service type: 1-15 characters of lowercase ASCII letters,
    /// digits and hyphens, with at least one letter and no leading, trailing or doubled hyphen. It must differ from the
    /// hubs' mDNS service types - Multipeer advertises over Bonjour as <c>_{serviceType}._tcp</c> too.
    /// </summary>
    public static void ValidateServiceType(string serviceType)
    {
        if (String.IsNullOrWhiteSpace(serviceType) || !ServiceTypeRegex().IsMatch(serviceType) || serviceType.Contains("--", StringComparison.Ordinal) || !serviceType.Any(Char.IsAsciiLetterLower))
            throw new ArgumentException($"'{serviceType}' is not a Multipeer service type - expected 1-15 lowercase letters, digits and hyphens, like mygame-mc", nameof(serviceType));
    }

    /// <summary>
    /// The NSBonjourServices entries an iOS / macOS app needs for <paramref name="serviceType"/>
    /// </summary>
    public static IReadOnlyList<string> BonjourServices(string serviceType) => [$"_{serviceType}._tcp", $"_{serviceType}._udp"];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,13}[a-z0-9])?$")]
    private static partial Regex ServiceTypeRegex();


    internal static byte[] Encode(MultipeerMessageKind kind, uint id, string? name, ReadOnlySpan<byte> payload)
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


    internal static byte[] Encode<T>(MultipeerMessageKind kind, uint id, string? name, T value, JsonTypeInfo<T> type)
        => Encode(kind, id, name, JsonSerializer.SerializeToUtf8Bytes(value, type));


    internal static MultipeerMessage Decode(ReadOnlyMemory<byte> message)
    {
        var span = message.Span;
        if (span.Length < HeaderSize)
            throw new BleHubProtocolException($"Multipeer message is too short ({span.Length} bytes)");
        if (span[0] != Version)
            throw new BleHubProtocolException($"Unsupported Multipeer protocol version {span[0]}");

        var nameLength = span[6];
        if (span.Length < HeaderSize + nameLength)
            throw new BleHubProtocolException("Multipeer message is shorter than its name");

        return new MultipeerMessage(
            (MultipeerMessageKind)span[1],
            BinaryPrimitives.ReadUInt32LittleEndian(span[2..]),
            nameLength == 0 ? null : Encoding.UTF8.GetString(span.Slice(HeaderSize, nameLength)),
            message[(HeaderSize + nameLength)..]
        );
    }


    internal static T Read<T>(this MultipeerMessage message, JsonTypeInfo<T> type)
        => JsonSerializer.Deserialize(message.Payload.Span, type) ?? throw new BleHubProtocolException($"{message.Kind} has no body");

    internal static string FileResourceName(uint id) => FileResourcePrefix + id;

    internal static uint? ReadFileResourceId(string resourceName)
        => resourceName.StartsWith(FileResourcePrefix, StringComparison.Ordinal) && UInt32.TryParse(resourceName.AsSpan(FileResourcePrefix.Length), out var id) ? id : null;
}


/// <summary>
/// What a Multipeer message is. Values travel on the wire - never renumber.
/// </summary>
public enum MultipeerMessageKind : byte
{
    // client -> host
    Handshake = 0x01,           // name = characteristic UUID, payload = HubHandshake
    Invoke = 0x10,              // name = method, payload = arguments
    Stream = 0x11,              // name = method, payload = arguments
    Cancel = 0x12,              // id = the call to cancel
    Rename = 0x13,              // payload = MultipeerRename
    FileRequest = 0x50,         // payload = MultipeerFileRequest
    MediaOpen = 0x60,

    // host -> client
    HandshakeReply = 0x02,      // payload = MultipeerHandshakeReply
    Completion = 0x20,          // payload = the result, empty for Task
    StreamItem = 0x21,          // payload = the item
    StreamEnd = 0x22,
    Error = 0x23,               // payload = MultipeerError
    Push = 0x30,                // name = event, payload = arguments
    HostRenamed = 0x31,         // payload = MultipeerRename
    FileReply = 0x51,           // payload = MultipeerFileReply - id = the request's
    FileDone = 0x52,            // payload = MultipeerFileDone - id = the request's
    MediaOpened = 0x61,

    // both ways
    Disconnect = 0x40,          // payload = HubHangUp. Host: the hub ended the session. Client: it is leaving.
    MediaPacket = 0x62,         // payload = one packet
    MediaClose = 0x63
}


internal sealed record MultipeerMessage(MultipeerMessageKind Kind, uint Id, string? Name, ReadOnlyMemory<byte> Payload);


/// <summary>
/// The host's answer to a handshake
/// </summary>
/// <param name="Accepted">Whether the client may use the hub</param>
/// <param name="Reason">Why it was refused</param>
/// <param name="HostName">The host's name - the same local name it advertises over BLE</param>
/// <param name="HostId">Identifies the host across transports</param>
/// <param name="Files">Whether the host serves file transfers</param>
/// <param name="MediaMaxPacketSize">The largest media packet, null when the host has no media channels</param>
public sealed record MultipeerHandshakeReply(bool Accepted, string? Reason, string? HostName, string? HostId, bool Files, int? MediaMaxPacketSize);

/// <summary>
/// A hub failure relayed to the caller - becomes BleHubRemoteException on the client
/// </summary>
public sealed record MultipeerError(string Type, string Message);

/// <summary>
/// A client's new name (Rename) or the host's (HostRenamed)
/// </summary>
public sealed record MultipeerRename(string? Name);

/// <summary>
/// The client wants to move a file. Uploads follow with a Multipeer resource once the host accepts.
/// </summary>
public sealed record MultipeerFileRequest(bool Upload, string Name, long? Size);

/// <summary>
/// The host's answer to a file request. <paramref name="Status"/> uses HTTP's codes so both Wi-Fi transports read alike:
/// 200 accepted, 400 bad name, 403 refused, 404 missing or no files, 409 exists, 413 too big.
/// </summary>
public sealed record MultipeerFileReply(int Status, string? Message, long? Length);

/// <summary>
/// An upload finished on the host - stored, or why not
/// </summary>
public sealed record MultipeerFileDone(bool Succeeded, string? Message, long Bytes);


[JsonSerializable(typeof(HubHandshake))]
[JsonSerializable(typeof(HubHangUp))]
[JsonSerializable(typeof(MultipeerHandshakeReply))]
[JsonSerializable(typeof(MultipeerError))]
[JsonSerializable(typeof(MultipeerRename))]
[JsonSerializable(typeof(MultipeerFileRequest))]
[JsonSerializable(typeof(MultipeerFileReply))]
[JsonSerializable(typeof(MultipeerFileDone))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal partial class MultipeerJsonContext : JsonSerializerContext;
