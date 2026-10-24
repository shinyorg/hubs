using System.Buffers.Binary;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs.Tests;

public class MediaFrameCodecTests
{
    static byte[] Packet(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }


    [Fact]
    public void SmallPacketIsOneWriteWithItsLength()
    {
        var writes = MediaFrameCodec.Encode(new byte[] { 9, 8, 7 });
        var write = Assert.Single(writes);
        Assert.Equal(7, write.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(write));
        Assert.Equal(new byte[] { 9, 8, 7 }, write[4..]);
    }


    [Fact]
    public void LargePacketIsSplitIntoBoundedWrites()
    {
        var packet = Packet(10_000);
        var writes = MediaFrameCodec.Encode(packet, 1000);

        Assert.Equal(11, writes.Count);   // 10,004 bytes framed
        Assert.All(writes, x => Assert.True(x.Length <= 1000));
        Assert.Equal(10_000u, BinaryPrimitives.ReadUInt32LittleEndian(writes[0]));
        Assert.Equal(packet, writes.SelectMany(x => x).Skip(4).ToArray());
    }


    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(251)]
    [InlineData(100_000)]
    public void DecodesFramesWhateverPiecesTheBytesArriveIn(int pieceSize)
    {
        var packets = new[] { Packet(0), Packet(1, 2), Packet(4, 3), Packet(5000, 4), Packet(17, 5) };
        var stream = packets.SelectMany(p => MediaFrameCodec.Encode(p, 64)).SelectMany(x => x).ToArray();

        var codec = new MediaFrameCodec(10_000);
        var decoded = new List<byte[]>();
        for (var offset = 0; offset < stream.Length; offset += pieceSize)
            decoded.AddRange(codec.Add(stream.AsSpan(offset, Math.Min(pieceSize, stream.Length - offset))));

        Assert.Equal(packets.Length, decoded.Count);
        for (var i = 0; i < packets.Length; i++)
            Assert.Equal(packets[i], decoded[i]);
    }


    [Fact]
    public void FrameOverTheLimitIsAProtocolError()
    {
        var codec = new MediaFrameCodec(100);
        Assert.Single(codec.Add(MediaFrameCodec.Encode(Packet(100))[0]));
        Assert.Throws<BleHubProtocolException>(() => codec.Add(MediaFrameCodec.Encode(Packet(101))[0]));
    }


    [Fact]
    public void HandshakeAckFromAnOlderHostHasNoMediaChannel()
    {
        var json = """{"Accepted":true,"Reason":null,"HostName":"Old","FileTransferPsm":129,"FileTransferSecure":false}"""u8.ToArray();
        var ack = ProtocolSerializer.ReadHandshakeAck(json);

        Assert.True(ack.Accepted);
        Assert.Equal(129, ack.FileTransferPsm);
        Assert.Equal(0, ack.MediaChannelPsm);
        Assert.Null(ack.MediaToken);
        Assert.Equal(0, ack.MediaMaxPacketSize);
    }


    [Fact]
    public void HandshakeAckCarriesTheMediaChannel()
    {
        var ack = new HandshakeAck(true, null, "Host", 0, false, 130, true, "abc", 4096);
        var read = ProtocolSerializer.ReadHandshakeAck(ProtocolSerializer.Serialize(ack));
        Assert.Equal(ack, read);
    }
}
