using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hosting;
using Shiny.UniversalHubs;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// AddUniversalServer / AddBleHubClient - what one call registers, and the mistakes it refuses
/// </summary>
public class RegistrationTests
{
    const string Custom = "11111111-2222-3333-4444-555555555555";
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string SecondChar = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

    static RegistrationTests() => Json.AddContext(TestJsonContext.Default);


    static ServiceProvider BuildServer(Action<UniversalServerBuilder> configure, FakeHostingManager hosting) => new ServiceCollection()
        .AddSingleton<IBleHostingManager>(hosting)
        .AddSingleton<HubLog>()
        .AddUniversalServer(configure)
        .BuildServiceProvider();


    [Fact]
    public async Task ServerServesEveryBleHubUnderItsServiceUuid()
    {
        var hosting = new FakeHostingManager();
        await using var sp = BuildServer(s => s
            .Configure(o => o.LocalName = "Host")
            .Protocol(o => o.RequestTimeout = TimeSpan.FromSeconds(3))
            .AddHub<TestHub>("test", h => h.UseBluetoothLE(Custom.ToUpperInvariant(), TestChar))
            .AddHub<SecondHub>("second", h => h.MaxClients(2).UseBluetoothLE(Custom, SecondChar)), hosting);

        Assert.Equal(TimeSpan.FromSeconds(3), sp.GetRequiredService<BleHubProtocolOptions>().RequestTimeout);
        Assert.NotNull(sp.GetRequiredService<IHubContext<TestHub>>());
        Assert.NotNull(sp.GetRequiredService<IHubContext<SecondHub>>());

        Assert.Equal(HubTransports.Ble, await sp.GetRequiredService<IUniversalHubHost>().Start());
        Assert.True(hosting.HasService(Custom));
        Assert.Equal([Custom], hosting.AdvertisedServices);
        Assert.Equal("Host", hosting.AdvertisedName);
        Assert.Contains($"add-service:{Custom}:2", hosting.Log);
    }


    [Fact]
    public void ServerIsRegisteredOnceWithAtLeastOneHub()
    {
        var services = new ServiceCollection().AddUniversalServer(s => s.AddHub<TestHub>("test", h => h.UseBluetoothLE(Custom, TestChar)));
        Assert.Throws<InvalidOperationException>(() => services.AddUniversalServer(s => s.AddHub<SecondHub>("second", h => h.UseBluetoothLE(Custom, SecondChar))));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(_ => { }));
    }


    [Fact]
    public void EveryHubNeedsATransport()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(s => s.AddHub<TestHub>("test", _ => { })));
        Assert.Contains("isn't served over any transport", ex.Message);
    }


    [Fact]
    public void ATransportSetUpForNoHubIsAMistake()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(s => s
            .Http(o => o.Advertise = false)
            .AddHub<TestHub>("test", h => h.UseBluetoothLE(Custom, TestChar))));
        Assert.Contains("no hub is served over it", ex.Message);
    }


    [Fact]
    public void ServerRefusesDuplicateHubsNamesAndCharacteristics()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(s => s
            .AddHub<TestHub>("test", h => h.UseBluetoothLE(Custom, TestChar))
            .AddHub<TestHub>("other", h => h.UseBluetoothLE(Custom, SecondChar))));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(s => s
            .AddHub<TestHub>("test", h => h.UseBluetoothLE(Custom, TestChar))
            .AddHub<SecondHub>("TEST", h => h.UseBluetoothLE(Custom, SecondChar))));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(s => s
            .AddHub<TestHub>("test", h => h.UseBluetoothLE(Custom, TestChar))
            .AddHub<SecondHub>("second", h => h.UseBluetoothLE(Custom, TestChar.ToUpperInvariant()))));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUniversalServer(s => s
            .AddHub<TestHub>("test", h => h.UseBluetoothLE(Custom, TestChar).UseBluetoothLE(Custom, SecondChar))));
    }


    [Fact]
    public void ServerRefusesInvalidNamesAndUuids()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUniversalServer(s => s.AddHub<TestHub>("has space", h => h.UseBluetoothLE(Custom, TestChar))));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUniversalServer(s => s.AddHub<TestHub>("test", h => h.UseBluetoothLE("180D", TestChar))));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddUniversalServer(s => s.AddHub<TestHub>("test", h => h.UseBluetoothLE(Custom, "not-a-uuid"))));
    }


    [Fact]
    public void ClientsUseTheirOwnServiceUuid()
    {
        using var sp = new ServiceCollection()
            .AddBleHubClient<ITestHub>(TestChar)
            .AddBleHubClient<ISecondHub>(SecondChar, o => o
                .Protocol(p => p.RequestTimeout = TimeSpan.FromSeconds(4))
                .ServiceUuid = Custom.ToUpperInvariant())
            .BuildServiceProvider();

        var test = (BleHubClient)(object)sp.GetRequiredService<IBleHubClient<ITestHub>>();
        var second = (BleHubClient)(object)sp.GetRequiredService<IBleHubClient<ISecondHub>>();
        Assert.Equal(BleHubProtocolOptions.DefaultServiceUuid, test.ServiceUuid);
        Assert.Equal(Custom, second.ServiceUuid);
        Assert.Equal(SecondChar, second.CharacteristicUuid);
        Assert.Same(second, sp.GetRequiredService<ISecondHub>());

        // protocol limits are app-wide
        Assert.Equal(TimeSpan.FromSeconds(4), sp.GetRequiredService<BleHubProtocolOptions>().RequestTimeout);
    }


    [Fact]
    public void ClientRefusesInvalidUuids()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddBleHubClient<ITestHub>(TestChar, o => o.ServiceUuid = "180D"));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddBleHubClient<ITestHub>("nope"));
    }
}
