using Shiny.UniversalHubs;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// Host transports as the tests talk about them
/// </summary>
static class TransportAssert
{
    /// <summary>
    /// Exactly these transport types, in any order
    /// </summary>
    public static void Equal(IEnumerable<Type> expected, IEnumerable<IHubTransport> actual)
        => Assert.Equal(expected.Select(x => x.Name).Order(), actual.Select(x => x.GetType().Name).Order());

    public static void Equal(IEnumerable<Type> expected, IEnumerable<Type> actual)
        => Assert.Equal(expected.Select(x => x.Name).Order(), actual.Select(x => x.Name).Order());

    /// <summary>
    /// The short names the tests log: Ble, Http, Multipeer, Nearby
    /// </summary>
    public static string Name(IHubTransport? transport) => transport switch
    {
        BleHostTransport => "Ble",
        HttpHostTransport => "Http",
        null => "None",
        _ => transport.DisplayName
    };
}
