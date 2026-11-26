using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.BluetoothLE.Hubs;

internal static class BleHubServiceCollectionExtensions
{
    /// <summary>
    /// Adjusts the app-wide protocol limits - AddBleHubServer's Protocol and AddBleHubClient's options call it
    /// </summary>
    public static IServiceCollection ConfigureBleHubProtocol(this IServiceCollection services, Action<BleHubProtocolOptions> configure)
    {
        services.AddBleHubCore();
        services.AddSingleton<IConfigureBleHubProtocol>(new ConfigureBleHubProtocol(configure));
        return services;
    }


    /// <summary>
    /// Registers the shared protocol services. Called by AddBleHubServer / AddBleHubClient.
    /// </summary>
    public static IServiceCollection AddBleHubCore(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var options = new BleHubProtocolOptions();
            foreach (var c in sp.GetServices<IConfigureBleHubProtocol>())
                c.Configure(options);
            return options;
        });
        services.TryAddSingleton<IBleHubSerializer>(sp => new ShinyJsonBleHubSerializer(sp.GetService<ISerializer>()));
        // a host and the clients in the same app share it - clients leave the host's own advertisement out of discovery
        services.TryAddSingleton<LocalHubAdvertisement>();
        return services;
    }
}


internal interface IConfigureBleHubProtocol
{
    void Configure(BleHubProtocolOptions options);
}

internal sealed class ConfigureBleHubProtocol(Action<BleHubProtocolOptions> action) : IConfigureBleHubProtocol
{
    public void Configure(BleHubProtocolOptions options) => action(options);
}
