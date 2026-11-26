namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Base for all BLE hub failures
/// </summary>
public class BleHubException : Exception
{
    public BleHubException(string message) : base(message) { }
    public BleHubException(string message, Exception? inner) : base(message, inner) { }
}


/// <summary>
/// A malformed or unexpected frame was received
/// </summary>
public class BleHubProtocolException(string message, ushort? messageId = null) : BleHubException(message)
{
    public ushort? MessageId { get; } = messageId;
}


/// <summary>
/// The remote handler threw - the original exception type and message are carried across
/// </summary>
public class BleHubRemoteException(string remoteErrorType, string message) : BleHubException(message)
{
    /// <summary>
    /// The <see cref="RemoteErrorType"/> of a rename the host refused (ValidateClient or OnRenamedAsync) - the message says why
    /// </summary>
    public const string RenameRefused = "HubRenameRefused";

    public string RemoteErrorType { get; } = remoteErrorType;

    /// <summary>
    /// The error type a caller is told for a hub method's exception: its class name, or - for a BleHubRemoteException thrown
    /// by the hub, which is how a hub relaying someone else's error keeps that error's type - its <see cref="RemoteErrorType"/>
    /// </summary>
    public static string TypeOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is BleHubRemoteException remote ? remote.RemoteErrorType : exception.GetType().Name;
    }
}


/// <summary>
/// The connection dropped (or was ended by the host) while a call was in flight
/// </summary>
public class BleHubDisconnectedException(string message, HubDisconnect? disconnect = null) : BleHubException(message)
{
    /// <summary>
    /// Why the connection ended, when it had been connected
    /// </summary>
    public HubDisconnect? Disconnect { get; } = disconnect;

    /// <summary>
    /// <see cref="HubDisconnect.Description"/>
    /// </summary>
    public string? Reason => this.Disconnect?.Description;
}


/// <summary>
/// The connected host does not serve file transfers (disabled, or L2CAP is unavailable on its platform)
/// </summary>
public class BleHubFileTransferNotSupportedException(string message) : BleHubException(message);
