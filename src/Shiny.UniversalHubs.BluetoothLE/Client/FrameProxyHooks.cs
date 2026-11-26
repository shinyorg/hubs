namespace Shiny.UniversalHubs;

/// <summary>
/// Drives a hub proxy over a caller supplied frame link - the BLE wire without a radio (tests, in-memory)
/// </summary>
internal static class FrameProxyHooks
{
    /// <summary>
    /// Connects over a caller supplied frame link. Frames from the host go to <see cref="ReceiveFrame"/>.
    /// </summary>
    public static Task ConnectTransport(
        this HubProxy proxy,
        Func<byte[], CancellationToken, Task> write,
        int mtu,
        HubHandshakeOptions? options,
        CancellationToken cancellationToken,
        Func<ushort, bool, CancellationToken, Task<Shiny.BluetoothLE.L2CapChannel>>? openL2Cap = null,
        Func<CancellationToken, Task<int>>? relink = null
    ) => proxy.ConnectExternal(
        events => new FrameClientTransport(proxy.Services.Options, new DelegateFrameLink(write, mtu, openL2Cap, relink), events, proxy.Logger),
        options,
        cancellationToken
    );


    static FrameClientTransport? Frames(HubProxy proxy) => proxy.ConnectionTransport as FrameClientTransport;

    public static void ReceiveFrame(this HubProxy proxy, ReadOnlySpan<byte> frame) => Frames(proxy)?.OnFrame(frame);

    /// <summary>
    /// Simulates the link dropping with no resume
    /// </summary>
    public static Task LoseConnection(this HubProxy proxy, string reason)
        => Frames(proxy)?.Lose(new HubDisconnect(HubDisconnectReason.ClientTimeout, reason)) ?? Task.CompletedTask;

    /// <summary>
    /// Simulates the link dropping the way the radio reports it - resumed when it can be
    /// </summary>
    public static void DropLink(this HubProxy proxy, string reason) => Frames(proxy)?.OnLinkLost(new HubDisconnect(HubDisconnectReason.ClientTimeout, reason));
}
