using System.ComponentModel;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Carries a hub client's calls over a transport other than BLE (Wi-Fi, ...). Created per connection by
/// <see cref="BleHubClient.ConnectExternal"/>; the generated proxy, events and file methods work unchanged on top of it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IBleHubClientTransport
{
    /// <summary>
    /// Opens the connection and runs the handshake. A refusal comes back as an ack with Accepted = false.
    /// </summary>
    Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="arguments"/> is the encoded argument list (see <see cref="BleHubArgumentWriter"/>). Returns the serialized
    /// result, empty for a method returning Task. A hub failure is thrown as <see cref="BleHubRemoteException"/>.
    /// </summary>
    Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Gives the client a new name on the host. A refusal is thrown as <see cref="BleHubRemoteException"/> of type
    /// <see cref="BleHubRemoteException.RenameRefused"/>.
    /// </summary>
    Task Rename(string? name, CancellationToken cancellationToken)
        => throw new NotSupportedException("This transport can't rename a client");

    /// <summary>
    /// Whether this connection can move files
    /// </summary>
    bool CanTransferFiles { get; }

    Task<L2CapTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);
    Task<L2CapTransferResult> Download(string remoteFileName, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// The app is leaving - tell the host and close. Not called when the transport reported the close itself.
    /// </summary>
    Task Close(CancellationToken cancellationToken);

    /// <summary>
    /// Whether this connection can open a media channel
    /// </summary>
    bool CanOpenMediaChannel => false;

    /// <summary>
    /// Opens this connection's media channel. BleHubClient serializes the calls, returns an open channel it already has
    /// instead of calling again, and closes the channel when the connection ends.
    /// </summary>
    Task<IBleHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken)
        => throw new NotSupportedException("This transport can't open a media channel");
}


/// <summary>
/// What a transport reports back to the hub client
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IBleHubClientTransportEvents
{
    /// <summary>
    /// A push from the host. Call in the order received - pushes are raised one at a time, in that order.
    /// </summary>
    void Pushed(string eventName, ReadOnlyMemory<byte> arguments);

    /// <summary>
    /// The host has a new name. Call in order with <see cref="Pushed"/>.
    /// </summary>
    void HostRenamed(string? hostName);

    /// <summary>
    /// The connection ended without the app asking (the host disconnected the client, or the link was lost), and why
    /// </summary>
    void Closed(HubDisconnect disconnect);
}
