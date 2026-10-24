namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// The kind of logical message a frame belongs to. Values are part of the wire protocol - never renumber.
/// </summary>
public enum FrameKind : byte
{
    // client -> host
    Handshake = 0x01,
    Invoke = 0x10,
    StreamInvoke = 0x12,
    Cancel = 0x13,
    Rename = 0x14,
    // Disconnect (0x40) is also sent client -> host when the client leaves

    // host -> client
    HandshakeAck = 0x02,
    Completion = 0x21,
    StreamItem = 0x22,
    StreamEnd = 0x23,
    Error = 0x24,
    Push = 0x30,
    HostRenamed = 0x31,
    Disconnect = 0x40
}
