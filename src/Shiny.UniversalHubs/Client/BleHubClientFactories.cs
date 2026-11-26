using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Generated proxies register themselves here (module initializer) so AddBleHubClient can create them without reflection
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BleHubClientFactories
{
    static readonly ConcurrentDictionary<Type, Func<BleHubClientServices, string, string, BleHubClient>> factories = new();

    public static void Register<TContract, TProxy>(Func<BleHubClientServices, string, string, TProxy> factory)
        where TContract : class
        where TProxy : BleHubClient, TContract
        => factories[typeof(TContract)] = factory;


    internal static BleHubClient Create<TContract>(BleHubClientServices services, string serviceUuid, string characteristicUuid) where TContract : class
    {
        if (!factories.TryGetValue(typeof(TContract), out var factory))
        {
            RuntimeHelpers.RunModuleConstructor(typeof(TContract).Module.ModuleHandle);
            if (!factories.TryGetValue(typeof(TContract), out factory))
            {
                throw new InvalidOperationException(
                    $"No generated proxy for {typeof(TContract).FullName}. Mark it [BleHubClient] and make sure the project calling " +
                    "AddBleHubClient references Shiny.BluetoothLE.Hubs.Client (the generator only emits proxies there)."
                );
            }
        }
        return factory(services, serviceUuid, characteristicUuid);
    }
}
