using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.Net.HttpServer.Ssh;
using Shiny.UniversalHubs;
using Sample.Hub;
using Sample.Services;

namespace Sample.Pages;

/// <summary>
/// Everyone this device can talk to, once each: people nearby or on the network, people visiting this device's chat, and
/// past conversations. Message, call or video call anyone straight from the list - it connects first when it has to.
/// </summary>
[ShellMap<InboxPage>("Inbox")]
public partial class InboxViewModel(
    ShellServices shell,
    ChatService chat,
    CallService calls,
    PlayerSettings settings,
    IUniversalHubClient<IChatHub> client
) : ObservableObject, IPageLifecycleAware
{
    // how often Wi-Fi-only hosts are probed, and how long a host stays listed without being seen again
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(4);
    static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(12);

    CancellationTokenSource? scanning;
    CancellationTokenSource? sharing;
    CancellationTokenSource? reaching;

    public ObservableCollection<Person> People { get; } = new();

    [ObservableProperty] public partial string OpenOn { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? BusyText { get; set; }
    [ObservableProperty] public partial bool ShowOptions { get; set; }
    [ObservableProperty] public partial string? Address { get; set; }

    [ObservableProperty] public partial bool IsSharing { get; set; }
    [ObservableProperty] public partial bool IsStartingShare { get; set; }
    [ObservableProperty] public partial string? PublicUrl { get; set; }
    [ObservableProperty] public partial string? ShareStatus { get; set; }

    public IReadOnlyList<string> VideoCodecs => CallService.VideoCodecChoices;

    /// <summary>
    /// The video codec this phone offers on calls - force H264 or Jpeg to test each
    /// </summary>
    /// <summary>
    /// Reach people over Bluetooth when they're nearby, even if they're on the same Wi-Fi
    /// </summary>
    public bool PreferBluetooth
    {
        get => settings.PreferBluetooth;
        set
        {
            settings.PreferBluetooth = value;
            this.OnPropertyChanged();
        }
    }

    public string VideoCodec
    {
        get => calls.VideoCodec;
        set
        {
            calls.VideoCodec = value;
            this.OnPropertyChanged();
        }
    }


    public void OnAppearing()
    {
        var over = chat.HostedOver;
        this.OpenOn = over == HubTransports.None
            ? "⚠️ Others can't reach you - check Bluetooth and Wi-Fi"
            : "Others can reach you over " + String.Join(" + ", new[]
            {
                over.HasFlag(HubTransports.Ble) ? "🔵 Bluetooth" : null,
                over.HasFlag(HubTransports.Http) ? "📶 Wi-Fi" : null
            }.Where(x => x != null));

        this.People.Clear();
        foreach (var conversation in chat.Conversations)
            this.Track(conversation);
        chat.Conversations.CollectionChanged += this.OnConversationsChanged;

        this.scanning = new CancellationTokenSource();
        _ = this.ScanLoop(this.scanning.Token);

        chat.Tunnel.PropertyChanged += this.OnTunnelChanged;
        this.OnTunnelChanged(null, new PropertyChangedEventArgs(null));
    }


    public void OnDisappearing()
    {
        this.scanning?.Cancel();
        this.scanning = null;
        chat.Conversations.CollectionChanged -= this.OnConversationsChanged;
        chat.Tunnel.PropertyChanged -= this.OnTunnelChanged;
        foreach (var person in this.People)
            person.Detach();
    }


    // ---------- the list ----------

    void OnConversationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => shell.MainThread.BeginInvokeOnMainThread(() =>
    {
        foreach (var conversation in chat.Conversations)
            this.Track(conversation);
    });


    void Track(Conversation conversation)
    {
        if (this.People.Any(x => x.Conversation == conversation))
            return;

        // someone already listed from discovery, now with a conversation
        var person = this.People.FirstOrDefault(x => x.Conversation == null && x.Host != null && Matches(conversation, x.Host));
        if (person == null)
        {
            person = this.NewPerson();
            this.People.Insert(0, person);
        }
        person.Conversation = conversation;
    }


    /// <summary>
    /// One scan for as long as the list is open - BLE and mDNS together. A host heard over Bluetooth is alive (Apple and
    /// Android keep reporting an advertiser); one found only over Wi-Fi is probed every few seconds, because mDNS holds
    /// on to a host that was killed or walked away long after it has gone. Anyone not seen for a while drops off.
    /// The scan pauses while connecting or in a call: blehubs stops scanning to connect, and the radio does better
    /// without a scan beside a call.
    /// </summary>
    async Task ScanLoop(CancellationToken ct)
    {
        IDisposable? scan = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var paused = this.reaching != null || calls.IsInCall;
                if (paused && scan != null)
                {
                    scan.Dispose();
                    scan = null;
                }
                else if (!paused && scan == null)
                {
                    scan = client.DiscoverAll().Subscribe(found => shell.MainThread.BeginInvokeOnMainThread(() => this.OnFound(found)));
                }

                if (!paused)
                    await this.CheckWifiOnly(ct);

                await Task.Delay(CheckEvery, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            scan?.Dispose();
        }
    }


    void OnFound(HubHostInfo found)
    {
        if (found.Transports == HubTransports.None)
            return;   // gone - it ages out like any other host

        // heard over Bluetooth just now: alive. Wi-Fi only: listed, but alive only once a probe says so
        this.OnSeen(found, found.Ble != null ? DateTimeOffset.UtcNow : null);
    }


    async Task CheckWifiOnly(CancellationToken ct)
    {
        var hosts = this.People.Where(x => x.Host is { Ble: null, Http: not null }).Select(x => x.Host!).ToList();
        var alive = await Task.WhenAll(hosts.Select(async host => (Host: host, Alive: await IsAlive(host, ct))));
        var now = DateTimeOffset.UtcNow;
        shell.MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (var (host, isAlive) in alive)
            {
                if (isAlive)
                    this.OnSeen(host, now);
            }
            this.ForgetStale(now);
        });
    }


    static async Task<bool> IsAlive(HubHostInfo host, CancellationToken ct)
    {
        if (host.Ble != null)
            return true;
        if (host.Http is not { } http)
            return false;

        // is anything listening where mDNS says it is?
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(TimeSpan.FromSeconds(1));
        foreach (var address in http.Addresses.Prepend(null))
        {
            try
            {
                using var tcp = new TcpClient();
                if (address == null)
                    await tcp.ConnectAsync(http.BaseAddress.Host, http.Port, probe.Token);
                else
                    await tcp.ConnectAsync(address, http.Port, probe.Token);
                return true;
            }
            catch
            {
                if (probe.IsCancellationRequested)
                    return false;
            }
        }
        return false;
    }


    void OnSeen(HubHostInfo found, DateTimeOffset? when)
    {
        var person = this.People.FirstOrDefault(x => x.Host?.Key == found.Key)
                     ?? this.People.FirstOrDefault(x => x.Host == null && x.Conversation != null && Matches(x.Conversation, found));

        if (person == null)
        {
            person = this.NewPerson();
            this.People.Add(person);
        }
        person.Host = found;
        if (when is { } seen)
            person.LastSeen = seen;
        else if (person.LastSeen == default)
            person.LastSeen = DateTimeOffset.UtcNow;   // a first mDNS sighting gets a probe's grace period
    }


    // someone not seen for a while is no longer here - unless they're connected to this device right now
    void ForgetStale(DateTimeOffset now)
    {
        foreach (var person in this.People.ToList())
        {
            if (person.Host == null || now - person.LastSeen < ForgetAfter)
                continue;

            person.Host = null;
            if (person.Conversation == null)
                this.People.Remove(person);
        }
    }


    // the chat this device is visiting is keyed by the host; someone visiting this device is keyed by their install id,
    // which is also the host id their own chat advertises over Wi-Fi
    static bool Matches(Conversation conversation, HubHostInfo host)
        => conversation.Id == "out:" + host.Key
           || (host.HostId != null && conversation.Id == "in:" + host.HostId);


    // ---------- actions ----------

    Person NewPerson()
    {
        Person? person = null;
        person = new Person
        {
            MessageCommand = new AsyncRelayCommand(() => this.Message(person!)),
            CallCommand = new AsyncRelayCommand(() => this.Call(person!)),
            VideoCommand = new AsyncRelayCommand(() => this.Video(person!))
        };
        return person;
    }


    Task Message(Person person) => this.Run(person, "Connecting", async conversation =>
    {
        chat.Selected = conversation;
        await shell.Navigator.NavigateToChat();
    });

    Task Call(Person person) => this.Run(person, "Calling", c => calls.Call(c, video: false));

    Task Video(Person person) => this.Run(person, "Calling", c => calls.Call(c, video: true));


    async Task Run(Person person, string doing, Func<Conversation, Task> action)
    {
        if (this.IsBusy)
            return;

        this.IsBusy = true;
        this.BusyText = $"{doing} {person.Name}...";
        using var reaching = this.reaching = new CancellationTokenSource();
        try
        {
            var conversation = await this.Reach(person, reaching.Token);
            this.IsBusy = false;
            await action(conversation);
        }
        catch (OperationCanceledException) when (reaching.IsCancellationRequested)
        {
            this.IsBusy = false;   // the Cancel button
        }
        catch (Exception ex)
        {
            this.IsBusy = false;
            await shell.Dialogs.Alert(person.Name, ex.Message);
        }
        finally
        {
            this.reaching = null;
        }
    }


    /// <summary>
    /// Cancel on the "Calling..." overlay - stops connecting to them
    /// </summary>
    [RelayCommand]
    void CancelReach() => this.reaching?.Cancel();


    /// <summary>
    /// A conversation that is connected now: the one they're in, or a fresh visit to their chat
    /// </summary>
    async Task<Conversation> Reach(Person person, CancellationToken cancellationToken)
    {
        if (person.Conversation is { IsOnline: true } connected
            && !(connected.IsVisiting && person.Host is { } current && chat.ShouldSwitchTransport(current)))
            return connected;

        if (person.Host is { } host)
        {
            var conversation = await chat.Visit(host, cancellationToken);
            person.Conversation = conversation;
            return conversation;
        }
        throw new InvalidOperationException("They aren't nearby or connected right now");
    }


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

        var person = this.NewPerson();
        person.Host = HubHostInfo.FromUrl(uri);
        person.LastSeen = DateTimeOffset.UtcNow;
        await this.Message(person);
    }


    [RelayCommand]
    Task VideoCheck() => shell.Navigator.NavigateToVideoCheck();


    [RelayCommand]
    void ToggleOptions() => this.ShowOptions = !this.ShowOptions;


    // ---------- sharing on the internet ----------

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
            _ => "Let people beyond this network message you (the game is never shared)"
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
}


/// <summary>
/// One person in the list: found nearby or on the network (<see cref="Host"/>), in a conversation (<see cref="Conversation"/>),
/// or both
/// </summary>
public partial class Person : ObservableObject
{
    Conversation? conversation;
    HubHostInfo? host;

    /// <summary>
    /// The row's buttons bind here rather than reaching up to the page's view model - an ancestor binding inside a
    /// CollectionView template doesn't resolve on every platform (the macOS AppKit backend)
    /// </summary>
    public required IAsyncRelayCommand MessageCommand { get; init; }
    public required IAsyncRelayCommand CallCommand { get; init; }
    public required IAsyncRelayCommand VideoCommand { get; init; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Initial { get; set; } = "?";
    [ObservableProperty] public partial string Reach { get; set; } = "";
    [ObservableProperty] public partial string LastText { get; set; } = "";
    [ObservableProperty] public partial bool HasLastText { get; set; }
    [ObservableProperty] public partial int Unread { get; set; }
    [ObservableProperty] public partial bool IsReachable { get; set; }

    /// <summary>
    /// The last scan that confirmed this host is there
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// Call buttons fade out for someone who can't be reached right now
    /// </summary>
    public double CallOpacity => this.IsReachable ? 1 : 0.35;

    partial void OnIsReachableChanged(bool value) => this.OnPropertyChanged(nameof(this.CallOpacity));


    public Conversation? Conversation
    {
        get => this.conversation;
        set
        {
            if (this.conversation != null)
                this.conversation.PropertyChanged -= this.OnConversationChanged;
            this.conversation = value;
            if (value != null)
                value.PropertyChanged += this.OnConversationChanged;
            this.Refresh();
        }
    }


    public HubHostInfo? Host
    {
        get => this.host;
        set
        {
            this.host = value;
            this.Refresh();
        }
    }


    public void Detach()
    {
        if (this.conversation != null)
            this.conversation.PropertyChanged -= this.OnConversationChanged;
    }


    void OnConversationChanged(object? sender, PropertyChangedEventArgs e) => this.Refresh();


    void Refresh()
    {
        this.Name = this.conversation?.Name ?? this.host?.Name ?? "Someone";
        this.Initial = this.Name.Length > 0 ? this.Name[..1].ToUpperInvariant() : "?";
        this.LastText = this.conversation?.LastText ?? "";
        this.HasLastText = this.LastText.Length > 0;
        this.Unread = this.conversation?.Unread ?? 0;

        var online = this.conversation?.IsOnline == true;
        this.IsReachable = online || this.host != null;
        this.Reach = online
            ? this.conversation!.Status
            : this.host is { } h
                ? String.Join("  ", new[]
                {
                    h.Http != null ? "📶 on this network" : null,
                    h.Ble != null ? "🔵 nearby" : null
                }.Where(x => x != null))
                : "offline";
    }
}
