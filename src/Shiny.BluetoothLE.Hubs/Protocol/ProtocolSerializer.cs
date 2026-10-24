using System.Text.Json;

namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// Protocol models are always JSON via an internal source generated context - independent of the app's serializer
/// </summary>
internal static class ProtocolSerializer
{
    public static byte[] Serialize(HandshakeInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, BleHubProtocolJsonContext.Default.HandshakeInfo);
    public static byte[] Serialize(HandshakeAck value) => JsonSerializer.SerializeToUtf8Bytes(value, BleHubProtocolJsonContext.Default.HandshakeAck);
    public static byte[] Serialize(RemoteError value) => JsonSerializer.SerializeToUtf8Bytes(value, BleHubProtocolJsonContext.Default.RemoteError);
    public static byte[] Serialize(DisconnectInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, BleHubProtocolJsonContext.Default.DisconnectInfo);
    public static byte[] Serialize(RenameInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, BleHubProtocolJsonContext.Default.RenameInfo);

    public static HandshakeInfo ReadHandshake(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, BleHubProtocolJsonContext.Default.HandshakeInfo)!;
    public static HandshakeAck ReadHandshakeAck(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, BleHubProtocolJsonContext.Default.HandshakeAck)!;
    public static RenameInfo ReadRename(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, BleHubProtocolJsonContext.Default.RenameInfo)!;
    public static RemoteError ReadError(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, BleHubProtocolJsonContext.Default.RemoteError)!;

    public static DisconnectInfo ReadDisconnect(ReadOnlyMemory<byte> data)
        => data.Length == 0 ? new DisconnectInfo(null) : JsonSerializer.Deserialize(data.Span, BleHubProtocolJsonContext.Default.DisconnectInfo)!;
}
