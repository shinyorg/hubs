using System.Collections.ObjectModel;
using Shiny.UniversalHubs;

namespace Sample.MacOS;

/// <summary>
/// People whose chat is open and games being hosted on this network, plus an address box for a shared tunnel
/// </summary>
public class HomePage : ContentPage
{
    readonly MacClient client;
    readonly ObservableCollection<HubHostInfo> people = new();
    readonly ObservableCollection<HubHostInfo> games = new();
    readonly Entry name;
    readonly Entry address;
    readonly Label status = new() { FontSize = 12 };
    IDisposable? chatScan, gameScan;


    public HomePage(MacClient client)
    {
        this.client = client;
        this.Title = "Universal Hubs for Mac";

        this.name = new Entry { Text = client.Name, Placeholder = "Your name" };
        this.address = new Entry { Placeholder = "Address someone shared (https://...)" };
        var go = new Button { Text = "Message" };
        go.Clicked += async (_, _) => await this.MessageAddress();

        this.Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 14,
                Children =
                {
                    new Label { Text = "Universal Hubs for Mac", FontSize = 28, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "A client for the phone app: message people whose app is open, or join the games they host. Wi-Fi (mDNS) or a shared tunnel address." },
                    this.name,
                    new Grid
                    {
                        ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                        ColumnSpacing = 8,
                        Children = { this.address, Column(go, 1) }
                    },
                    this.status,
                    Header("💬 Message someone"),
                    this.List(this.people, "Searching this network... open Universal Hubs on a phone on the same Wi-Fi and it shows up here", this.Visit),
                    Header("🎮 Join a game"),
                    this.List(this.games, "Searching this network... tap Host a game on a phone and it shows up here", this.JoinGame)
                }
            }
        };
    }


    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.people.Clear();
        this.games.Clear();
        this.chatScan = this.client.Chat.DiscoverAll(HubTransports.Http).Subscribe(x => this.Dispatcher.Dispatch(() => Track(this.people, x)), this.OnScanError);
        this.gameScan = this.client.Game.DiscoverAll(HubTransports.Http).Subscribe(x => this.Dispatcher.Dispatch(() => Track(this.games, x)), this.OnScanError);
    }


    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        this.chatScan?.Dispose();
        this.gameScan?.Dispose();
    }


    void OnScanError(Exception ex) => this.Dispatcher.Dispatch(() => this.status.Text = "Discovery failed: " + ex.Message);


    static void Track(ObservableCollection<HubHostInfo> list, HubHostInfo found)
    {
        var existing = list.FirstOrDefault(x => x.Key == found.Key);
        if (existing != null)
            list.Remove(existing);
        if (found.Transports != HubTransports.None)
            list.Add(found);
    }


    async Task MessageAddress()
    {
        var text = this.address.Text?.Trim();
        if (String.IsNullOrEmpty(text))
            return;
        if (!text.Contains("://"))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            await this.DisplayAlertAsync("Address", "That isn't an address", "OK");
            return;
        }
        await this.Visit(HubHostInfo.FromUrl(uri));
    }


    async Task Visit(HubHostInfo host) => await this.Busy("Connecting to " + host.Name, async () =>
    {
        await this.client.Visit(host);
        await this.Navigation.PushAsync(new ChatPage(this.client));
    });


    async Task JoinGame(HubHostInfo host) => await this.Busy("Joining " + host.Name, async () =>
    {
        await this.client.Join(host);
        await this.Navigation.PushAsync(new GamePage(this.client));
    });


    async Task Busy(string text, Func<Task> work)
    {
        var name = this.name.Text?.Trim();
        if (String.IsNullOrEmpty(name))
        {
            await this.DisplayAlertAsync("Name", "Enter your name first", "OK");
            return;
        }
        this.client.Name = name;
        this.status.Text = text + "...";
        try
        {
            await work();
            this.status.Text = "";
        }
        catch (Exception ex)
        {
            this.status.Text = "";
            await this.DisplayAlertAsync("Could not connect", ex.Message, "OK");
        }
    }


    View List(ObservableCollection<HubHostInfo> items, string empty, Func<HubHostInfo, Task> tapped)
    {
        // the AppKit backend doesn't pick up children added to a layout that is already on screen, so every change
        // builds a fresh list and swaps it in
        var host = new ContentView();
        void Refresh()
        {
            var list = new VerticalStackLayout { Spacing = 8 };
            if (items.Count == 0)
                list.Children.Add(new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        new ActivityIndicator { IsRunning = true, WidthRequest = 16, HeightRequest = 16 },
                        new Label { Text = empty, TextColor = Colors.Gray, VerticalOptions = LayoutOptions.Center }
                    }
                });

            foreach (var item in items)
            {
                var row = new Button { Text = $"{item.Name}   📶  ›", HorizontalOptions = LayoutOptions.Fill };
                row.Clicked += async (_, _) => await tapped(item);
                list.Children.Add(row);
            }
            host.Content = list;
        }
        items.CollectionChanged += (_, _) => Refresh();
        Refresh();
        return host;
    }


    static Label Header(string text) => new() { Text = text, FontSize = 18, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 10, 0, 0) };

    static View Column(View view, int column)
    {
        Grid.SetColumn(view, column);
        return view;
    }
}
