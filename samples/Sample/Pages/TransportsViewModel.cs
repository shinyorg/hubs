using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.UniversalHubs;
using Sample.Hub;
using Sample.Services;

namespace Sample.Pages;

/// <summary>
/// The transport driver: each transport this device has, hosting and connecting turned on and off one by one, or one alone
/// </summary>
[ShellMap<TransportsPage>("Transports")]
public partial class TransportsViewModel(
    ShellServices shell,
    IPlatform platform,
    TransportDriver driver,
    IUniversalHubClient<IChatHub> chat,
    IUniversalHubClient<IGameHub> game
) : ObservableObject, IPageLifecycleAware
{
    public ObservableCollection<TransportRow> Transports { get; } = new();


    public void OnAppearing()
    {
        this.Transports.Clear();
        foreach (var transport in driver.Available)
            this.Transports.Add(new TransportRow(transport, this));

        driver.Changed += this.OnChanged;
        chat.StatusChanged += this.OnStatus;
        game.StatusChanged += this.OnStatus;
    }


    public void OnDisappearing()
    {
        driver.Changed -= this.OnChanged;
        chat.StatusChanged -= this.OnStatus;
        game.StatusChanged -= this.OnStatus;
    }


    [RelayCommand]
    Task All() => this.Run(driver.All);


    internal bool IsHosting(SampleTransport transport) => driver.IsHosting(transport);
    internal bool IsConnecting(SampleTransport transport) => driver.IsConnecting(transport);

    internal Task SetHosting(SampleTransport transport, bool on) => this.Run(() => driver.SetHosting(transport, on));
    internal Task SetConnecting(SampleTransport transport, bool on) => this.Run(() => driver.SetConnecting(transport, on));
    internal Task Solo(SampleTransport transport) => this.Run(() => driver.Solo(transport));


    internal string Status(SampleTransport transport)
    {
        var parts = new List<string>();
        if (driver.IsHosting(transport))
            parts.Add(driver.IsRunning(transport) ? "serving your hubs" : "on, nothing served (radio off or unavailable?)");

        if (chat.Transport?.GetType() == transport.ClientType)
            parts.Add("chat connected");
        if (game.Transport?.GetType() == transport.ClientType)
            parts.Add("game connected");

        return parts.Count == 0 ? "off" : String.Join(" · ", parts);
    }


    async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await shell.Dialogs.Alert("Transports", ex.Message);
        }
        this.Refresh();
    }


    void OnChanged(object? sender, EventArgs e) => platform.InvokeOnMainThread(this.Refresh);
    void OnStatus(object? sender, HubStatusChangedEventArgs e) => platform.InvokeOnMainThread(this.Refresh);

    void Refresh()
    {
        foreach (var row in this.Transports)
            row.Refresh();
    }
}


public partial class TransportRow(SampleTransport transport, TransportsViewModel owner) : ObservableObject
{
    public string Title => transport.Title;

    public bool Hosting
    {
        get => owner.IsHosting(transport);
        set
        {
            if (value != owner.IsHosting(transport))
                _ = owner.SetHosting(transport, value);
        }
    }

    public bool Connecting
    {
        get => owner.IsConnecting(transport);
        set
        {
            if (value != owner.IsConnecting(transport))
                _ = owner.SetConnecting(transport, value);
        }
    }

    public string Status => owner.Status(transport);

    [RelayCommand]
    Task Solo() => owner.Solo(transport);

    public void Refresh()
    {
        this.OnPropertyChanged(nameof(this.Hosting));
        this.OnPropertyChanged(nameof(this.Connecting));
        this.OnPropertyChanged(nameof(this.Status));
    }
}
