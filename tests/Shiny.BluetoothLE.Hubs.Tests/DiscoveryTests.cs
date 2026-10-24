using Microsoft.Extensions.DependencyInjection;
using Shiny.BluetoothLE.Hosting;

namespace Shiny.BluetoothLE.Hubs.Tests;

/// <summary>
/// BleHubClient.Discover in an app that hosts hubs too - a scan on the hosting device can report its own advertisement
/// </summary>
public class DiscoveryTests
{
    const string Custom = "11111111-2222-3333-4444-555555555555";
    const string TestChar = "6e400002-b5a3-f393-e0a9-e50e24dcca9e";

    static DiscoveryTests() => Json.AddContext(TestJsonContext.Default);

    readonly FakeHostingManager hosting = new();
    readonly FakeBleManager ble = new();


    ServiceProvider Build(bool withServer, string serviceUuid = BleHubProtocolOptions.DefaultServiceUuid)
    {
        var services = new ServiceCollection()
            .AddSingleton<IBleHostingManager>(this.hosting)
            .AddSingleton<IBleManager>(this.ble)
            .AddSingleton<HubLog>();

        if (withServer)
            services.AddBleHubServer(s => s.Host(o => o.LocalName = "Me").AddHub<TestHub>(TestChar));

        services.AddBleHubClient<ITestHub>(TestChar, o => o.ServiceUuid = serviceUuid);
        return services.BuildServiceProvider();
    }


    // the scan pushes synchronously, so every result has been through the filter by the time Advertise returns
    (List<string?> Names, IDisposable Scan) Watch(ServiceProvider sp)
    {
        var names = new List<string?>();
        var scan = sp.GetRequiredService<IBleHubClient<ITestHub>>().Discover().Subscribe(x => names.Add(x.Name));
        Assert.True(this.ble.IsScanning);
        return (names, scan);
    }


    [Fact]
    public async Task TheHostsOwnAdvertisementIsLeftOut()
    {
        await using var sp = this.Build(withServer: true);
        await sp.GetRequiredService<IBleHubHost>().Start();
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
        var host = sp.GetRequiredService<IBleHubHost>();
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
        var host = sp.GetRequiredService<IBleHubHost>();
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
        await sp.GetRequiredService<IBleHubHost>().Start();
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

        var client = sp.GetRequiredService<IBleHubClient<ITestHub>>();
        await Assert.ThrowsAnyAsync<BleException>(() => client.Connect(new BleHubHostInfo(stale, "Host", -50)));
        Assert.True(current.ConnectCalled);
    }
}
