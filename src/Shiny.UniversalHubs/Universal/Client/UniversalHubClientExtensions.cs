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
        string characteristicUuid,
        string mdnsServiceType,
        Action<UniversalHubClientOptions>? configure = null
    ) where TContract : class
    {
        UniversalHubProtocol.ValidateServiceType(mdnsServiceType);

        return services.AddUniversalHubClient<TContract>(characteristicUuid, o =>
        {
            o.ServiceType = mdnsServiceType;
            configure?.Invoke(o);
        });
    }


    public static IServiceCollection AddUniversalHubClient<TContract>(
        this IServiceCollection services,
        string characteristicUuid,
        Action<UniversalHubClientOptions>? configure = null
    ) where TContract : class
    {
        var options = new UniversalHubClientOptions();
        configure?.Invoke(options);

        // blehubs adds the platform BLE stack (with iOS's background alerts off) on Android, iOS and Mac Catalyst
        services.AddBleHubClient<TContract>(characteristicUuid, o => o.ServiceUuid = options.ServiceUuid);
        services.AddHttpServerLocator();

        foreach (var c in options.ServiceActions)
            c(services);

        services.AddSingleton<IUniversalHubClient<TContract>>(sp => new UniversalHubClient<TContract>(
            sp.GetRequiredService<IBleHubClient<TContract>>(),
            // resolved on first discovery, so connecting to a known host needs no mDNS stack
            () => sp.GetService<IHttpServerLocator>(),
            options,
            sp.GetService<ILoggerFactory>(),
            () => options.Transports.Select(x => x(sp)).ToList()
        ));
        return services;
    }
}
