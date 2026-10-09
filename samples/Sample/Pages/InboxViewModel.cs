using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.Net.HttpServer.Ssh;
using Shiny.UniversalHubs;
using Sample.Services;

namespace Sample.Pages;

/// <summary>
/// Everyone talking to this device's chat, and the chats it is visiting - plus sharing the chat on the internet
/// </summary>
[ShellMap<InboxPage>("Inbox")]
public partial class InboxViewModel(ShellServices shell, ChatService chat) : ObservableObject, IPageLifecycleAware
{
    CancellationTokenSource? sharing;

    public ChatService Chat => chat;
    [ObservableProperty] public partial string OpenOn { get; set; } = "";
    [ObservableProperty] public partial bool IsSharing { get; set; }
    [ObservableProperty] public partial bool IsStartingShare { get; set; }
    [ObservableProperty] public partial string? PublicUrl { get; set; }
    [ObservableProperty] public partial string? ShareStatus { get; set; }


    public void OnAppearing()
    {
        var over = chat.HostedOver;
        this.OpenOn = over == HubTransports.None
            ? "Your chat isn't open - check Bluetooth and Wi-Fi"
            : "Your chat is open over " + String.Join(" + ", new[]
            {
                over.HasFlag(HubTransports.Ble) ? "🔵 Bluetooth" : null,
                over.HasFlag(HubTransports.Http) ? "📶 Wi-Fi" : null
            }.Where(x => x != null));

        chat.Tunnel.PropertyChanged += this.OnTunnelChanged;
        this.OnTunnelChanged(null, new PropertyChangedEventArgs(null));
    }


    public void OnDisappearing() => chat.Tunnel.PropertyChanged -= this.OnTunnelChanged;


    // the address changes every time a free tunnel reconnects - always show the live one
    void OnTunnelChanged(object? sender, PropertyChangedEventArgs e) => shell.MainThread.BeginInvokeOnMainThread(() =>
    {
        this.PublicUrl = chat.Tunnel.PublicUrl;
        this.IsSharing = chat.Tunnel.State is QuickTunnelState.Connected or QuickTunnelState.Reconnecting or QuickTunnelState.Connecting;
        this.ShareStatus = chat.Tunnel.State switch
        {
            QuickTunnelState.Connected => "Anyone with this address can message you:",
            QuickTunnelState.Reconnecting => "Reconnecting - the address will change...",
            QuickTunnelState.Connecting => "Opening a tunnel...",
            QuickTunnelState.Failed => chat.Tunnel.LastError,
            _ => "Share your chat beyond this network (the game is never shared)"
        };
    });


    [RelayCommand]
    async Task ToggleSharing()
    {
        if (this.IsSharing || this.IsStartingShare)
        {
            this.sharing?.Cancel();
            await chat.StopSharing();
            return;
        }

        this.IsStartingShare = true;
        this.sharing = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (await chat.StartSharing(this.sharing.Token) == null)
                await shell.Dialogs.Alert("Share", chat.Tunnel.LastError ?? "The tunnel never reported its address");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await shell.Dialogs.Alert("Share", ex.Message);
        }
        finally
        {
            this.IsStartingShare = false;
        }
    }


    [RelayCommand]
    async Task CopyUrl()
    {
        if (this.PublicUrl != null)
            await Clipboard.Default.SetTextAsync(this.PublicUrl);
    }


    [RelayCommand]
    async Task Open(Conversation conversation)
    {
        chat.Selected = conversation;
        await shell.Navigator.NavigateToChat();
    }


    [RelayCommand]
    Task Visit() => shell.Navigator.NavigateToVisit();
}
