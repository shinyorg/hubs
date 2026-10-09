using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Switchboard;

namespace Shiny.UniversalHubs;

public static class UniversalHubHostExtensions
{
    /// <summary>
    /// Serves hubs over Wi-Fi on this HttpServer, beside BLE. Add each hub with <see cref="UniversalHubHostBuilder.AddHub{THub}"/>
    /// after registering it with AddBleHub. Start and stop hosting with <see cref="IUniversalHubHost"/>.
    /// <code>
    /// services.AddBleHub&lt;GameHub&gt;(serviceUuid, characteristicUuid);
    /// services.AddShinyHttpServer(http =>
    /// {
    ///     http.Options.Address = IPAddress.Any;   // reachable from other devices
    ///     http.Options.Port = 0;                  // any free port - mDNS publishes it
    ///     http.AddUniversalHubs().AddHub&lt;GameHub&gt;();
    /// }, autoStart: false);
    /// </code>
    /// </summary>
    public static UniversalHubHostBuilder AddUniversalHubs(this ShinyHttpServerBuilder builder, Action<UniversalHubHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var existing = builder.Services.FirstOrDefault(x => x.ServiceType == typeof(UniversalHubHostOptions))?.ImplementationInstance as UniversalHubHostOptions;
        if (existing != null)
        {
            configure?.Invoke(existing);
            return new UniversalHubHostBuilder(builder, existing);
        }

        var options = new UniversalHubHostOptions();
        configure?.Invoke(options);

        builder.AddSwitchboard();
        builder.Services.AddMdns();
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton<FileTokens>();
        builder.Services.TryAddSingleton<UniversalHubHost>();
        builder.Services.TryAddSingleton<IUniversalHubHost>(sp => sp.GetRequiredService<UniversalHubHost>());
        builder.Configure(server =>
        {
            // before routing: a hub that isn't shared over a tunnel doesn't exist as far as the internet can tell
            server.Use(async (ctx, next) =>
            {
                if (ctx.Connection.IsTunneled && !TunnelGate.IsAllowed(ctx.Request.Path, options.BasePath, server.Services!.GetServices<IHubBinding>()))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                await next(ctx);
            });
            FileRoutes.Map(server, options);
        });

        return new UniversalHubHostBuilder(builder, options);
    }
}


public sealed class UniversalHubHostBuilder
{
    readonly ShinyHttpServerBuilder builder;
    readonly UniversalHubHostOptions options;

    internal UniversalHubHostBuilder(ShinyHttpServerBuilder builder, UniversalHubHostOptions options)
    {
        this.builder = builder;
        this.options = options;
    }


    /// <summary>
    /// Serves <typeparamref name="THub"/> over Wi-Fi at <c>{BasePath}/{characteristicUuid}</c>. The hub must also be registered
    /// with AddBleHub - its characteristic UUID, options (MaxClients, ValidateClient) and clients are shared with BLE.
    /// </summary>
    /// <param name="mdnsServiceType">
    /// The mDNS service type this hub is advertised under - clients of this hub browse for the same one. Null uses
    /// <see cref="UniversalHubHostOptions.ServiceType"/>. Hubs that share a type share one advertisement.
    /// </param>
    /// <param name="allowTunnel">
    /// Whether clients may reach this hub through a tunnel (the public internet). Off by default: a tunnelled request
    /// for a hub that doesn't allow it gets a 404.
    /// </param>
    public UniversalHubHostBuilder AddHub<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>(string? mdnsServiceType = null, bool allowTunnel = false) where THub : class
    {
        if (mdnsServiceType != null)
            UniversalHubProtocol.ValidateServiceType(mdnsServiceType);

        HubBoardRegistration.Register<THub>();
        this.builder.Services.TryAddSingleton(sp => new HubBinding<THub>(
            sp,
            sp.GetRequiredService<UniversalHubHost>(),
            sp.GetRequiredService<FileTokens>(),
            mdnsServiceType,
            allowTunnel,
            sp.GetService<ILoggerFactory>()
        ));
        this.builder.Services.AddSingleton<IHubBinding>(sp => sp.GetRequiredService<HubBinding<THub>>());

        var options = this.options;
        this.builder.Configure(server =>
        {
            var binding = server.Services!.GetRequiredService<HubBinding<THub>>();
            server.MapSwitchboard<HubBoard<THub>>(UniversalHubProtocol.HubPath(options.BasePath, binding.CharacteristicUuid));
        });
        return this;
    }
}
