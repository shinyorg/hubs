namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// An ordered, bidirectional packet pipe beside one hub connection, for live media (audio and video calls). It skips the
/// hub's message framing and serializer: you send and receive raw packets. Over BLE it is an L2CAP channel of its own;
/// another transport (Wi-Fi) can carry it too.
///
/// Packets arrive whole and in the order they were sent, or the channel closes - there is no partial or reordered
/// delivery. It closes when either side disposes it, when its link fails, and when the hub connection it belongs to ends.
/// </summary>
public interface IBleHubMediaChannel : IAsyncDisposable
{
    /// <summary>
    /// The hub connection this channel belongs to, on the host. Null on the client.
    /// </summary>
    string? ConnectionId { get; }

    /// <summary>
    /// The largest packet either side may send. The host sets it (BleHubHostOptions.EnableMediaChannels).
    /// </summary>
    int MaxPacketSize { get; }

    /// <summary>
    /// False once the channel has closed
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Sends one packet. Calls are serialized, so packets go out whole and in call order. Throws
    /// <see cref="ArgumentException"/> for a packet over <see cref="MaxPacketSize"/>, and <see cref="BleHubException"/>
    /// when the channel is closed. Cancelling a send that has started writing closes the channel, since the far side
    /// can't recover from half a packet.
    /// </summary>
    ValueTask Send(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);

    /// <summary>
    /// The packets from the far side, in order. One reader at a time (a second concurrent reader throws
    /// <see cref="InvalidOperationException"/>). Completes when the channel closes. Packets that arrive before anyone reads
    /// are buffered.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> Receive(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes (never faults) when the channel closes, for whatever reason
    /// </summary>
    Task Completion { get; }
}
