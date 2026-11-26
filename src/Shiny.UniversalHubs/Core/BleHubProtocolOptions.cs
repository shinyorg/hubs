namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Protocol limits, app-wide for hosts and clients alike. Set them with AddBleHubServer's Protocol or
/// AddBleHubClient's options.
/// </summary>
public class BleHubProtocolOptions
{
    /// <summary>
    /// The service UUID hosts serve their hubs under, and clients scan for, unless they set their own (the host's and the
    /// client's ServiceUuid). Every hub on a host is a characteristic inside this one service: two 128-bit UUIDs don't fit
    /// in a BLE advertisement. Set your own on both sides to keep other apps' hosts out of your scans.
    /// </summary>
    public const string DefaultServiceUuid = "98ac0390-867f-4c0d-b9f5-4266bdeac29b";

    /// <summary>
    /// Largest payload accepted for a single message. Bigger content belongs in a file transfer.
    /// </summary>
    public int MaxPayloadSize { get; set; } = 256 * 1024;

    /// <summary>
    /// How long a partially received message is kept before it is dropped
    /// </summary>
    public TimeSpan ReassemblyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default timeout for a hub invocation (streams are not timed out)
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Partial messages allowed in flight per peer
    /// </summary>
    public int MaxPartialMessages { get; set; } = 16;
}
