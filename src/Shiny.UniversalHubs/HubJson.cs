using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// Converts between the hub's encoded values and the JSON a Switchboard carries. Hub arguments, results, stream items and
/// pushes are serialized by IBleHubSerializer exactly as over BLE - here they are only re-framed, never re-serialized, so
/// the serializer must produce JSON (the default does).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HubJson
{
    /// <summary>
    /// <c>[count:1]</c> then <c>count x ([length:4][value])</c> (BleHubArgumentWriter's format) → a JSON array of the values
    /// </summary>
    public static JsonElement ArgumentsToJson(ReadOnlySpan<byte> encoded)
    {
        var buffer = new ArrayBufferWriter<byte>(encoded.Length + 16);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            if (encoded.Length > 0)
            {
                var count = encoded[0];
                var offset = 1;
                for (var i = 0; i < count; i++)
                {
                    if (offset + 4 > encoded.Length)
                        throw new BleHubProtocolException("Argument header is truncated");

                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded[offset..]);
                    offset += 4;
                    if (length < 0 || offset + length > encoded.Length)
                        throw new BleHubProtocolException("Argument value is truncated");

                    WriteRaw(writer, encoded.Slice(offset, length));
                    offset += length;
                }
            }
            writer.WriteEndArray();
        }
        return Parse(buffer.WrittenSpan);
    }


    /// <summary>
    /// A JSON array of values → BleHubArgumentWriter's format
    /// </summary>
    public static byte[] ArgumentsFromJson(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new BleHubProtocolException("Arguments must be a JSON array");

        var count = array.GetArrayLength();
        if (count > byte.MaxValue)
            throw new BleHubProtocolException($"A hub call can have at most {byte.MaxValue} arguments");

        var values = new List<ReadOnlyMemory<byte>>(count);
        foreach (var item in array.EnumerateArray())
            values.Add(Raw(item));

        var buffer = new byte[1 + values.Sum(x => 4 + x.Length)];
        buffer[0] = (byte)count;
        var offset = 1;
        foreach (var value in values)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)value.Length);
            offset += 4;
            value.Span.CopyTo(buffer.AsSpan(offset));
            offset += value.Length;
        }
        return buffer;
    }


    /// <summary>
    /// A successful call: <c>{"result": value}</c>, or <c>{}</c> for a method returning Task
    /// </summary>
    public static JsonElement Result(ReadOnlySpan<byte> serialized, bool hasValue)
    {
        var buffer = new ArrayBufferWriter<byte>(serialized.Length + 16);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (hasValue)
            {
                writer.WritePropertyName("result");
                WriteRaw(writer, serialized);
            }
            writer.WriteEndObject();
        }
        return Parse(buffer.WrittenSpan);
    }


    /// <summary>
    /// A stream item: <c>{"item": value}</c>
    /// </summary>
    public static JsonElement Item(ReadOnlySpan<byte> serialized)
    {
        var buffer = new ArrayBufferWriter<byte>(serialized.Length + 16);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("item");
            WriteRaw(writer, serialized);
            writer.WriteEndObject();
        }
        return Parse(buffer.WrittenSpan);
    }


    /// <summary>
    /// A hub failure: <c>{"error": {"type": "...", "message": "..."}}</c> - the same type and message BLE relays
    /// </summary>
    public static JsonElement Error(string type, string message)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("type", type);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Parse(buffer.WrittenSpan);
    }


    /// <summary>
    /// Reads a call envelope: the serialized result (empty for a method returning Task), or throws the hub's error
    /// </summary>
    public static ReadOnlyMemory<byte> ReadResult(JsonElement envelope)
    {
        ThrowIfError(envelope);
        return envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("result", out var result)
            ? Raw(result)
            : ReadOnlyMemory<byte>.Empty;
    }


    /// <summary>
    /// Reads a stream item envelope, or throws the hub's error
    /// </summary>
    public static ReadOnlyMemory<byte> ReadItem(JsonElement envelope)
    {
        ThrowIfError(envelope);
        return envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("item", out var item)
            ? Raw(item)
            : throw new BleHubProtocolException("Stream item envelope has no item");
    }


    internal static JsonElement Serialize(HubHandshake value)
        => JsonSerializer.SerializeToElement(value, UniversalHubJsonContext.Default.HubHandshake);

    internal static HubHandshake ReadHandshake(JsonElement value)
        => value.Deserialize(UniversalHubJsonContext.Default.HubHandshake) ?? throw new BleHubProtocolException("Empty handshake");

    internal static JsonElement Serialize(HubHandshakeReply value)
        => JsonSerializer.SerializeToElement(value, UniversalHubJsonContext.Default.HubHandshakeReply);

    internal static HubHandshakeReply ReadHandshakeReply(JsonElement value)
        => value.Deserialize(UniversalHubJsonContext.Default.HubHandshakeReply) ?? throw new BleHubProtocolException("Empty handshake reply");


    /// <summary>
    /// The Switchboard hang-up message carrying <paramref name="disconnect"/>
    /// </summary>
    public static string HangUpMessage(HubDisconnect disconnect)
        => JsonSerializer.Serialize(new HubHangUp((int)disconnect.Reason, disconnect.Message), UniversalHubJsonContext.Default.HubHangUp);

    /// <summary>
    /// Reads a hang-up message written by <see cref="HangUpMessage"/>; null when it is anything else
    /// </summary>
    public static HubDisconnect? ReadHangUp(string? message)
    {
        if (String.IsNullOrEmpty(message) || message[0] != '{')
            return null;

        try
        {
            var hangUp = JsonSerializer.Deserialize(message, UniversalHubJsonContext.Default.HubHangUp);
            return hangUp == null || !Enum.IsDefined((HubDisconnectReason)hangUp.Reason)
                ? null
                : new HubDisconnect((HubDisconnectReason)hangUp.Reason, hangUp.Message);
        }
        catch (JsonException)
        {
            return null;
        }
    }


    static void ThrowIfError(JsonElement envelope)
    {
        if (envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("error", out var error))
        {
            var type = error.TryGetProperty("type", out var t) ? t.GetString() : null;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new BleHubRemoteException(type ?? "Error", message ?? "The hub reported an error");
        }
    }


    static void WriteRaw(Utf8JsonWriter writer, ReadOnlySpan<byte> json)
    {
        try
        {
            writer.WriteRawValue(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new BleHubException(
                "UniversalHubs carries hub values as JSON, but the registered IBleHubSerializer produced something else. " +
                "Use the default serializer (or another JSON one) when hubs are served over Wi-Fi.",
                ex
            );
        }
    }


    static ReadOnlyMemory<byte> Raw(JsonElement element) => JsonMarshal.GetRawUtf8Value(element).ToArray();


    static JsonElement Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        return JsonElement.ParseValue(ref reader);
    }
}
