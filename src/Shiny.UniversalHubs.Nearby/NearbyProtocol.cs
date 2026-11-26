using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.UniversalHubs;

/// <summary>
/// What is Nearby's own on the wire. Hub sessions carry <see cref="PeerProtocol"/> messages like Multipeer; underneath,
/// Nearby allows one connection between two devices, so every hub session is a channel inside it.
/// <para>
/// Endpoint info (advertised by the host, at most <see cref="MaxEndpointInfoLength"/> bytes):
/// <code>
/// [0]     PeerProtocol.Version
/// [1]     host id length, then the host id (UTF-8)
/// [..]    hub count, then a 4 byte tag per running hub (<see cref="HubTag"/>)
/// [..]    the host's name (UTF-8), cut to fit
/// </code>
/// A client's connection request carries <c>[version][its name]</c>.
/// </para>
/// <para>
/// Frames (one BYTES payload each, at most <see cref="MaxBytesPayloadSize"/> bytes):
/// <code>
/// [0]     frame kind (<see cref="NearbyFrameKind"/>), | 0x80 when the sender opened the channel
/// [1..4]  channel id (little endian) - numbered by whoever opened it
/// [5..]   a whole message, a piece of one, or a file name (8 byte payload id, then UTF-8)
/// </code>
/// </para>
/// </summary>
public static class NearbyProtocol
{
    /// <summary>
    /// Nearby's limit on endpoint info
    /// </summary>
    public const int MaxEndpointInfoLength = 131;

    /// <summary>
    /// Nearby's limit on one BYTES payload - longer messages travel in pieces
    /// </summary>
    public const int MaxBytesPayloadSize = 32 * 1024;

    internal const int FrameHeaderSize = 5;
    internal const byte OpenerFlag = 0x80;


    /// <summary>
    /// Throws unless <paramref name="serviceId"/> can be a Nearby service id: 1-64 printable ASCII characters. Every app
    /// that should find each other uses the same one, conventionally a reverse-DNS name like <c>com.mycompany.mygame</c>.
    /// </summary>
    public static void ValidateServiceId(string serviceId)
    {
        if (String.IsNullOrWhiteSpace(serviceId) || serviceId.Length > 64 || serviceId.Any(x => x < 0x21 || x > 0x7E))
            throw new ArgumentException($"'{serviceId}' is not a Nearby service id - expected 1-64 printable ASCII characters, like com.mycompany.mygame", nameof(serviceId));
    }


    /// <summary>
    /// The NSBonjourServices entry an iOS / macOS app needs for <paramref name="serviceId"/>: Nearby advertises over
    /// Bonjour as <c>_</c> + the first 6 bytes of SHA-256(serviceId) in upper-case hex + <c>._tcp</c>
    /// </summary>
    public static IReadOnlyList<string> BonjourServices(string serviceId)
    {
        ValidateServiceId(serviceId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serviceId));
        return [$"_{Convert.ToHexString(hash, 0, 6)}._tcp"];
    }


    /// <summary>
    /// The 4 bytes a hub is listed under in the endpoint info: the start of SHA-256 of its lower-case characteristic UUID.
    /// Room for a few dozen hubs where the UUIDs themselves would not fit two.
    /// </summary>
    public static uint HubTag(string characteristicUuid)
        => BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(characteristicUuid.ToLowerInvariant())));


    internal static byte[] EncodeEndpointInfo(NearbyEndpointInfo info)
    {
        var hostId = Encoding.UTF8.GetBytes(info.HostId ?? "");
        if (hostId.Length > byte.MaxValue)
            throw new ArgumentException("The host id is too long for a Nearby endpoint");

        var fixedLength = 1 + 1 + hostId.Length + 1 + info.Hubs.Count * 4;
        if (info.Hubs.Count > byte.MaxValue || fixedLength > MaxEndpointInfoLength)
            throw new InvalidOperationException($"{info.Hubs.Count} hubs don't fit in a Nearby endpoint ({MaxEndpointInfoLength} bytes)");

        var name = Truncate(info.Name, MaxEndpointInfoLength - fixedLength);
        var buffer = new byte[fixedLength + name.Length];
        var i = 0;
        buffer[i++] = PeerProtocol.Version;
        buffer[i++] = (byte)hostId.Length;
        hostId.CopyTo(buffer, i);
        i += hostId.Length;
        buffer[i++] = (byte)info.Hubs.Count;
        foreach (var tag in info.Hubs)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i), tag);
            i += 4;
        }
        name.CopyTo(buffer, i);
        return buffer;
    }


    /// <summary>
    /// Null when it isn't a hub host's endpoint, or one speaking another version
    /// </summary>
    internal static NearbyEndpointInfo? DecodeEndpointInfo(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 3 || data[0] != PeerProtocol.Version)
                return null;

            var i = 1;
            var hostIdLength = data[i++];
            var hostId = hostIdLength == 0 ? null : Encoding.UTF8.GetString(data.Slice(i, hostIdLength));
            i += hostIdLength;
            var count = data[i++];
            var hubs = new uint[count];
            for (var h = 0; h < count; h++)
            {
                hubs[h] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
                i += 4;
            }
            return new NearbyEndpointInfo(hostId, Encoding.UTF8.GetString(data[i..]), hubs);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }


    internal static byte[] EncodeRequestInfo(string name)
    {
        var bytes = Truncate(name, MaxEndpointInfoLength - 1);
        var buffer = new byte[bytes.Length + 1];
        buffer[0] = PeerProtocol.Version;
        bytes.CopyTo(buffer, 1);
        return buffer;
    }


    internal static string DecodeRequestInfo(ReadOnlySpan<byte> data)
        => data.Length > 1 && data[0] == PeerProtocol.Version ? Encoding.UTF8.GetString(data[1..]) : "Hub client";


    internal static byte[] Frame(NearbyFrameKind kind, bool opener, uint channel, ReadOnlySpan<byte> body)
    {
        var frame = new byte[FrameHeaderSize + body.Length];
        frame[0] = (byte)((byte)kind | (opener ? OpenerFlag : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), channel);
        body.CopyTo(frame.AsSpan(FrameHeaderSize));
        return frame;
    }


    internal static byte[] FileNameFrame(bool opener, uint channel, long payloadId, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var body = new byte[8 + nameBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(body, payloadId);
        nameBytes.CopyTo(body, 8);
        return Frame(NearbyFrameKind.FileName, opener, channel, body);
    }


    /// <summary>
    /// A message as the frames it travels in - one when it fits
    /// </summary>
    internal static IEnumerable<byte[]> Split(bool opener, uint channel, byte[] message)
    {
        const int piece = MaxBytesPayloadSize - FrameHeaderSize;
        if (message.Length <= piece)
        {
            yield return Frame(NearbyFrameKind.Message, opener, channel, message);
            yield break;
        }
        for (var offset = 0; offset < message.Length; offset += piece)
        {
            var length = Math.Min(piece, message.Length - offset);
            var last = offset + length >= message.Length;
            yield return Frame(last ? NearbyFrameKind.Last : NearbyFrameKind.Part, opener, channel, message.AsSpan(offset, length));
        }
    }


    internal static (NearbyFrameKind Kind, bool SenderOpened, uint Channel, ReadOnlyMemory<byte> Body) ReadFrame(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        if (span.Length < FrameHeaderSize)
            throw new HubProtocolException($"Nearby frame is too short ({span.Length} bytes)");

        return (
            (NearbyFrameKind)(span[0] & ~OpenerFlag),
            (span[0] & OpenerFlag) != 0,
            BinaryPrimitives.ReadUInt32LittleEndian(span[1..]),
            frame[FrameHeaderSize..]
        );
    }


    static byte[] Truncate(string? value, int maxBytes)
    {
        if (String.IsNullOrEmpty(value) || maxBytes <= 0)
            return [];

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
            return bytes;

        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            count += rune.Utf8SequenceLength;
            if (count > maxBytes)
                break;
            builder.Append(rune.ToString());
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}


/// <summary>
/// What a Nearby frame is. Values travel on the wire - never renumber.
/// </summary>
public enum NearbyFrameKind : byte
{
    /// <summary>
    /// A whole PeerProtocol message
    /// </summary>
    Message = 0x00,

    /// <summary>
    /// A piece of a message too long for one payload - more follow
    /// </summary>
    Part = 0x01,

    /// <summary>
    /// The last piece
    /// </summary>
    Last = 0x02,

    /// <summary>
    /// Opens a channel - a hub session. Its messages follow.
    /// </summary>
    Open = 0x10,

    /// <summary>
    /// Ends a channel
    /// </summary>
    Close = 0x11,

    /// <summary>
    /// Names the file payload that goes (or went) with it: 8 byte payload id, then the name
    /// </summary>
    FileName = 0x20
}


/// <summary>
/// What a hub host advertises over Nearby
/// </summary>
/// <param name="HostId">Identifies the host across transports</param>
/// <param name="Name">The host's name - the BLE local name unless set</param>
/// <param name="Hubs">The running hubs, as <see cref="NearbyProtocol.HubTag"/>s</param>
internal sealed record NearbyEndpointInfo(string? HostId, string Name, IReadOnlyList<uint> Hubs);
