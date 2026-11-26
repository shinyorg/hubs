namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// A fully reassembled logical message
/// </summary>
/// <param name="Kind">What the message is</param>
/// <param name="MessageId">Correlation id - client allocated ids are 1..0x7FFF, host allocated ids have the high bit set</param>
/// <param name="Name">The hub method or push event name, null for kinds that do not carry one</param>
/// <param name="Payload">The serialized payload (may be empty)</param>
public sealed record BleHubMessage(
    FrameKind Kind,
    ushort MessageId,
    string? Name,
    ReadOnlyMemory<byte> Payload
);
