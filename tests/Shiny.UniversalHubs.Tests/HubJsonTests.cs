using System.Text.Json;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs.Tests;

public class HubJsonTests
{
    static HubJsonTests() => Json.AddContext(TestJsonContext.Default);

    readonly IBleHubSerializer serializer = new ShinyJsonBleHubSerializer();


    [Fact]
    public void ArgumentsRoundTripThroughJson()
    {
        var encoded = new BleHubArgumentWriter(this.serializer)
            .Write("text")
            .Write(42)
            .Write(new Payload("p", 7))
            .Write<string?>(null)
            .ToArray();

        var json = HubJson.ArgumentsToJson(encoded);
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(4, json.GetArrayLength());
        Assert.Equal("text", json[0].GetString());
        Assert.Equal(42, json[1].GetInt32());

        var reader = new BleHubArgumentReader(this.serializer, HubJson.ArgumentsFromJson(json)).Expect(4, "test");
        Assert.Equal("text", reader.Read<string>());
        Assert.Equal(42, reader.Read<int>());
        Assert.Equal(new Payload("p", 7), reader.Read<Payload>());
        Assert.Null(reader.Read<string?>());
    }


    [Fact]
    public void NoArguments()
    {
        var json = HubJson.ArgumentsToJson(BleHubArgumentWriter.Empty);
        Assert.Equal(0, json.GetArrayLength());
        Assert.Equal(BleHubArgumentWriter.Empty, HubJson.ArgumentsFromJson(json));
    }


    [Fact]
    public void ResultEnvelopes()
    {
        var value = this.serializer.Serialize(new Payload("r", 1));
        var read = HubJson.ReadResult(HubJson.Result(value, true));
        Assert.Equal(new Payload("r", 1), this.serializer.Deserialize<Payload>(read.Span));

        Assert.True(HubJson.ReadResult(HubJson.Result([], false)).IsEmpty);

        var ex = Assert.Throws<BleHubRemoteException>(() => HubJson.ReadResult(HubJson.Error("Kaboom", "it broke")));
        Assert.Equal("Kaboom", ex.RemoteErrorType);
        Assert.Equal("it broke", ex.Message);
    }


    [Fact]
    public void NonJsonSerializerIsExplained()
    {
        var encoded = new BleHubArgumentWriter(new BinarySerializer()).Write(5).ToArray();
        var ex = Assert.Throws<BleHubException>(() => HubJson.ArgumentsToJson(encoded));
        Assert.Contains("IBleHubSerializer", ex.Message);
    }


    sealed class BinarySerializer : IBleHubSerializer
    {
        public byte[] Serialize<T>(T value) => [0xFF, 0x00, 0x01];
        public T Deserialize<T>(ReadOnlySpan<byte> data) => default!;
    }
}
