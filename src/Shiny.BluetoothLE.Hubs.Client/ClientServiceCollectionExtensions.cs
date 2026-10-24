using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;

namespace Shiny.BluetoothLE.Hubs;

public static class ClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers the generated proxy for <typeparamref name="TContract"/> as a singleton. Resolve it as
    /// IBleHubClient&lt;TContract&gt; (connection + Hub) or as TContract. It scans for and connects to
    /// <see cref="BleHubClientOptions.ServiceUuid"/>, which must match the host's. On Android, iOS and Mac Catalyst it adds
    /// the BLE stack (AddBluetoothLE) too; on plain .NET, register an IBleManager yourself.
    /// </summary>
    /// <param name="characteristicUuid">The hub's characteristic - the one the host added it with</param>
    /// <param name="configure">The service UUID, and the app-wide protocol limits</param>
    public static IServiceCollection AddBleHubClient<TContract>(
        this IServiceCollection services,
        string characteristicUuid,
        Action<BleHubClientOptions>? configure = null
    ) where TContract : class
    {
        var options = new BleHubClientOptions();
        configure?.Invoke(options);
        var serviceUuid = BleUuid.Normalize(options.ServiceUuid, nameof(options.ServiceUuid));
        var charUuid = BleUuid.Normalize(characteristicUuid, nameof(characteristicUuid));

#if IOS || MACCATALYST
        // the platform BLE central stack - a no-op when the app already added it. Hubs are a foreground thing, so iOS isn't
        // asked to alert the user about connections and notifications while the app is in the background (Shiny's
        // defaults). Call AddBluetoothLE(config) before this to use your own AppleBleConfiguration.
        services.AddBluetoothLE(new AppleBleConfiguration
        {
            NotifyOnConnection = false,
            NotifyOnDisconnection = false,
            NotifyOnNotification = false
        });
#elif ANDROID
        // the platform BLE central stack - a no-op when the app already added it
        services.AddBluetoothLE();
#endif
        services.AddBleHubCore();
        foreach (var c in options.ProtocolActions)
            services.ConfigureBleHubProtocol(c);

        services.TryAddSingleton(sp => new BleHubClientServices(
            sp.GetRequiredService<BleHubProtocolOptions>(),
            sp.GetRequiredService<IBleHubSerializer>(),
            sp.GetService<IBleManager>(),
            sp.GetService<ILoggerFactory>()
        )
        {
            LocalAdvertisement = sp.GetService<LocalHubAdvertisement>()
        });

        services.AddSingleton<IBleHubClient<TContract>>(sp => (IBleHubClient<TContract>)BleHubClientFactories.Create<TContract>(
            sp.GetRequiredService<BleHubClientServices>(),
            serviceUuid,
            charUuid
        ));
        services.AddSingleton(sp => (TContract)sp.GetRequiredService<IBleHubClient<TContract>>());
        return services;
    }
}


/// <summary>
/// How a hub client finds its host
/// </summary>
public sealed class BleHubClientOptions
{
    /// <summary>
    /// The service the host serves its hubs under - scanned for and connected to. Must match the host's.
    /// </summary>
    public string ServiceUuid { get; set; } = BleHubProtocolOptions.DefaultServiceUuid;

    internal List<Action<BleHubProtocolOptions>> ProtocolActions { get; } = new();

    /// <summary>
    /// Protocol limits (payload size, timeouts) - app-wide, shared with every other hub client and host in the app
    /// </summary>
    public BleHubClientOptions Protocol(Action<BleHubProtocolOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        this.ProtocolActions.Add(configure);
        return this;
    }
}
