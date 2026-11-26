using System.Buffers.Binary;
using System.Text;

namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// Splits logical messages into MTU sized frames.
///
/// Frame layout (little endian):
/// <code>
/// [0]    protocol version
/// [1]    kind
/// [2..3] message id
/// [4..5] sequence
/// [6]    flags (bit0 = first, bit1 = last)
/// [7..10] total body length - FIRST frame only
/// ...    body chunk
/// </code>
/// The body is <c>[name length:1][name utf8][payload]</c> and is split across frames.
/// </summary>
public static class FrameCodec
{
    public const byte ProtocolVersion = 1;
    public const int HeaderSize = 7;
    public const int FirstHeaderSize = HeaderSize + 4;

    /// <summary>
    /// ATT_MTU 23 (the BLE minimum) leaves 20 bytes per write/notification
    /// </summary>
    public const int MinFrameSize = 20;

    /// <summary>
    /// A characteristic value can never exceed 512 bytes
    /// </summary>
    public const int MaxFrameSize = 512;

    /// <summary>
    /// The ATT header that precedes every write and notification
    /// </summary>
    public const int AttHeaderSize = 3;

    /// <summary>
    /// The largest ATT MTU there is (517 - Android's GATT_MAX_MTU_SIZE). The client asks for it on every new BLE connection;
    /// frames still stop at <see cref="MaxFrameSize"/>.
    /// </summary>
    public const int MaxAttMtu = 517;

    public const byte FlagFirst = 0x01;
    public const byte FlagLast = 0x02;

    const int MaxNameBytes = byte.MaxValue;


    /// <summary>
    /// Gets the usable frame size for a negotiated ATT MTU (3 bytes of ATT header are subtracted). Pass the ATT MTU, not
    /// Shiny's IPeripheral.Mtu - that is already the payload size.
    /// </summary>
    public static int GetFrameSize(int mtu) => Math.Clamp(mtu - AttHeaderSize, MinFrameSize, MaxFrameSize);


    /// <summary>
    /// Encodes a logical message into one or more frames no larger than <paramref name="maxFrameSize"/>
    /// </summary>
    public static IReadOnlyList<byte[]> Encode(FrameKind kind, ushort messageId, string? name, ReadOnlySpan<byte> payload, int maxFrameSize)
    {
        if (maxFrameSize < MinFrameSize)
            throw new ArgumentOutOfRangeException(nameof(maxFrameSize), $"Frame size must be at least {MinFrameSize}");

        if (maxFrameSize > MaxFrameSize)
            maxFrameSize = MaxFrameSize;

        var idBytes = name == null ? [] : Encoding.UTF8.GetBytes(name);
        if (idBytes.Length > MaxNameBytes)
            throw new ArgumentException($"Name '{name}' is longer than {MaxNameBytes} UTF-8 bytes", nameof(name));

        var bodyLength = 1 + idBytes.Length + payload.Length;
        var body = new byte[bodyLength];
        body[0] = (byte)idBytes.Length;
        idBytes.CopyTo(body, 1);
        payload.CopyTo(body.AsSpan(1 + idBytes.Length));

        var frames = new List<byte[]>();
        var offset = 0;
        ushort seq = 0;

        do
        {
            var first = seq == 0;
            var headerSize = first ? FirstHeaderSize : HeaderSize;
            var chunk = Math.Min(maxFrameSize - headerSize, bodyLength - offset);
            var last = offset + chunk >= bodyLength;

            var frame = new byte[headerSize + chunk];
            frame[0] = ProtocolVersion;
            frame[1] = (byte)kind;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), messageId);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), seq);
            frame[6] = (byte)((first ? FlagFirst : 0) | (last ? FlagLast : 0));
            if (first)
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), (uint)bodyLength);

            body.AsSpan(offset, chunk).CopyTo(frame.AsSpan(headerSize));
            frames.Add(frame);

            offset += chunk;
            if (seq == ushort.MaxValue && !last)
                throw new ArgumentException("Message is too large to be framed at this frame size", nameof(payload));

            seq++;
        }
        while (offset < bodyLength);

        return frames;
    }


    /// <summary>
    /// Reads the header of a single frame
    /// </summary>
    public static FrameHeader ReadHeader(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderSize)
            throw new BleHubProtocolException($"Frame is too short ({frame.Length} bytes)");

        if (frame[0] != ProtocolVersion)
            throw new BleHubProtocolException($"Unsupported protocol version {frame[0]}");

        var flags = frame[6];
        var first = (flags & FlagFirst) != 0;
        uint totalLength = 0;
        if (first)
        {
            if (frame.Length < FirstHeaderSize)
                throw new BleHubProtocolException("First frame is too short");

            totalLength = BinaryPrimitives.ReadUInt32LittleEndian(frame[7..]);
        }

        return new FrameHeader(
            (FrameKind)frame[1],
            BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]),
            first,
            (flags & FlagLast) != 0,
            totalLength,
            first ? FirstHeaderSize : HeaderSize
        );
    }


    /// <summary>
    /// Splits a reassembled body into its name and payload
    /// </summary>
    public static BleHubMessage ParseBody(FrameKind kind, ushort messageId, byte[] body)
    {
        if (body.Length < 1)
            throw new BleHubProtocolException("Message body is empty");

        var idLength = body[0];
        if (body.Length < 1 + idLength)
            throw new BleHubProtocolException("Message body is shorter than its name");

        var name = idLength == 0 ? null : Encoding.UTF8.GetString(body, 1, idLength);
        var payload = new ReadOnlyMemory<byte>(body, 1 + idLength, body.Length - 1 - idLength);
        return new BleHubMessage(kind, messageId, name, payload);
    }
}


public readonly record struct FrameHeader(
    FrameKind Kind,
    ushort MessageId,
    ushort Sequence,
    bool IsFirst,
    bool IsLast,
    uint TotalLength,
    int HeaderSize
);
