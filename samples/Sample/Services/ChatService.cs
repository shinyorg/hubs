using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Shiny.Actors;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Ssh;
using Shiny.Notifications;
using Shiny.UniversalHubs;
using Sample.Hub;

namespace Sample.Services;

/// <summary>
/// Both sides of the always-on chat. Each conversation's history is a ConversationActor (on disk), and the list of
/// conversations is the ChatDirectory actor - this service only keeps who is online right now.
/// Owner: every running app hosts a ChatHub; each visitor gets a conversation, answered through IHubContext&lt;ChatHub&gt;.
/// Visitor: messages someone else's chat through the generated IChatHub proxy - found nearby, on the network, or by the
/// tunnel address its owner shared.
/// </summary>
public class ChatService
{
    const string InstallIdProperty = "installId";

    readonly IActorSystem actors;
    readonly IHubContext<ChatHub> hub;
    readonly IUniversalHubHost host;
    readonly IUniversalHubClient<IChatHub> client;
    readonly BleHubHostOptions hostOptions;
    readonly INotificationManager notifications;
    readonly PlayerSettings settings;
    readonly QuickTunnel tunnel;
    readonly ILogger<ChatService> logger;
    Conversation? remote;
    int nextNotificationId = 1000;


    public ChatService(
        IActorSystem actors,
        IHubContext<ChatHub> hub,
        IUniversalHubHost host,
        IUniversalHubClient<IChatHub> client,
        BleHubHostOptions hostOptions,
        INotificationManager notifications,
        PlayerSettings settings,
        QuickTunnel tunnel,
        ILogger<ChatService> logger
    )
    {
        this.actors = actors;
        this.hub = hub;
        this.host = host;
        this.client = client;
        this.hostOptions = hostOptions;
        this.notifications = notifications;
        this.settings = settings;
        this.tunnel = tunnel;
        this.logger = logger;

        // the owner's answers arrive as pushes on the proxy
        client.Hub.MessageReceived += line =>
        {
            if (this.remote is { } conversation)
                _ = this.Add(conversation, line, fromPeer: true);
        };
        client.Disconnected += (_, disconnect) =>
        {
            if (this.remote is { } conversation)
                MainThread.BeginInvokeOnMainThread(() => conversation.SetOffline(disconnect));
        };
    }


    /// <summary>
    /// Everyone this device is talking to - visitors to its chat, and the chats it is visiting
    /// </summary>
    public ObservableCollection<Conversation> Conversations { get; } = new();

    /// <summary>
    /// The transports this device's chat is open on
    /// </summary>
    public HubTransports HostedOver => this.host.GetRunningTransports<ChatHub>();

    public QuickTunnel Tunnel => this.tunnel;

    /// <summary>
    /// The conversation on screen - it doesn't raise notifications
    /// </summary>
    public string? Viewing { get; set; }

    /// <summary>
    /// The conversation the chat page opens
    /// </summary>
    public Conversation? Selected { get; set; }

    public IConversationActor History(Conversation conversation) => this.actors.Get<IConversationActor>(conversation.Id);

    /// <summary>
    /// Every message added to a conversation, as it happens
    /// </summary>
    public IActorStream<ChatLine> Live(Conversation conversation) => this.actors.GetStream<ChatLine>(conversation.Id);

    public Conversation? Find(string id) => this.Conversations.FirstOrDefault(x => x.Id == id);


    /// <summary>
    /// Opens this device's chat for as long as the app runs - unlike the game, which only runs while hosting
    /// </summary>
    public async Task Start()
    {
        // conversations from earlier runs - offline until their people come back
        foreach (var info in await this.actors.Get<IChatDirectory>(ChatDirectory.Me).All())
        {
            if (this.Find(info.Id) != null)
                continue;

            var conversation = new Conversation(info.Id, info.Name, info.IsVisiting) { Status = "offline" };
            var lines = await this.History(conversation).Lines();
            conversation.LastText = lines.LastOrDefault()?.Text ?? "";
            this.Conversations.Add(conversation);
        }

        try
        {
            this.hostOptions.LocalName ??= ShortName(this.settings.Name);
            var over = await this.host.StartHub<ChatHub>(HubTransports.All);
            this.logger.LogInformation("Chat open over {Transports}", over);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Chat could not start");
        }

        // asked after the chat is open - the prompt can sit unanswered without keeping visitors out
        try
        {
            await this.notifications.RequestAccess(AccessRequestFlags.Notification);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Notifications are unavailable");
        }
    }


    /// <summary>
    /// Shares this device's chat on the internet. The game is never reachable this way - only ChatHub allows tunnels.
    /// Returns the public address, or null when the tunnel came up without learning one.
    /// </summary>
    public Task<string?> StartSharing(CancellationToken cancellationToken) => this.tunnel.StartAsync(cancellationToken);

    public Task StopSharing() => this.tunnel.StopAsync();


    // ---------- owner ----------

    public async Task OnVisitorConnected(HubConnection connection)
    {
        // the same visitor keeps one conversation across reconnects and transports
        var key = connection.Client.Properties.TryGetValue(InstallIdProperty, out var installId) ? installId : connection.Id;
        var id = "in:" + key;
        connection.Properties["conversation"] = id;

        var name = connection.Client.Name ?? "Visitor";
        await this.actors.Get<IConversationActor>(id).Open(name, isVisiting: false);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var conversation = this.Find(id);
            if (conversation == null)
            {
                conversation = new Conversation(id, name, isVisiting: false);
                this.Conversations.Insert(0, conversation);
            }
            conversation.Name = name;
            conversation.SetOnline(connection.Id, connection.Transport);
        });
    }


    public async Task<ChatLine> OnVisitorMessage(HubConnection connection, string text, string clientMessageId)
    {
        var id = (string)connection.Properties["conversation"];
        var line = new ChatLine(Guid.NewGuid().ToString("N"), clientMessageId, false, connection.Client.Name ?? "Visitor", text, DateTimeOffset.Now);
        var conversation = this.Find(id) ?? throw new InvalidOperationException("Not in a conversation");
        await this.Add(conversation, line, fromPeer: true);
        return line;
    }


    public void OnVisitorDisconnected(HubConnection connection, HubDisconnect disconnect)
    {
        if (connection.Properties.TryGetValue("conversation", out var id))
            MainThread.BeginInvokeOnMainThread(() => this.Find((string)id)?.SetOffline(disconnect));
    }


    // ---------- visitor ----------

    /// <summary>
    /// Opens a conversation with someone else's chat. One at a time: visiting another one leaves the current one.
    /// </summary>
    public async Task<Conversation> Visit(HubHostInfo info)
    {
        if (this.client.Status != BleHubClientStatus.Disconnected)
            await this.client.Disconnect();

        await this.client.Connect(info, new HubConnectOptions(
            this.settings.Name,
            AppInfo.Current.VersionString,
            new Dictionary<string, string> { [InstallIdProperty] = PlayerSettings.InstallId }
        ));

        var id = "out:" + info.Key;
        var name = this.client.HostName ?? info.Name;
        await this.actors.Get<IConversationActor>(id).Open(name, isVisiting: true);

        var conversation = this.Find(id);
        if (conversation == null)
        {
            conversation = new Conversation(id, name, isVisiting: true);
            this.Conversations.Insert(0, conversation);
        }
        conversation.SetOnline(null, this.client.Transport);
        this.remote = conversation;
        return conversation;
    }


    // ---------- both ----------

    /// <summary>
    /// Sends from this device: to the owner when visiting, to the visitor when owning
    /// </summary>
    public async Task<ChatLine> Send(Conversation conversation, string text, string clientMessageId)
    {
        ChatLine line;
        if (conversation.IsVisiting)
        {
            if (!ReferenceEquals(this.remote, conversation) || this.client.Status != BleHubClientStatus.Connected)
                throw new InvalidOperationException($"Not connected to {conversation.Name}");

            line = await this.client.Hub.Send(text, clientMessageId);
        }
        else
        {
            var connectionId = conversation.ConnectionId ?? throw new InvalidOperationException($"{conversation.Name} has left");
            line = new ChatLine(Guid.NewGuid().ToString("N"), clientMessageId, true, this.settings.Name, text, DateTimeOffset.Now);
            await this.hub.Clients.Client(connectionId).MessageReceived(line);
        }

        await this.Add(conversation, line, fromPeer: false);
        return line;
    }


    async Task Add(Conversation conversation, ChatLine line, bool fromPeer)
    {
        // the actor stores it and publishes it to anyone watching the conversation
        if (await this.History(conversation).Add(line) == null)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            conversation.LastText = line.Text;
            if (fromPeer && this.Viewing != conversation.Id)
            {
                conversation.Unread++;
                _ = this.Notify(conversation, line);
            }
        });
    }


    async Task Notify(Conversation conversation, ChatLine line)
    {
        try
        {
            await this.notifications.Send(new Notification
            {
                Id = Interlocked.Increment(ref this.nextNotificationId),
                Title = conversation.Name,
                Message = line.Text,
                Thread = conversation.Id
            });
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Could not show a notification");
        }
    }


    static string ShortName(string name) => name.Length > 8 ? name[..8] : name;
}


/// <summary>
/// One person this device is talking to
/// </summary>
public partial class Conversation(string id, string name, bool isVisiting) : ObservableObject
{
    public string Id { get; } = id;

    /// <summary>
    /// True when this device is the visitor - the other side owns the chat
    /// </summary>
    public bool IsVisiting { get; } = isVisiting;

    [ObservableProperty] public partial string Name { get; set; } = name;
    [ObservableProperty] public partial bool IsOnline { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string LastText { get; set; } = "";
    [ObservableProperty] public partial int Unread { get; set; }

    /// <summary>
    /// The visitor's hub connection, while connected (owner side)
    /// </summary>
    public string? ConnectionId { get; private set; }

    public event EventHandler? OnlineChanged;

    /// <summary>
    /// Whether this device wrote the line
    /// </summary>
    public bool IsMine(ChatLine line) => this.IsVisiting ? !line.FromHost : line.FromHost;


    public void SetOnline(string? connectionId, HubTransports transport)
    {
        this.ConnectionId = connectionId;
        this.IsOnline = true;
        this.Status = transport switch
        {
            HubTransports.Ble => "🔵 here over Bluetooth",
            HubTransports.Http => "📶 here over Wi-Fi / internet",
            _ => "here"
        };
        this.OnlineChanged?.Invoke(this, EventArgs.Empty);
    }


    public void SetOffline(HubDisconnect disconnect)
    {
        this.ConnectionId = null;
        this.IsOnline = false;
        this.Status = disconnect.Reason switch
        {
            HubDisconnectReason.ClientTimeout => "📡 lost connection",
            HubDisconnectReason.ServerShutdown => "💤 chat closed",
            HubDisconnectReason.ServerDisconnect => "🚫 removed",
            _ => "👋 left"
        };
        this.OnlineChanged?.Invoke(this, EventArgs.Empty);
    }
}
