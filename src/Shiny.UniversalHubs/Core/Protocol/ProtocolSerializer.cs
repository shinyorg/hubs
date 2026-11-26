using System.Text.Json;

namespace Shiny.UniversalHubs.Protocol;

/// <summary>
/// Protocol models are always JSON via an internal source generated context - independent of the app's serializer
/// </summary>
internal static class ProtocolSerializer
{
    public static byte[] Serialize(HandshakeInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, HubProtocolJsonContext.Default.HandshakeInfo);
    public static byte[] Serialize(HandshakeAck value) => JsonSerializer.SerializeToUtf8Bytes(value, HubProtocolJsonContext.Default.HandshakeAck);
    public static byte[] Serialize(RemoteError value) => JsonSerializer.SerializeToUtf8Bytes(value, HubProtocolJsonContext.Default.RemoteError);
    public static byte[] Serialize(DisconnectInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, HubProtocolJsonContext.Default.DisconnectInfo);
    public static byte[] Serialize(RenameInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, HubProtocolJsonContext.Default.RenameInfo);
    public static byte[] Serialize(ResumeInfo value) => JsonSerializer.SerializeToUtf8Bytes(value, HubProtocolJsonContext.Default.ResumeInfo);

    public static HandshakeInfo ReadHandshake(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, HubProtocolJsonContext.Default.HandshakeInfo)!;
    public static HandshakeAck ReadHandshakeAck(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, HubProtocolJsonContext.Default.HandshakeAck)!;
    public static ResumeInfo ReadResume(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, HubProtocolJsonContext.Default.ResumeInfo)!;
    public static RenameInfo ReadRename(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, HubProtocolJsonContext.Default.RenameInfo)!;
    public static RemoteError ReadError(ReadOnlyMemory<byte> data) => JsonSerializer.Deserialize(data.Span, HubProtocolJsonContext.Default.RemoteError)!;

    public static DisconnectInfo ReadDisconnect(ReadOnlyMemory<byte> data)
        => data.Length == 0 ? new DisconnectInfo(null) : JsonSerializer.Deserialize(data.Span, HubProtocolJsonContext.Default.DisconnectInfo)!;
}
