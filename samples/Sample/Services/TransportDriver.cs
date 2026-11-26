using Shiny.UniversalHubs;
using Sample.Game;
using Sample.Hub;

namespace Sample.Services;

/// <summary>
/// A transport as the sample shows it: its host and client sides, and whether this platform has it at all
/// </summary>
public sealed record SampleTransport(string Key, string Label, string Icon, Type HostType, Type ClientType, bool IsSupported)
{
    public string Title => $"{this.Icon} {this.Label}";
}


/// <summary>
/// Turns transports on and off - for this device's hubs (what others reach it over) and for its hub clients (what it
/// reaches others over) - so each one can be tested on its own. Remembered across launches. Built on the library's
/// IUniversalHubHost.Start&lt;T&gt; / Stop&lt;T&gt; and IUniversalHubClient.SetEnabled.
/// </summary>
[Singleton]
public class TransportDriver(IUniversalHubHost host, IUniversalHubClient<IGameHub> game, IUniversalHubClient<IChatHub> chat)
{
    /// <summary>
    /// Every transport the sample knows - Multipeer is Apple only, Nearby is Android, iOS and macOS
    /// </summary>
    public static IReadOnlyList<SampleTransport> Known { get; } =
    [
        new("http", "Wi-Fi", "📶", typeof(HttpHostTransport), typeof(HttpClientTransport), true),
        new("multipeer", "Multipeer", "📡", typeof(MultipeerHostTransport), typeof(MultipeerClientTransport),
            OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS()),
        new("nearby", "Nearby", "🛰️", typeof(NearbyHostTransport), typeof(NearbyClientTransport),
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacOS()),
        // the macOS app registers Shiny's macOS BLE stacks itself
        new("ble", "Bluetooth", "🔵", typeof(BleHostTransport), typeof(BleClientTransport),
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
    ];

    /// <summary>
    /// The transports this device has
    /// </summary>
    public IReadOnlyList<SampleTransport> Available { get; } = Known.Where(x => x.IsSupported).ToList();

    /// <summary>
    /// Something was turned on or off
    /// </summary>
    public event EventHandler? Changed;


    public bool IsHosting(SampleTransport transport) => Preferences.Get(HostKey(transport), true);
    public bool IsConnecting(SampleTransport transport) => Preferences.Get(ClientKey(transport), true);

    /// <summary>
    /// The host transports this device's hubs are served over - what ChatService and GameSession start them on
    /// </summary>
    public IReadOnlyList<Type> HostTypes => this.Available.Where(this.IsHosting).Select(x => x.HostType).ToList();


    /// <summary>
    /// Applies the remembered client choices - once, at startup
    /// </summary>
    public async Task ApplyClients()
    {
        foreach (var transport in this.Available)
            await this.ApplyClient(transport, this.IsConnecting(transport));
    }


    /// <summary>
    /// Serves this device's running hubs over <paramref name="transport"/>, or stops serving them over it. At least one
    /// host transport stays on.
    /// </summary>
    public async Task SetHosting(SampleTransport transport, bool on)
    {
        if (!on && this.HostTypes.SequenceEqual([transport.HostType]))
            throw new InvalidOperationException("Keep at least one way in");

        Preferences.Set(HostKey(transport), on);
        await this.ApplyHost(transport, on);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>
    /// Lets the hub clients find and connect over <paramref name="transport"/>, or not. Turning off the one a connection is
    /// on disconnects it.
    /// </summary>
    public async Task SetConnecting(SampleTransport transport, bool on)
    {
        Preferences.Set(ClientKey(transport), on);
        await this.ApplyClient(transport, on);
        this.Changed?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>
    /// <paramref name="transport"/> alone, hosting and connecting - every other one off
    /// </summary>
    public async Task Solo(SampleTransport transport)
    {
        await this.SetHosting(transport, true);
        await this.SetConnecting(transport, true);
        foreach (var other in this.Available.Where(x => x != transport))
        {
            await this.SetHosting(other, false);
            await this.SetConnecting(other, false);
        }
    }


    /// <summary>
    /// Every transport this device has, hosting and connecting
    /// </summary>
    public async Task All()
    {
        foreach (var transport in this.Available)
        {
            await this.SetHosting(transport, true);
            await this.SetConnecting(transport, true);
        }
    }


    /// <summary>
    /// Whether this device's hubs are actually being served over <paramref name="transport"/> right now
    /// </summary>
    public bool IsRunning(SampleTransport transport) => host.RunningTransports.Any(x => x.GetType() == transport.HostType);


    /// <summary>
    /// How the sample shows a transport, from a host transport, a client transport or either type
    /// </summary>
    public static string Describe(object? transport)
    {
        var type = transport as Type ?? transport?.GetType();
        var known = Known.FirstOrDefault(x => x.HostType == type || x.ClientType == type);
        return known?.Title ?? (transport as IHubTransport)?.DisplayName ?? (transport as IHubClientTransport)?.DisplayName ?? "";
    }

    public static string Describe(IEnumerable<object> transports) => String.Join(" + ", transports.Select(Describe));


    async Task ApplyHost(SampleTransport transport, bool on)
    {
        if (on)
        {
            // every hub running somewhere starts on it too
            if (host.GetRunningTransports<ChatHub>().Count > 0)
                await host.StartHub<ChatHub>([transport.HostType]);
            if (host.GetRunningTransports<GameHub>().Count > 0)
                await host.StartHub<GameHub>([transport.HostType]);
        }
        else
        {
            await host.Stop([transport.HostType], "Turned off on the host");
        }
    }


    async Task ApplyClient(SampleTransport transport, bool on)
    {
        await game.SetEnabled(transport.ClientType, on);
        await chat.SetEnabled(transport.ClientType, on);
    }


    static string HostKey(SampleTransport transport) => "transport.host." + transport.Key;
    static string ClientKey(SampleTransport transport) => "transport.client." + transport.Key;
}
