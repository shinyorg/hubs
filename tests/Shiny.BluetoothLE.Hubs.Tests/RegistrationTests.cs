using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hosting;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// AddBleHubServer / AddBleHubClient - what one call registers, and the mistakes it refuses
/// </summary>
public class RegistrationTests
{
    const string Custom = "11111111-2222-3333-4444-555555555555";
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string SecondChar = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

    static RegistrationTests() => Json.AddContext(TestJsonContext.Default);


    static ServiceProvider BuildServer(Action<BleHubServerBuilder> configure, FakeHostingManager hosting) => new ServiceCollection()
        .AddSingleton<IBleHostingManager>(hosting)
        .AddSingleton<HubLog>()
        .AddBleHubServer(configure)
        .BuildServiceProvider();


    [Fact]
    public async Task ServerServesEveryHubUnderItsServiceUuid()
    {
        var hosting = new FakeHostingManager();
        await using var sp = BuildServer(s => s
            .ServiceUuid(Custom.ToUpperInvariant())
            .Host(o => o.LocalName = "Host")
            .Protocol(o => o.RequestTimeout = TimeSpan.FromSeconds(3))
            .AddHub<TestHub>(TestChar)
            .AddHub<SecondHub>(SecondChar, o => o.MaxClients = 2), hosting);

        Assert.Equal(Custom, sp.GetRequiredService<BleHubHostOptions>().ServiceUuid);
        Assert.Equal(TimeSpan.FromSeconds(3), sp.GetRequiredService<BleHubProtocolOptions>().RequestTimeout);
        Assert.NotNull(sp.GetRequiredService<IHubContext<TestHub>>());
        Assert.NotNull(sp.GetRequiredService<IHubContext<SecondHub>>());

        await sp.GetRequiredService<IBleHubHost>().Start();
        Assert.True(hosting.HasService(Custom));
        Assert.Equal([Custom], hosting.AdvertisedServices);
        Assert.Equal("Host", hosting.AdvertisedName);
        Assert.Contains($"add-service:{Custom}:2", hosting.Log);
    }


    [Fact]
    public void ServerDefaultsToTheLibraryServiceUuid()
    {
        using var sp = BuildServer(s => s.AddHub<TestHub>(TestChar), new FakeHostingManager());
        Assert.Equal(BleHubProtocolOptions.DefaultServiceUuid, sp.GetRequiredService<BleHubHostOptions>().ServiceUuid);
    }


    [Fact]
    public void ServerIsRegisteredOnceWithAtLeastOneHub()
    {
        var services = new ServiceCollection().AddBleHubServer(s => s.AddHub<TestHub>(TestChar));
        Assert.Throws<InvalidOperationException>(() => services.AddBleHubServer(s => s.AddHub<SecondHub>(SecondChar)));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddBleHubServer(_ => { }));
    }


    [Fact]
    public void ServerRefusesDuplicateHubsAndCharacteristics()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddBleHubServer(s => s
            .AddHub<TestHub>(TestChar)
            .AddHub<TestHub>(SecondChar)));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddBleHubServer(s => s
            .AddHub<TestHub>(TestChar)
            .AddHub<SecondHub>(TestChar.ToUpperInvariant())));
    }


    [Fact]
    public void ServerRefusesInvalidUuids()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddBleHubServer(s => s.ServiceUuid("180D").AddHub<TestHub>(TestChar)));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddBleHubServer(s => s.AddHub<TestHub>("not-a-uuid")));
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
