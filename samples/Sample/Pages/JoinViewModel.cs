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
    IUniversalHubClient<IGameHub> client,
    PlayerSettings settings
) : ObservableObject, IPageLifecycleAware
{
    IDisposable? scan;

    public ObservableCollection<HostItem> Hosts { get; } = new();
    [ObservableProperty] public partial string Status { get; set; } = "Looking for games nearby and on this network...";
    [ObservableProperty] public partial string ConnectChoice { get; set; } = settings.ConnectChoice;
    public IReadOnlyList<string> ConnectChoices => PlayerSettings.ConnectChoices;

    partial void OnConnectChoiceChanged(string value) => settings.ConnectChoice = value;


    public void OnAppearing()
    {
        this.Hosts.Clear();

        // one list for both transports - a host seen over BLE and Wi-Fi is one entry
        this.scan = client
            .DiscoverAll()
            .Subscribe(
                found => shell.MainThread.BeginInvokeOnMainThread(() => this.OnFound(found)),
                ex => shell.MainThread.BeginInvokeOnMainThread(() => this.Status = ex.Message)
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
        if (info.Transports == HubTransports.None)
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

    static string Describe(HubHostInfo info)
    {
        var parts = new List<string>();
        if (info.Http != null)
            parts.Add("📶 Wi-Fi");
        if (info.Multipeer != null)
            parts.Add("📡 Multipeer");
        if (info.Nearby != null)
            parts.Add("🛰️ Nearby");
        if (info.Ble != null)
            parts.Add($"🔵 Bluetooth {info.Rssi} dBm");
        return String.Join("   ", parts);
    }
}
