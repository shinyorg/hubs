using System.Buffers.Binary;

namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// Frames media packets over a byte stream (an L2CAP channel). Each packet is <c>[u32 LE length][bytes]</c>.
///
/// Opening a media channel: the client's first frame is its media token (the UTF-8 bytes of
/// <see cref="HandshakeAck.MediaToken"/>), and the host answers with one frame holding <see cref="Accepted"/>, or closes
/// the channel. Every frame after that is a packet.
///
/// <see cref="Encode"/> is static; an instance decodes one direction of one channel and keeps the partial frame between
/// reads, since a stream hands bytes over in whatever pieces it likes.
/// </summary>
public sealed class MediaFrameCodec
{
    /// <summary>
    /// The length prefix on every frame
    /// </summary>
    public const int LengthPrefixSize = 4;

    /// <summary>
    /// The largest single write <see cref="Encode"/> hands the stream unless told otherwise
    /// </summary>
    public const int DefaultMaxWriteSize = 4096;

    /// <summary>
    /// The host's one-byte answer to a valid media token
    /// </summary>
    public const byte Accepted = 0x01;

    readonly int maxFrameSize;
    readonly byte[] prefix = new byte[LengthPrefixSize];
    int prefixFilled;
    byte[]? body;
    int bodyFilled;


    /// <param name="maxFrameSize">The largest frame body accepted - a bigger length prefix is a protocol error</param>
    public MediaFrameCodec(int maxFrameSize)
    {
        if (maxFrameSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFrameSize), "Max frame size must be at least 1");

        this.maxFrameSize = maxFrameSize;
    }


    /// <summary>
    /// Encodes one packet as one frame, split into writes no larger than <paramref name="maxWriteSize"/>. Every write is
    /// its own array, since a stream may only queue it.
    /// </summary>
    public static IReadOnlyList<byte[]> Encode(ReadOnlySpan<byte> packet, int maxWriteSize = DefaultMaxWriteSize)
    {
        if (maxWriteSize < LengthPrefixSize)
            throw new ArgumentOutOfRangeException(nameof(maxWriteSize), $"Write size must be at least {LengthPrefixSize}");

        var total = LengthPrefixSize + packet.Length;
        if (total <= maxWriteSize)
        {
            var single = new byte[total];
            BinaryPrimitives.WriteUInt32LittleEndian(single, (uint)packet.Length);
            packet.CopyTo(single.AsSpan(LengthPrefixSize));
            return [single];
        }

        var writes = new List<byte[]>((total + maxWriteSize - 1) / maxWriteSize);
        var first = new byte[maxWriteSize];
        BinaryPrimitives.WriteUInt32LittleEndian(first, (uint)packet.Length);
        packet[..(maxWriteSize - LengthPrefixSize)].CopyTo(first.AsSpan(LengthPrefixSize));
        writes.Add(first);

        var offset = maxWriteSize - LengthPrefixSize;
        while (offset < packet.Length)
        {
            var chunk = Math.Min(maxWriteSize, packet.Length - offset);
            writes.Add(packet.Slice(offset, chunk).ToArray());
            offset += chunk;
        }
        return writes;
    }


    /// <summary>
    /// Feeds bytes read from the stream. Returns the frames they complete, in order - often none, sometimes several.
    /// Throws <see cref="BleHubProtocolException"/> for a frame longer than the max; the stream can't be trusted after that.
    /// </summary>
    public IReadOnlyList<byte[]> Add(ReadOnlySpan<byte> data)
    {
        List<byte[]>? frames = null;
        while (true)
        {
            if (this.body == null)
            {
                if (data.IsEmpty)
                    break;

                var take = Math.Min(LengthPrefixSize - this.prefixFilled, data.Length);
                data[..take].CopyTo(this.prefix.AsSpan(this.prefixFilled));
                this.prefixFilled += take;
                data = data[take..];
                if (this.prefixFilled < LengthPrefixSize)
                    break;

                var length = BinaryPrimitives.ReadUInt32LittleEndian(this.prefix);
                if (length > (uint)this.maxFrameSize)
                    throw new BleHubProtocolException($"Media frame of {length} bytes exceeds the {this.maxFrameSize} byte limit");

                this.body = length == 0 ? [] : new byte[length];
                this.bodyFilled = 0;
            }

            var remaining = this.body.Length - this.bodyFilled;
            if (remaining > 0)
            {
                if (data.IsEmpty)
                    break;

                var take = Math.Min(remaining, data.Length);
                data[..take].CopyTo(this.body.AsSpan(this.bodyFilled));
                this.bodyFilled += take;
                data = data[take..];
                if (this.bodyFilled < this.body.Length)
                    break;
            }

            (frames ??= new()).Add(this.body);
            this.body = null;
            this.prefixFilled = 0;
        }
        return frames ?? (IReadOnlyList<byte[]>)[];
    }
}
