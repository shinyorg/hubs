using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hosting;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// Each hub in a GATT service of its own choosing: several services, what goes in the advertisement, and what fits
/// </summary>
public class BlePerHubTests : IAsyncLifetime
{
    const string ServiceA = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
    const string ServiceB = "8f1a0001-9c4d-4a6e-b1f2-0c3d5e7f9a1b";
    const string Short = "0000180d-0000-1000-8000-00805f9b34fb";
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";
    const string SecondChar = "6e400003-b5a3-f393-e0a9-e50e24dcca9e";

    readonly FakeHostingManager hosting = new();
    ServiceProvider? services;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (this.services != null)
            await this.services.DisposeAsync();
    }


    ServiceProvider Build(Func<UniversalHubBuilder, UniversalHubBuilder> test, Func<UniversalHubBuilder, UniversalHubBuilder> second)
    {
        this.services = new ServiceCollection()
            .AddSingleton<HubLog>()
            .AddSingleton<IBleHostingManager>(this.hosting)
            .AddUniversalServer(server => server
                .Configure(o => o.LocalName = "Host")
                .AddHub<TestHub>("test", h => test(h))
                .AddHub<SecondHub>("second", h => second(h)))
            .BuildServiceProvider();
        return this.services;
    }


    [Fact]
    public async Task HubsCanEachHaveTheirOwnService()
    {
        var sp = this.Build(h => h.UseBluetoothLE("180D", TestChar), h => h.UseBluetoothLE(ServiceB, SecondChar));
        var host = sp.GetRequiredService<IUniversalHubHost>();

        await host.StartHub<TestHub>();
        Assert.True(this.hosting.HasService(Short));
        Assert.False(this.hosting.HasService(ServiceB));
        Assert.Equal([Short], this.hosting.AdvertisedServices);

        await host.StartHub<SecondHub>();
        Assert.True(this.hosting.HasService(ServiceB));
        Assert.Equal(new[] { Short, ServiceB }.Order(), this.hosting.AdvertisedServices.Order());

        // a stopped hub's service leaves the GATT server and the advertisement
        await host.StopHub<TestHub>();
        Assert.False(this.hosting.HasService(Short));
        Assert.Equal([ServiceB], this.hosting.AdvertisedServices);
        Assert.Equal("Host", this.hosting.AdvertisedName);

        await host.StopHub<SecondHub>();
        Assert.False(this.hosting.IsAdvertising);
    }


    [Fact]
    public async Task AServiceSharedWithARunningHubStays()
    {
        var sp = this.Build(h => h.UseBluetoothLE(ServiceA, TestChar), h => h.UseBluetoothLE(ServiceA, SecondChar));
        var host = sp.GetRequiredService<IUniversalHubHost>();

        await host.Start();
        await host.StopHub<TestHub>();

        Assert.True(this.hosting.HasService(ServiceA));
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);
    }


    [Fact]
    public async Task AHubThatIsNotAdvertisedIsServedButLeftOutOfTheAdvertisement()
    {
        var sp = this.Build(h => h.UseBluetoothLE(ServiceA, TestChar), h => h.UseBluetoothLE(ServiceB, SecondChar, advertise: false));
        var host = sp.GetRequiredService<IUniversalHubHost>();

        await host.Start();
        Assert.True(this.hosting.HasService(ServiceB));
        Assert.Equal([ServiceA], this.hosting.AdvertisedServices);

        // only it running: served, nothing on air
        await host.StopHub<TestHub>();
        Assert.True(this.hosting.HasService(ServiceB));
        Assert.False(this.hosting.IsAdvertising);
    }


    [Fact]
    public void AdvertisedServicesMustFitInOneAdvertisement()
    {
        // two 128-bit UUIDs: 3 + 2 + 32 bytes
        var ex = Assert.Throws<InvalidOperationException>(() => this.Build(h => h.UseBluetoothLE(ServiceA, TestChar), h => h.UseBluetoothLE(ServiceB, SecondChar)));
        Assert.Contains("31", ex.Message);

        // counted whether or not they ever run together, and fine once one isn't advertised or is short
        this.Build(h => h.UseBluetoothLE(ServiceA, TestChar), h => h.UseBluetoothLE(ServiceB, SecondChar, advertise: false)).Dispose();
        this.Build(h => h.UseBluetoothLE(ServiceA, TestChar), h => h.UseBluetoothLE("180D", SecondChar)).Dispose();
        this.Build(h => h.UseBluetoothLE("180D", TestChar), h => h.UseBluetoothLE("ABCD1234", SecondChar)).Dispose();
    }


    [Theory]
    [InlineData("180D", "0000180d-0000-1000-8000-00805f9b34fb", 2)]
    [InlineData("ABCD1234", "abcd1234-0000-1000-8000-00805f9b34fb", 4)]
    [InlineData("6E400001-B5A3-F393-E0A9-E50E24DCCA9E", "6e400001-b5a3-f393-e0a9-e50e24dcca9e", 16)]
    [InlineData("0000180d-0000-1000-8000-00805f9b34fb", "0000180d-0000-1000-8000-00805f9b34fb", 2)]
    public void ShortUuidsExpandOntoTheBluetoothBase(string uuid, string expected, int advertisedBytes)
    {
        var normalized = BleUuid.Normalize(uuid, "uuid");
        Assert.Equal(expected, normalized);
        Assert.Equal(advertisedBytes, BleUuid.AdvertisedSize(normalized));
    }


    [Fact]
    public void AdvertisementLengthCountsEachSizeInItsOwnField()
    {
        Assert.Equal(21, BleAdvertisement.Length([new BleHubSettings(ServiceA, TestChar)]));
        Assert.Equal(25, BleAdvertisement.Length([new BleHubSettings(ServiceA, TestChar), new BleHubSettings(Short, SecondChar)]));
        // a service shared by two hubs is in it once; one that isn't advertised not at all
        Assert.Equal(21, BleAdvertisement.Length([new BleHubSettings(ServiceA, TestChar), new BleHubSettings(ServiceA, SecondChar), new BleHubSettings(ServiceB, "1234", false)]));
    }
}
