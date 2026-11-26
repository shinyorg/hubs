using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.UniversalHubs;

internal static class HubCoreServiceCollectionExtensions
{
    /// <summary>
    /// Adjusts the app-wide protocol limits - AddUniversalServer's Protocol and AddBleHubClient's options call it
    /// </summary>
    public static IServiceCollection ConfigureHubProtocol(this IServiceCollection services, Action<HubProtocolOptions> configure)
    {
        services.AddHubCore();
        services.AddSingleton<IConfigureBleHubProtocol>(new ConfigureHubProtocol(configure));
        return services;
    }


    /// <summary>
    /// Registers the shared protocol services. Called by AddUniversalServer / AddBleHubClient.
    /// </summary>
    public static IServiceCollection AddHubCore(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var options = new HubProtocolOptions();
            foreach (var c in sp.GetServices<IConfigureBleHubProtocol>())
                c.Configure(options);
            return options;
        });
        services.TryAddSingleton<IHubSerializer>(sp => new JsonHubSerializer(sp.GetService<ISerializer>()));
        // a host and the clients in the same app share it - clients leave the host's own advertisement out of discovery
        return services;
    }
}


internal interface IConfigureBleHubProtocol
{
    void Configure(HubProtocolOptions options);
}

internal sealed class ConfigureHubProtocol(Action<HubProtocolOptions> action) : IConfigureBleHubProtocol
{
    public void Configure(HubProtocolOptions options) => action(options);
}
