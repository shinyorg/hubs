using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Switchboard;

namespace Shiny.SwitchboardR;

public static class SwitchboardRHostExtensions
{
    /// <summary>
    /// Serves hubs over Wi-Fi on this HttpServer, beside BLE. Add each hub with <see cref="SwitchboardRHostBuilder.AddHub{THub}"/>
    /// after registering it with AddBleHub. Start and stop hosting with <see cref="ISwitchboardRHost"/>.
    /// <code>
    /// services.AddBleHub&lt;GameHub&gt;(serviceUuid, characteristicUuid);
    /// services.AddShinyHttpServer(http =>
    /// {
    ///     http.Options.Address = IPAddress.Any;   // reachable from other devices
    ///     http.Options.Port = 0;                  // any free port - mDNS publishes it
    ///     http.AddSwitchboardR().AddHub&lt;GameHub&gt;();
    /// }, autoStart: false);
    /// </code>
    /// </summary>
    public static SwitchboardRHostBuilder AddSwitchboardR(this ShinyHttpServerBuilder builder, Action<SwitchboardRHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var existing = builder.Services.FirstOrDefault(x => x.ServiceType == typeof(SwitchboardRHostOptions))?.ImplementationInstance as SwitchboardRHostOptions;
        if (existing != null)
        {
            configure?.Invoke(existing);
            return new SwitchboardRHostBuilder(builder, existing);
        }

        var options = new SwitchboardRHostOptions();
        configure?.Invoke(options);

        builder.AddSwitchboard();
        builder.Services.AddMdns();
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton<FileTokens>();
        builder.Services.TryAddSingleton<SwitchboardRHost>();
        builder.Services.TryAddSingleton<ISwitchboardRHost>(sp => sp.GetRequiredService<SwitchboardRHost>());
        builder.Configure(server => FileRoutes.Map(server, options));

        return new SwitchboardRHostBuilder(builder, options);
    }
}


public sealed class SwitchboardRHostBuilder
{
    readonly ShinyHttpServerBuilder builder;
    readonly SwitchboardRHostOptions options;

    internal SwitchboardRHostBuilder(ShinyHttpServerBuilder builder, SwitchboardRHostOptions options)
    {
        this.builder = builder;
        this.options = options;
    }


    /// <summary>
    /// Serves <typeparamref name="THub"/> over Wi-Fi at <c>{BasePath}/{characteristicUuid}</c>. The hub must also be registered
    /// with AddBleHub - its characteristic UUID, options (MaxClients, ValidateClient) and clients are shared with BLE.
    /// </summary>
    public SwitchboardRHostBuilder AddHub<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THub>() where THub : class
    {
        HubBoardRegistration.Register<THub>();
        this.builder.Services.TryAddSingleton<HubBinding<THub>>();
        this.builder.Services.AddSingleton<IHubBinding>(sp => sp.GetRequiredService<HubBinding<THub>>());

        var options = this.options;
        this.builder.Configure(server =>
        {
            var binding = server.Services!.GetRequiredService<HubBinding<THub>>();
            server.MapSwitchboard<HubBoard<THub>>(SwitchboardRProtocol.HubPath(options.BasePath, binding.CharacteristicUuid));
        });
        return this;
    }
}
