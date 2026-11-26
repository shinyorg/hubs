using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.UniversalHubs;

public static class UniversalHubClientExtensions
{
    /// <summary>
    /// Registers the generated proxy for <typeparamref name="TContract"/> as a client of the hub named
    /// <paramref name="hubName"/>, over the transports <paramref name="configure"/> picks (at least one). Resolve
    /// <see cref="IUniversalHubClient{TContract}"/>; IBleHubClient&lt;TContract&gt; and TContract resolve to the same
    /// connection.
    /// <code>
    /// services.AddUniversalHubClient&lt;IGameHub&gt;("game", o => o
    ///     .UseBluetoothLE(ServiceUuid, GameCharacteristicUuid)
    ///     .UseHttp("_game._tcp"));
    /// </code>
    /// </summary>
    /// <param name="hubName">The name the host added the hub under</param>
    public static IServiceCollection AddUniversalHubClient<TContract>(
        this IServiceCollection services,
        string hubName,
        Action<UniversalHubClientOptions> configure
    ) where TContract : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        UniversalServerExtensions.ValidateHubName(hubName);

        var options = new UniversalHubClientOptions();
        configure(options);
        if (options.Enabled == HubTransports.None)
            throw new InvalidOperationException($"The {hubName} client isn't set up with any transport - call UseBluetoothLE, UseHttp, UseMultipeer or UseNearby");

        services.AddHubClientCore();
        foreach (var c in options.ProtocolActions)
            services.ConfigureBleHubProtocol(c);

        foreach (var c in options.ServiceActions)
            c(services);

        services.AddSingleton<IBleHubClient<TContract>>(sp => (IBleHubClient<TContract>)BleHubClientFactories.Create<TContract>(
            sp.GetRequiredService<BleHubClientServices>(),
            hubName
        ));
        services.AddSingleton(sp => (TContract)sp.GetRequiredService<IBleHubClient<TContract>>());
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
