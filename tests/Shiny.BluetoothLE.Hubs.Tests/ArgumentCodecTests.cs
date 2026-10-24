namespace Shiny.BluetoothLE.Hubs.Tests;

public class ArgumentCodecTests
{
    static ArgumentCodecTests() => Json.AddContext(TestJsonContext.Default);

    readonly IBleHubSerializer serializer = new ShinyJsonBleHubSerializer();


    [Fact]
    public void RoundTrip()
    {
        var bytes = new BleHubArgumentWriter(this.serializer)
            .Write("text")
            .Write(42)
            .Write(new Payload("p", 1))
            .ToArray();

        var reader = new BleHubArgumentReader(this.serializer, bytes).Expect(3, "m");
        Assert.Equal("text", reader.Read<string>());
        Assert.Equal(42, reader.Read<int>());
        Assert.Equal(new Payload("p", 1), reader.Read<Payload>());
    }


    [Fact]
    public void NoArguments() => Assert.Equal(0, new BleHubArgumentReader(this.serializer, BleHubArgumentWriter.Empty).Count);


    [Fact]
    public void CountMismatchIsAProtocolError()
    {
        var bytes = new BleHubArgumentWriter(this.serializer).Write(1).ToArray();
        var ex = Assert.Throws<BleHubProtocolException>(() => new BleHubArgumentReader(this.serializer, bytes).Expect(2, "Move"));
        Assert.Contains("same contract", ex.Message);
    }


    [Fact]
    public void TruncatedArgumentsAreRejected()
    {
        var bytes = new BleHubArgumentWriter(this.serializer).Write("hello world").ToArray();
        var reader = new BleHubArgumentReader(this.serializer, bytes.AsMemory(0, bytes.Length - 3));
        Assert.Throws<BleHubProtocolException>(() => reader.Read<string>());
    }
}
