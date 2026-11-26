namespace Shiny.UniversalHubs;

/// <summary>
/// Serializes contracts and results for the wire. Every hub uses the app's (registered before AddUniversalServer /
/// AddUniversalHubClient - <see cref="JsonHubSerializer"/> unless you register another), or its own (the hub's and the
/// client's <c>Serializer(...)</c>). Host and client must use the same one: its <see cref="Name"/> travels in the
/// handshake, and a hub refuses a client whose serializer has another name.
/// </summary>
public interface IHubSerializer
{
    /// <summary>
    /// Identifies the format on the wire - eg. "json", "msgpack". Two serializers with the same name must read each other.
    /// </summary>
    string Name { get; }

    byte[] Serialize<T>(T value);
    T Deserialize<T>(ReadOnlySpan<byte> data);
}


/// <summary>
/// Default serializer - uses Shiny's AOT friendly JSON serializer, so any [ShinyJsonContext] / Shiny.Json.AddContext registration
/// covers your contracts.
/// </summary>
public sealed class JsonHubSerializer(ISerializer? serializer = null) : IHubSerializer
{
    public const string FormatName = "json";

    readonly ISerializer inner = serializer ?? Json.Default;

    public string Name => FormatName;

    public byte[] Serialize<T>(T value) => this.inner.SerializeToUtf8Bytes(value);
    public T Deserialize<T>(ReadOnlySpan<byte> data) => this.inner.Deserialize<T>(data);
}
