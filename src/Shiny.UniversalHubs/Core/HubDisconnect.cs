namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Why a hub connection ended. Values travel on the wire - never renumber.
/// </summary>
public enum HubDisconnectReason
{
    /// <summary>
    /// The client left on purpose (it called Disconnect)
    /// </summary>
    ClientDisconnect = 0,

    /// <summary>
    /// The link went quiet and was given up on - out of range, the app was killed, the network dropped
    /// </summary>
    ClientTimeout = 1,

    /// <summary>
    /// The host ended this one client's session (Context.Abort, IHubContext.Disconnect)
    /// </summary>
    ServerDisconnect = 2,

    /// <summary>
    /// The host stopped the hub, or stopped hosting altogether
    /// </summary>
    ServerShutdown = 3,

    /// <summary>
    /// The transport failed (a write or the connection itself errored)
    /// </summary>
    ConnectionFailed = 4
}


/// <summary>
/// How a hub connection ended
/// </summary>
/// <param name="Reason">Why, as a value you can switch on</param>
/// <param name="Message">Optional detail - the text passed to Context.Abort, Stop or Disconnect</param>
public sealed record HubDisconnect(HubDisconnectReason Reason, string? Message = null)
{
    /// <summary>
    /// <see cref="Message"/>, or a short description of <see cref="Reason"/> when there isn't one
    /// </summary>
    public string Description => this.Message ?? Describe(this.Reason);

    public override string ToString() => this.Message == null ? this.Reason.ToString() : $"{this.Reason}: {this.Message}";

    public static string Describe(HubDisconnectReason reason) => reason switch
    {
        HubDisconnectReason.ClientDisconnect => "Disconnected",
        HubDisconnectReason.ClientTimeout => "Connection lost",
        HubDisconnectReason.ServerDisconnect => "Disconnected by host",
        HubDisconnectReason.ServerShutdown => "Host stopped",
        HubDisconnectReason.ConnectionFailed => "Connection failed",
        _ => reason.ToString()
    };
}
