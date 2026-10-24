using System.Text;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs.Tests;

public class FrameCodecTests
{
    static MessageReassembler NewReassembler(int max = 1024 * 1024, TimeProvider? time = null)
        => new(max, TimeSpan.FromSeconds(30), 16, time);


    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 20)]
    [InlineData(9, 20)]
    [InlineData(10, 20)]
    [InlineData(500, 20)]
    [InlineData(500, 182)]
    [InlineData(100_000, 509)]
    public void RoundTrip(int payloadSize, int frameSize)
    {
        var payload = Enumerable.Range(0, payloadSize).Select(x => (byte)x).ToArray();
        var frames = FrameCodec.Encode(FrameKind.Invoke, 42, "ttt.move", payload, frameSize);

        Assert.All(frames, f => Assert.True(f.Length <= frameSize));

        var reassembler = NewReassembler();
        BleHubMessage? message = null;
        for (var i = 0; i < frames.Count; i++)
        {
            message = reassembler.Add(frames[i]);
            if (i < frames.Count - 1)
                Assert.Null(message);
        }

        Assert.NotNull(message);
        Assert.Equal(FrameKind.Invoke, message.Kind);
        Assert.Equal((ushort)42, message.MessageId);
        Assert.Equal("ttt.move", message.Name);
        Assert.Equal(payload, message.Payload.ToArray());
        Assert.Equal(0, reassembler.PendingCount);
    }


    [Fact]
    public void NoContractId()
    {
        var frames = FrameCodec.Encode(FrameKind.Completion, 7, null, [], 20);
        Assert.Single(frames);

        var message = NewReassembler().Add(frames[0]);
        Assert.NotNull(message);
        Assert.Null(message.Name);
        Assert.True(message.Payload.IsEmpty);
    }


    [Fact]
    public void InterleavedMessagesReassembleIndependently()
    {
        var a = FrameCodec.Encode(FrameKind.Invoke, 1, "a", Encoding.UTF8.GetBytes(new string('a', 100)), 20);
        var b = FrameCodec.Encode(FrameKind.StreamInvoke, 2, "b", Encoding.UTF8.GetBytes(new string('b', 100)), 20);
        var r = NewReassembler();

        var results = new List<BleHubMessage>();
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count && r.Add(a[i]) is { } ma) results.Add(ma);
            if (i < b.Count && r.Add(b[i]) is { } mb) results.Add(mb);
        }

        Assert.Equal(2, results.Count);
        Assert.Equal(new string('a', 100), Encoding.UTF8.GetString(results.Single(x => x.Name == "a").Payload.Span));
        Assert.Equal(new string('b', 100), Encoding.UTF8.GetString(results.Single(x => x.Name == "b").Payload.Span));
    }


    [Fact]
    public void OutOfOrderFrameThrowsAndDropsMessage()
    {
        var frames = FrameCodec.Encode(FrameKind.Invoke, 5, "x", new byte[100], 20);
        var r = NewReassembler();
        r.Add(frames[0]);

        var ex = Assert.Throws<BleHubProtocolException>(() => r.Add(frames[2]));
        Assert.Equal((ushort)5, ex.MessageId);
        Assert.Equal(0, r.PendingCount);
    }


    [Fact]
    public void ContinuationWithoutFirstThrows()
    {
        var frames = FrameCodec.Encode(FrameKind.Invoke, 5, "x", new byte[100], 20);
        Assert.Throws<BleHubProtocolException>(() => NewReassembler().Add(frames[1]));
    }


    [Fact]
    public void OversizedMessageRejected()
    {
        var frames = FrameCodec.Encode(FrameKind.Invoke, 9, "x", new byte[5000], 200);
        var ex = Assert.Throws<BleHubProtocolException>(() => NewReassembler(max: 1000).Add(frames[0]));
        Assert.Equal((ushort)9, ex.MessageId);
    }


    [Fact]
    public void WrongVersionRejected()
    {
        var frame = FrameCodec.Encode(FrameKind.Completion, 1, null, [], 20)[0];
        frame[0] = 99;
        Assert.Throws<BleHubProtocolException>(() => NewReassembler().Add(frame));
    }


    [Fact]
    public void ShortFrameRejected()
        => Assert.Throws<BleHubProtocolException>(() => NewReassembler().Add(new byte[3]));


    [Fact]
    public void StalePartialsExpire()
    {
        var time = new ManualTime();
        var r = new MessageReassembler(10_000, TimeSpan.FromSeconds(5), 16, time);
        var frames = FrameCodec.Encode(FrameKind.Invoke, 3, "x", new byte[100], 20);

        r.Add(frames[0]);
        Assert.Equal(1, r.PendingCount);

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.Throws<BleHubProtocolException>(() => r.Add(frames[1]));
        Assert.Equal(0, r.PendingCount);
    }


    [Fact]
    public void TooManyPartialsRejected()
    {
        var r = new MessageReassembler(10_000, TimeSpan.FromSeconds(30), 2);
        r.Add(FrameCodec.Encode(FrameKind.Invoke, 1, "x", new byte[100], 20)[0]);
        r.Add(FrameCodec.Encode(FrameKind.Invoke, 2, "x", new byte[100], 20)[0]);
        Assert.Throws<BleHubProtocolException>(() => r.Add(FrameCodec.Encode(FrameKind.Invoke, 3, "x", new byte[100], 20)[0]));
    }


    [Theory]
    [InlineData(23, 20)]
    [InlineData(185, 182)]
    [InlineData(517, 512)]
    [InlineData(10, 20)]
    public void FrameSizeFromMtu(int mtu, int expected) => Assert.Equal(expected, FrameCodec.GetFrameSize(mtu));


    sealed class ManualTime : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan by) => this.now += by;
        public override DateTimeOffset GetUtcNow() => this.now;
    }
}
