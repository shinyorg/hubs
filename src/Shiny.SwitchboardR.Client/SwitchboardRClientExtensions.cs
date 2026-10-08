using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.SwitchboardR;

public static class SwitchboardRClientExtensions
{
    /// <summary>
    /// Registers the generated proxy for <typeparamref name="TContract"/> (as AddBleHubClient does) plus Wi-Fi discovery
    /// and transport. Resolve <see cref="ISwitchboardRClient{TContract}"/>; IBleHubClient&lt;TContract&gt; and TContract
    /// resolve to the same connection. BLE still needs AddBluetoothLE.
    /// </summary>
    public static IServiceCollection AddSwitchboardRClient<TContract>(
        this IServiceCollection services,
        string serviceUuid,
        string characteristicUuid,
        Action<SwitchboardRClientOptions>? configure = null
    ) where TContract : class
    {
        services.AddBleHubClient<TContract>(serviceUuid, characteristicUuid);
        services.AddHttpServerLocator();

        var options = new SwitchboardRClientOptions();
        configure?.Invoke(options);

        services.AddSingleton<ISwitchboardRClient<TContract>>(sp => new SwitchboardRClient<TContract>(
            sp.GetRequiredService<IBleHubClient<TContract>>(),
            // resolved on first discovery, so connecting to a known host needs no mDNS stack
            () => sp.GetService<IHttpServerLocator>(),
            options,
            sp.GetService<ILoggerFactory>()
        ));
        return services;
    }
}
