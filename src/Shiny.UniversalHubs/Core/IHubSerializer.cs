namespace Shiny.UniversalHubs;

/// <summary>
/// Serializes contracts and results for the wire. Swap it out (MessagePack, protobuf, ...) by registering your own before AddBleHub / AddBleHubClient.
/// </summary>
public interface IHubSerializer
{
    byte[] Serialize<T>(T value);
    T Deserialize<T>(ReadOnlySpan<byte> data);
}


/// <summary>
/// Default serializer - uses Shiny's AOT friendly JSON serializer, so any [ShinyJsonContext] / Shiny.Json.AddContext registration
/// covers your contracts.
/// </summary>
public sealed class ShinyJsonBleHubSerializer(ISerializer? serializer = null) : IHubSerializer
{
    readonly ISerializer inner = serializer ?? Json.Default;

    public byte[] Serialize<T>(T value) => this.inner.SerializeToUtf8Bytes(value);
    public T Deserialize<T>(ReadOnlySpan<byte> data) => this.inner.Deserialize<T>(data);
}
