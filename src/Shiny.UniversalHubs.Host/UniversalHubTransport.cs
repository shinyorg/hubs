using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// A transport beyond BLE and HTTP that hubs can also be served over (Shiny.UniversalHubs.Multipeer). It reaches each hub
/// through <see cref="IHubBinding.Endpoint"/> - the blehubs transport seam - so its clients share Clients, Groups,
/// MaxClients and ValidateClient with everyone else. <see cref="UniversalHubHost"/> starts and stops it with the others.
/// </summary>
internal interface IUniversalHubTransport
{
    /// <summary>
    /// The one flag this transport answers to
    /// </summary>
    HubTransports Transport { get; }

    /// <summary>
    /// Whether <paramref name="hub"/> is accepting clients over this transport
    /// </summary>
    bool IsRunning(IHubBinding hub);

    /// <summary>
    /// Starts accepting clients of <paramref name="hubs"/>. Throws when the transport can't run here (no radio, wrong
    /// platform) - the host skips it as long as another transport starts.
    /// </summary>
    Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken cancellationToken);

    /// <summary>
    /// Stops <paramref name="hubs"/> and disconnects their clients on this transport, telling each one why
    /// </summary>
    Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect);
}
