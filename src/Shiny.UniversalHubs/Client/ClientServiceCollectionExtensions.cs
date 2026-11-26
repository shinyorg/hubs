using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shiny.BluetoothLE.Hubs;

internal static class ClientServiceCollectionExtensions
{
    /// <summary>
    /// What every hub client needs - the protocol, the serializer and the services proxies are created with - without a
    /// proxy or a transport. AddUniversalHubClient calls it; so does a client created at runtime (dynamic hub clients).
    /// </summary>
    public static IServiceCollection AddHubClientCore(this IServiceCollection services)
    {
        services.AddBleHubCore();
        services.TryAddSingleton(sp => new BleHubClientServices(
            sp.GetRequiredService<BleHubProtocolOptions>(),
            sp.GetRequiredService<IBleHubSerializer>(),
            sp.GetService<ILoggerFactory>()
        ));
        return services;
    }
}
