using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shiny.UniversalHubs;

/// <summary>
/// Generated proxies register themselves here (module initializer) so AddUniversalHubClient can create them without reflection
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HubProxyFactories
{
    static readonly ConcurrentDictionary<Type, Func<HubClientServices, string, HubProxy>> factories = new();

    public static void Register<TContract, TProxy>(Func<HubClientServices, string, TProxy> factory)
        where TContract : class
        where TProxy : HubProxy, TContract
        => factories[typeof(TContract)] = factory;


    internal static HubProxy Create<TContract>(HubClientServices services, string hubName) where TContract : class
    {
        if (!factories.TryGetValue(typeof(TContract), out var factory))
        {
            RuntimeHelpers.RunModuleConstructor(typeof(TContract).Module.ModuleHandle);
            if (!factories.TryGetValue(typeof(TContract), out factory))
            {
                throw new InvalidOperationException(
                    $"No generated proxy for {typeof(TContract).FullName}. Mark it [UniversalHubContract] and make sure the project calling " +
                    "AddUniversalHubClient references Shiny.UniversalHubs (the package carries the generator)."
                );
            }
        }
        return factory(services, hubName);
    }
}
