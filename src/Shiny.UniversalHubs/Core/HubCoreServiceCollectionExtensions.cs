using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.UniversalHubs;

internal static class HubCoreServiceCollectionExtensions
{
    /// <summary>
    /// Adjusts the app-wide protocol limits - AddUniversalServer's Protocol and AddUniversalHubClient's Protocol call it
    /// </summary>
    public static IServiceCollection ConfigureHubProtocol(this IServiceCollection services, Action<HubProtocolOptions> configure)
    {
        services.AddHubCore();
        services.AddSingleton<IConfigureHubProtocol>(new ConfigureHubProtocol(configure));
        return services;
    }


    /// <summary>
    /// Registers the shared protocol services. Called by AddUniversalServer / AddUniversalHubClient.
    /// </summary>
    public static IServiceCollection AddHubCore(this IServiceCollection services)
    {
        services.TryAddSingleton(sp =>
        {
            var options = new HubProtocolOptions();
            foreach (var c in sp.GetServices<IConfigureHubProtocol>())
                c.Configure(options);
            return options;
        });
        services.TryAddSingleton<IHubSerializer>(sp => new JsonHubSerializer(sp.GetService<ISerializer>()));
        // a host and the clients in the same app share it - clients leave the host's own advertisement out of discovery
        return services;
    }
}


internal interface IConfigureHubProtocol
{
    void Configure(HubProtocolOptions options);
}

internal sealed class ConfigureHubProtocol(Action<HubProtocolOptions> action) : IConfigureHubProtocol
{
    public void Configure(HubProtocolOptions options) => action(options);
}
