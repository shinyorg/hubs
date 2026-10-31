namespace Shiny.UniversalHubs;

/// <summary>
/// This device's Multipeer radio. The Apple implementation wraps MultipeerConnectivity; tests use an in-memory one. The
/// transports above it never see an MC type, so everything except the radio runs (and is tested) on plain .NET.
/// </summary>
internal interface IMultipeerLink
{
    /// <summary>
    /// False where there is no MultipeerConnectivity (Android, Windows, Linux)
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Advertises this device as <paramref name="displayName"/> under <paramref name="serviceType"/> and accepts every
    /// invitation into a session of its own, handed to <paramref name="accepted"/>. Dispose to stop advertising; sessions
    /// already accepted carry on.
    /// </summary>
    IDisposable Advertise(string serviceType, string displayName, IReadOnlyDictionary<string, string> info, Action<IPeerConnection> accepted);

    /// <summary>
    /// Peers advertising <paramref name="serviceType"/> as they appear and go, browsing as <paramref name="displayName"/>.
    /// Dispose the subscription to stop browsing.
    /// </summary>
    IObservable<MultipeerSighting> Browse(string serviceType, string displayName);

    /// <summary>
    /// Invites <paramref name="peer"/> (a <see cref="MultipeerSighting.Peer"/>) into a new session and waits until it is
    /// connected. The invitation comes from the browser that saw the peer, under that browser's display name.
    /// </summary>
    Task<IPeerConnection> Connect(object peer, TimeSpan timeout, CancellationToken cancellationToken);
}


/// <summary>
/// A peer seen while browsing
/// </summary>
/// <param name="Peer">The native peer - pass it back to <see cref="IMultipeerLink.Connect"/></param>
/// <param name="DisplayName">Its display name</param>
/// <param name="Info">What it advertises; null when it has gone</param>
internal sealed record MultipeerSighting(object Peer, string DisplayName, IReadOnlyDictionary<string, string>? Info);



/// <summary>
/// Where there is no MultipeerConnectivity
/// </summary>
internal sealed class UnsupportedMultipeerLink : IMultipeerLink
{
    public bool IsSupported => false;

    public IDisposable Advertise(string serviceType, string displayName, IReadOnlyDictionary<string, string> info, Action<IPeerConnection> accepted)
        => throw new PlatformNotSupportedException("MultipeerConnectivity is only available on iOS, Mac Catalyst and macOS");

    public IObservable<MultipeerSighting> Browse(string serviceType, string displayName)
        => throw new PlatformNotSupportedException("MultipeerConnectivity is only available on iOS, Mac Catalyst and macOS");

    public Task<IPeerConnection> Connect(object peer, TimeSpan timeout, CancellationToken cancellationToken)
        => throw new PlatformNotSupportedException("MultipeerConnectivity is only available on iOS, Mac Catalyst and macOS");
}
