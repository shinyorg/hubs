using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Sample.Calls;

/// <summary>
/// A video codec a call can use
/// </summary>
public enum CallVideoCodec : byte
{
    Jpeg = 1,
    H264 = 2
}


/// <summary>
/// The video codecs a device can both encode and decode
/// </summary>
[Flags]
public enum CallVideoCodecs : byte
{
    None = 0,
    Jpeg = 1,
    H264 = 2
}


/// <summary>
/// How a call's audio travels: PCM16 over Wi-Fi, IMA ADPCM over Bluetooth
/// </summary>
public enum CallAudioCodec : byte
{
    Pcm16 = 1,
    ImaAdpcm = 2
}


/// <summary>
/// The quality a call runs at, set by the slowest link it crosses
/// </summary>
public enum CallProfile : byte
{
    Wifi = 1,
    Ble = 2
}


/// <summary>
/// Why a call ended, or was never answered
/// </summary>
public enum CallEndReason : byte
{
    /// <summary>
    /// Either side hung up an answered call
    /// </summary>
    HungUp = 1,

    /// <summary>
    /// The callee declined
    /// </summary>
    Declined = 2,

    /// <summary>
    /// The callee is already on a call
    /// </summary>
    Busy = 3,

    /// <summary>
    /// The two sides share no codec for what was asked, or speak another call protocol
    /// </summary>
    Unsupported = 4,

    /// <summary>
    /// The callee isn't connected to the host, or can't take calls
    /// </summary>
    Unreachable = 5,

    /// <summary>
    /// Nobody answered in time
    /// </summary>
    Missed = 6,

    /// <summary>
    /// The link to the other side was lost
    /// </summary>
    Dropped = 7,

    /// <summary>
    /// The caller gave up before an answer
    /// </summary>
    Cancelled = 8,

    /// <summary>
    /// The audio or camera couldn't start
    /// </summary>
    Failed = 9
}


internal enum CallPacketType : byte
{
    Invite = 0x01,
    Accept = 0x02,
    Decline = 0x03,
    HangUp = 0x04,
    Ringing = 0x05,
    Audio = 0x10,
    Video = 0x11,
    KeyFrameRequest = 0x12,
    MediaState = 0x13
}


/// <param name="To">The callee's connection id, empty for the host</param>
/// <param name="From">The caller's connection id, empty for the host - filled in by the host, never trusted from a client</param>
/// <param name="VideoCodecs">What the caller can encode and decode</param>
internal sealed record CallInvite(string To, string From, string? FromName, bool WantsVideo, CallProfile Profile, CallVideoCodecs VideoCodecs);

internal sealed record CallAccept(bool Video, CallVideoCodec? VideoCodec, CallProfile Profile);

internal readonly record struct AudioPacket(ushort Sequence, CallAudioCodec Codec, uint Timestamp, ReadOnlyMemory<byte> Payload);

internal readonly record struct VideoPacket(
    ushort Sequence,
    CallVideoCodec Codec,
    bool IsKeyFrame,
    bool IsMirrored,
    int Rotation,
    int Width,
    int Height,
    uint Timestamp,
    ReadOnlyMemory<byte> Payload
);


/// <summary>
/// The packets a call sends over a hub media channel, little-endian:
/// <code>
/// [version u8][type u8][callId 16 bytes] body
/// </code>
/// Strings are <c>[u16 length][UTF-8]</c>. See PLAN.md §9.3.
/// </summary>
internal static class CallProtocol
{
    /// <summary>
    /// Bumped on a breaking change to any packet. An invite of another version is declined as Unsupported.
    /// </summary>
    public const byte Version = 1;

    public const int HeaderSize = 18;

    const byte KeyFrameFlag = 1;
    const byte MirroredFlag = 2;


    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out byte version, out CallPacketType type, out Guid callId)
    {
        if (packet.Length < HeaderSize)
        {
            version = 0;
            type = 0;
            callId = Guid.Empty;
            return false;
        }
        version = packet[0];
        type = (CallPacketType)packet[1];
        callId = new Guid(packet.Slice(2, 16));
        return true;
    }


    public static bool IsMedia(CallPacketType type) => type is CallPacketType.Audio or CallPacketType.Video;


    // ---- writing ----

    public static byte[] Invite(Guid callId, CallInvite invite)
    {
        var w = new Writer(callId, CallPacketType.Invite, 64);
        w.String(invite.To);
        w.String(invite.From);
        w.String(invite.FromName);
        w.Byte(invite.WantsVideo ? (byte)1 : (byte)0);
        w.Byte((byte)invite.Profile);
        w.Byte((byte)invite.VideoCodecs);
        return w.ToArray();
    }


    public static byte[] Accept(Guid callId, CallAccept accept)
    {
        var w = new Writer(callId, CallPacketType.Accept, 3);
        w.Byte(accept.Video ? (byte)1 : (byte)0);
        w.Byte((byte)(accept.VideoCodec ?? 0));
        w.Byte((byte)accept.Profile);
        return w.ToArray();
    }


    public static byte[] Decline(Guid callId, CallEndReason reason, string? message) => End(callId, CallPacketType.Decline, reason, message);
    public static byte[] HangUp(Guid callId, CallEndReason reason, string? message) => End(callId, CallPacketType.HangUp, reason, message);
    public static byte[] Ringing(Guid callId) => new Writer(callId, CallPacketType.Ringing, 0).ToArray();
    public static byte[] KeyFrameRequest(Guid callId) => new Writer(callId, CallPacketType.KeyFrameRequest, 0).ToArray();


    public static byte[] MediaState(Guid callId, bool muted, bool cameraOn)
    {
        var w = new Writer(callId, CallPacketType.MediaState, 2);
        w.Byte(muted ? (byte)1 : (byte)0);
        w.Byte(cameraOn ? (byte)1 : (byte)0);
        return w.ToArray();
    }


    public static byte[] Audio(Guid callId, in AudioPacket audio)
    {
        var w = new Writer(callId, CallPacketType.Audio, 7 + audio.Payload.Length);
        w.UInt16(audio.Sequence);
        w.Byte((byte)audio.Codec);
        w.UInt32(audio.Timestamp);
        w.Bytes(audio.Payload.Span);
        return w.ToArray();
    }


    public static byte[] Video(Guid callId, in VideoPacket video)
    {
        var w = new Writer(callId, CallPacketType.Video, 14 + video.Payload.Length);
        w.UInt16(video.Sequence);
        w.Byte((byte)video.Codec);
        w.Byte((byte)((video.IsKeyFrame ? KeyFrameFlag : 0) | (video.IsMirrored ? MirroredFlag : 0)));
        w.Byte((byte)(((video.Rotation % 360 + 360) % 360) / 90));
        w.UInt16((ushort)video.Width);
        w.UInt16((ushort)video.Height);
        w.UInt32(video.Timestamp);
        w.Bytes(video.Payload.Span);
        return w.ToArray();
    }


    static byte[] End(Guid callId, CallPacketType type, CallEndReason reason, string? message)
    {
        var w = new Writer(callId, type, 3 + (message?.Length ?? 0) * 3);
        w.Byte((byte)reason);
        w.String(message);
        return w.ToArray();
    }


    // ---- reading ----
    // every reader throws FormatException on a short or malformed body; the caller drops the packet

    public static CallInvite ReadInvite(ReadOnlySpan<byte> packet)
    {
        var r = new Reader(packet);
        var to = r.String() ?? "";
        var from = r.String() ?? "";
        var fromName = r.String();
        var wantsVideo = r.Byte() != 0;
        var profile = (CallProfile)r.Byte();
        var codecs = (CallVideoCodecs)r.Byte();
        return new CallInvite(to, from, fromName, wantsVideo, profile == CallProfile.Ble ? CallProfile.Ble : CallProfile.Wifi, codecs);
    }


    public static CallAccept ReadAccept(ReadOnlySpan<byte> packet)
    {
        var r = new Reader(packet);
        var video = r.Byte() != 0;
        var codec = r.Byte();
        var profile = (CallProfile)r.Byte();
        return new CallAccept(
            video,
            codec == 0 ? null : (CallVideoCodec)codec,
            profile == CallProfile.Ble ? CallProfile.Ble : CallProfile.Wifi
        );
    }


    public static (CallEndReason Reason, string? Message) ReadEnd(ReadOnlySpan<byte> packet)
    {
        var r = new Reader(packet);
        return ((CallEndReason)r.Byte(), r.String());
    }


    public static (bool Muted, bool CameraOn) ReadMediaState(ReadOnlySpan<byte> packet)
    {
        var r = new Reader(packet);
        return (r.Byte() != 0, r.Byte() != 0);
    }


    public static AudioPacket ReadAudio(ReadOnlyMemory<byte> packet)
    {
        var r = new Reader(packet.Span);
        var seq = r.UInt16();
        var codec = (CallAudioCodec)r.Byte();
        var ts = r.UInt32();
        return new AudioPacket(seq, codec, ts, packet[r.Position..]);
    }


    public static VideoPacket ReadVideo(ReadOnlyMemory<byte> packet)
    {
        var r = new Reader(packet.Span);
        var seq = r.UInt16();
        var codec = (CallVideoCodec)r.Byte();
        var flags = r.Byte();
        var rotation = (r.Byte() % 4) * 90;
        var width = r.UInt16();
        var height = r.UInt16();
        var ts = r.UInt32();
        return new VideoPacket(
            seq,
            codec,
            (flags & KeyFrameFlag) != 0,
            (flags & MirroredFlag) != 0,
            rotation,
            width,
            height,
            ts,
            packet[r.Position..]
        );
    }


    ref struct Writer
    {
        readonly ArrayBufferWriter<byte> buffer;

        public Writer(Guid callId, CallPacketType type, int bodySizeHint)
        {
            this.buffer = new ArrayBufferWriter<byte>(HeaderSize + Math.Max(bodySizeHint, 0));
            var span = this.buffer.GetSpan(HeaderSize);
            span[0] = Version;
            span[1] = (byte)type;
            callId.TryWriteBytes(span.Slice(2, 16));
            this.buffer.Advance(HeaderSize);
        }

        public void Byte(byte value)
        {
            this.buffer.GetSpan(1)[0] = value;
            this.buffer.Advance(1);
        }

        public void UInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(this.buffer.GetSpan(2), value);
            this.buffer.Advance(2);
        }

        public void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(this.buffer.GetSpan(4), value);
            this.buffer.Advance(4);
        }

        public void Bytes(ReadOnlySpan<byte> value) => this.buffer.Write(value);

        public void String(string? value)
        {
            value ??= "";
            var length = Encoding.UTF8.GetByteCount(value);
            if (length > ushort.MaxValue)
                throw new ArgumentException("String too long for a call packet");

            this.UInt16((ushort)length);
            Encoding.UTF8.GetBytes(value, this.buffer.GetSpan(length));
            this.buffer.Advance(length);
        }

        public byte[] ToArray() => this.buffer.WrittenSpan.ToArray();
    }


    ref struct Reader(ReadOnlySpan<byte> packet)
    {
        readonly ReadOnlySpan<byte> packet = packet;
        public int Position = HeaderSize;

        ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || this.Position + count > this.packet.Length)
                throw new FormatException("Truncated call packet");

            var slice = this.packet.Slice(this.Position, count);
            this.Position += count;
            return slice;
        }

        public byte Byte() => this.Take(1)[0];
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(this.Take(2));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(this.Take(4));

        public string? String()
        {
            var length = this.UInt16();
            return length == 0 ? null : Encoding.UTF8.GetString(this.Take(length));
        }
    }
}
