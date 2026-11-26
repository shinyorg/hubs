using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.UniversalHubs;
using Sample.Hub;
using Sample.Services;

namespace Sample.Pages;

[ShellMap<JoinPage>("Join")]
public partial class JoinViewModel(
    ShellServices shell,
    IPlatform platform,
    IUniversalHubClient<IGameHub> client,
    PlayerSettings settings
) : ObservableObject, IPageLifecycleAware
{
    IDisposable? scan;

    public ObservableCollection<HostItem> Hosts { get; } = new();
    [ObservableProperty] public partial string Status { get; set; } = "Looking for games nearby and on this network...";

    /// <summary>
    /// Which transports this device connects over - the transport driver
    /// </summary>
    [RelayCommand]
    Task Transports() => shell.Navigator.NavigateToTransports();


    public void OnAppearing()
    {
        this.Hosts.Clear();

        // one list for every transport - a host seen several ways is one entry
        this.scan = client
            .DiscoverAll()
            .Subscribe(
                found => platform.InvokeOnMainThread(() => this.OnFound(found)),
                ex => platform.InvokeOnMainThread(() => this.Status = ex.Message)
            );
    }


    public void OnDisappearing()
    {
        this.scan?.Dispose();
        this.scan = null;
    }


    void OnFound(HubHostInfo info)
    {
        var existing = this.Hosts.FirstOrDefault(x => x.Info.Key == info.Key);
        if (info.Transports.Count == 0)
        {
            if (existing != null)
                this.Hosts.Remove(existing);
        }
        else if (existing == null)
        {
            this.Hosts.Add(new HostItem(info));
        }
        else
        {
            existing.Update(info);
        }
    }


    [RelayCommand]
    Task Connect(HostItem item) => shell.Navigator.NavigateToGame(item.Info);
}


public partial class HostItem(HubHostInfo info) : ObservableObject
{
    public HubHostInfo Info { get; private set; } = info;
    [ObservableProperty] public partial string Name { get; set; } = info.Name;
    [ObservableProperty] public partial string Reach { get; set; } = Describe(info);

    public void Update(HubHostInfo info)
    {
        this.Info = info;
        this.Name = info.Name;
        this.Reach = Describe(info);
    }

    // in the order a connect would try them
    static string Describe(HubHostInfo info) => String.Join("   ", info.Transports.Select(t => t == typeof(BleClientTransport) && info.Endpoint<BleHubEndpoint>() is { } ble
        ? $"{TransportDriver.Describe(t)} {ble.Rssi} dBm"
        : TransportDriver.Describe(t)));
}
