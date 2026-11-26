namespace Shiny.UniversalHubs.Tests;

public class ArgumentCodecTests
{
    static ArgumentCodecTests() => Json.AddContext(TestJsonContext.Default);

    readonly IHubSerializer serializer = new JsonHubSerializer();


    [Fact]
    public void RoundTrip()
    {
        var bytes = new HubArgumentWriter(this.serializer)
            .Write("text")
            .Write(42)
            .Write(new Payload("p", 1))
            .ToArray();

        var reader = new HubArgumentReader(this.serializer, bytes).Expect(3, "m");
        Assert.Equal("text", reader.Read<string>());
        Assert.Equal(42, reader.Read<int>());
        Assert.Equal(new Payload("p", 1), reader.Read<Payload>());
    }


    [Fact]
    public void NoArguments() => Assert.Equal(0, new HubArgumentReader(this.serializer, HubArgumentWriter.Empty).Count);


    [Fact]
    public void CountMismatchIsAProtocolError()
    {
        var bytes = new HubArgumentWriter(this.serializer).Write(1).ToArray();
        var ex = Assert.Throws<HubProtocolException>(() => new HubArgumentReader(this.serializer, bytes).Expect(2, "Move"));
        Assert.Contains("same contract", ex.Message);
    }


    [Fact]
    public void TruncatedArgumentsAreRejected()
    {
        var bytes = new HubArgumentWriter(this.serializer).Write("hello world").ToArray();
        var reader = new HubArgumentReader(this.serializer, bytes.AsMemory(0, bytes.Length - 3));
        Assert.Throws<HubProtocolException>(() => reader.Read<string>());
    }
}
