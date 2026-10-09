using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.UniversalHubs;

public static class UniversalHubClientExtensions
{
    /// <summary>
    /// Registers the generated proxy for <typeparamref name="TContract"/> (as AddBleHubClient does) plus Wi-Fi discovery
    /// and transport. Resolve <see cref="IUniversalHubClient{TContract}"/>; IBleHubClient&lt;TContract&gt; and TContract
    /// resolve to the same connection. On Android, iOS and Mac Catalyst it adds the BLE stack (AddBluetoothLE) too; on plain
    /// .NET, register an IBleManager yourself.
    /// </summary>
    /// <summary>
    /// The client side of a universal hub (see AddUniversalServer): discovers hosts over BLE and mDNS and connects over
    /// either. <paramref name="mdnsServiceType"/> must match the one the hub was added with. Resolve <see cref="IUniversalHubClient{TContract}"/>.
    /// On Android, iOS and Mac Catalyst it adds the BLE stack (AddBluetoothLE) too.
    /// </summary>
    public static IServiceCollection AddUniversalHubClient<TContract>(
        this IServiceCollection services,
        string serviceUuid,
        string characteristicUuid,
        string mdnsServiceType,
        Action<UniversalHubClientOptions>? configure = null
    ) where TContract : class
    {
        UniversalHubProtocol.ValidateServiceType(mdnsServiceType);

        return services.AddUniversalHubClient<TContract>(serviceUuid, characteristicUuid, o =>
        {
            o.ServiceType = mdnsServiceType;
            configure?.Invoke(o);
        });
    }


    public static IServiceCollection AddUniversalHubClient<TContract>(
        this IServiceCollection services,
        string serviceUuid,
        string characteristicUuid,
        Action<UniversalHubClientOptions>? configure = null
    ) where TContract : class
    {
#if ANDROID || IOS || MACCATALYST
        // the platform BLE central stack - a no-op when the app already added it. On Apple, call AddBluetoothLE(config)
        // before this to use your own AppleBleConfiguration.
        services.AddBluetoothLE();
#endif
        services.AddBleHubClient<TContract>(serviceUuid, characteristicUuid);
        services.AddHttpServerLocator();

        var options = new UniversalHubClientOptions();
        configure?.Invoke(options);

        services.AddSingleton<IUniversalHubClient<TContract>>(sp => new UniversalHubClient<TContract>(
            sp.GetRequiredService<IBleHubClient<TContract>>(),
            // resolved on first discovery, so connecting to a known host needs no mDNS stack
            () => sp.GetService<IHttpServerLocator>(),
            options,
            sp.GetService<ILoggerFactory>()
        ));
        return services;
    }
}
