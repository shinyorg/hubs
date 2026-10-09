using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.UniversalHubs;
using Sample.Hub;
using Sample.Services;

namespace Sample.Pages;

/// <summary>
/// Finds someone to message: chats nearby (BLE) or on this network (mDNS), or an address someone shared from far away
/// </summary>
[ShellMap<VisitPage>("Visit")]
public partial class VisitViewModel(
    ShellServices shell,
    IUniversalHubClient<IChatHub> client,
    ChatService chat
) : ObservableObject, IPageLifecycleAware
{
    IDisposable? scan;

    public ObservableCollection<HostItem> People { get; } = new();
    [ObservableProperty] public partial string? Address { get; set; }
    [ObservableProperty] public partial bool IsConnecting { get; set; }


    public void OnAppearing()
    {
        this.People.Clear();
        this.scan = client
            .DiscoverAll()
            .Subscribe(found => shell.MainThread.BeginInvokeOnMainThread(() =>
            {
                var existing = this.People.FirstOrDefault(x => x.Info.Key == found.Key);
                if (found.Transports == HubTransports.None)
                {
                    if (existing != null)
                        this.People.Remove(existing);
                }
                else if (existing == null)
                {
                    this.People.Add(new HostItem(found));
                }
                else
                {
                    existing.Update(found);
                }
            }));
    }


    public void OnDisappearing()
    {
        this.scan?.Dispose();
        this.scan = null;
    }


    [RelayCommand]
    Task Connect(HostItem item) => this.Open(item.Info);


    [RelayCommand]
    async Task ConnectToAddress()
    {
        var text = this.Address?.Trim();
        if (String.IsNullOrEmpty(text))
            return;

        if (!text.Contains("://"))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            await shell.Dialogs.Alert("Address", "That isn't an address");
            return;
        }
        await this.Open(HubHostInfo.FromUrl(uri));
    }


    async Task Open(HubHostInfo info)
    {
        if (this.IsConnecting)
            return;

        this.IsConnecting = true;
        try
        {
            chat.Selected = await chat.Visit(info);
            await shell.Navigator.NavigateToChat();
        }
        catch (Exception ex)
        {
            await shell.Dialogs.Alert("Could not connect", ex.Message);
        }
        finally
        {
            this.IsConnecting = false;
        }
    }
}
