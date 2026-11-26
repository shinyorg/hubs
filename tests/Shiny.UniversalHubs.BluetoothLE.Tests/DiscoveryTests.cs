using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hosting;
using Shiny.UniversalHubs;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// HubProxy.Discover in an app that hosts hubs too - a scan on the hosting device can report its own advertisement
/// </summary>
public class DiscoveryTests
{
    const string Custom = "11111111-2222-3333-4444-555555555555";
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

    static DiscoveryTests() => Json.AddContext(TestJsonContext.Default);

    readonly FakeHostingManager hosting = new();
    readonly FakeBleManager ble = new();


    ServiceProvider Build(bool withServer, string serviceUuid = HubProtocolOptions.DefaultServiceUuid, string? scanFor = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IBleHostingManager>(this.hosting)
            .AddSingleton<IBleManager>(this.ble)
            .AddSingleton<HubLog>();

        if (withServer)
            services.AddUniversalServer(s => s.Configure(o => o.LocalName = "Me").AddHub<TestHub>("test", h => h.UseBluetoothLE(HubProtocolOptions.DefaultServiceUuid, TestChar)));

        services.AddUniversalHubClient<ITestHub>("test", o => o.UseBluetoothLE(serviceUuid, TestChar, scanFor));
        return services.BuildServiceProvider();
    }


    // the scan pushes synchronously, so every result has been through the filter by the time Advertise returns
    (List<string?> Names, IDisposable Scan) Watch(ServiceProvider sp)
    {
        var names = new List<string?>();
        var scan = sp.GetRequiredService<IUniversalHubClient<ITestHub>>().DiscoverAll().Subscribe(x => names.Add(x.Endpoint<BleHubEndpoint>()?.Name));
        Assert.True(this.ble.IsScanning);
        return (names, scan);
    }


    [Fact]
    public async Task TheHostsOwnAdvertisementIsLeftOut()
    {
        await using var sp = this.Build(withServer: true);
        await sp.GetRequiredService<IUniversalHubHost>().Start();
        var (names, scan) = this.Watch(sp);
        using (scan)
        {
            this.ble.Advertise("self", "Me");
            this.ble.Advertise("other", "Someone");
            this.ble.Advertise("nameless", null);
        }

        // a host whose advertisement carries no name can't be told apart from this one - it is kept
        Assert.Equal(["Someone", null], names);
    }


    [Fact]
    public async Task OnceTheHostStopsTheSameNameIsSomeoneElse()
    {
        await using var sp = this.Build(withServer: true);
        var host = sp.GetRequiredService<IUniversalHubHost>();
        await host.Start();
        var (names, scan) = this.Watch(sp);
        using (scan)
        {
            this.ble.Advertise("self", "Me");
            await host.Stop();
            this.ble.Advertise("twin", "Me");
        }

        Assert.Equal(["Me"], names);
    }


    [Fact]
    public async Task ARenamedHostIsLeftOutUnderItsNewName()
    {
        await using var sp = this.Build(withServer: true);
        var host = sp.GetRequiredService<IUniversalHubHost>();
        await host.Start();
        var (names, scan) = this.Watch(sp);
        using (scan)
        {
            await host.Rename("New");
            this.ble.Advertise("self", "New");
            this.ble.Advertise("other", "Me");
        }

        Assert.Equal(["Me"], names);
    }


    [Fact]
    public async Task AHostNotYetStartedIsNotAdvertising()
    {
        await using var sp = this.Build(withServer: true);
        var (names, scan) = this.Watch(sp);
        using (scan)
            this.ble.Advertise("twin", "Me");

        Assert.Equal(["Me"], names);
    }


    [Fact]
    public async Task AClientScanningAnotherServiceKeepsTheSameName()
    {
        await using var sp = this.Build(withServer: true, serviceUuid: Custom);
        await sp.GetRequiredService<IUniversalHubHost>().Start();
        var (names, scan) = this.Watch(sp);
        using (scan)
            this.ble.Advertise("other", "Me");

        Assert.Equal(["Me"], names);
    }


    [Fact]
    public async Task AClientOnlyAppFiltersNothing()
    {
        await using var sp = this.Build(withServer: false);
        var (names, scan) = this.Watch(sp);
        using (scan)
        {
            this.ble.Advertise("a", "Me");
            this.ble.Advertise("b", null);
        }

        Assert.Equal(["Me", null], names);
    }


    [Fact]
    public async Task ConnectUsesTheManagersCurrentPeripheral()
    {
        // discovered in an earlier scan; a later scan replaced it in the manager's cache (Shiny does on Apple), so the
        // connection's events go to the manager's instance - connecting the stale one would wait forever
        await using var sp = this.Build(withServer: false);
        var stale = new FakeScanPeripheral("host-1", "Host");
        var current = new FailingConnectPeripheral("host-1");
        this.ble.Known["host-1"] = current;

        var client = sp.GetRequiredService<IUniversalHubClient<ITestHub>>();
        var host = new HubHostInfo("Host", "Host", null).With(typeof(BleClientTransport), new BleHubEndpoint(stale, "Host", -50), []);
        await Assert.ThrowsAnyAsync<BleException>(() => client.Connect(host));
        Assert.True(current.ConnectCalled);
    }


    [Fact]
    public async Task AClientScansForItsHubsServiceUnlessToldOtherwise()
    {
        await using (var sp = this.Build(withServer: false, serviceUuid: Custom))
        {
            using var _ = this.Watch(sp).Scan;
            Assert.Equal([Custom], this.ble.LastScan!.ServiceUuids);
        }

        // a hub whose service isn't advertised is found through one the host does advertise
        await using (var sp = this.Build(withServer: false, serviceUuid: Custom, scanFor: "180D"))
        {
            using var _ = this.Watch(sp).Scan;
            Assert.Equal(["0000180d-0000-1000-8000-00805f9b34fb"], this.ble.LastScan!.ServiceUuids);
        }
    }
}
