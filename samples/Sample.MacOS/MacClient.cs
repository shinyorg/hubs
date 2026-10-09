using Shiny.BluetoothLE.Hubs;
using Shiny.UniversalHubs;
using Sample.Game;
using Sample.Hub;

namespace Sample.MacOS;

/// <summary>
/// The Mac's side of both hubs: a visitor to someone's chat, and a player (or spectator) in someone's game
/// </summary>
public class MacClient
{
    readonly List<ChatLine> lines = new();

    public MacClient(IUniversalHubClient<IChatHub> chat, IUniversalHubClient<IGameHub> game)
    {
        this.Chat = chat;
        this.Game = game;

        chat.Hub.MessageReceived += line => this.OnLine(line);
        chat.Disconnected += (_, d) => this.ChatClosed?.Invoke(this, d);
        game.Hub.StateChanged += state =>
        {
            this.State = state;
            this.StateChanged?.Invoke(this, state);
        };
        game.Hub.Emote += (from, emoji) => this.Emoted?.Invoke(this, $"{from} {emoji}");
        game.Disconnected += (_, d) => this.GameClosed?.Invoke(this, d);
    }


    public IUniversalHubClient<IChatHub> Chat { get; }
    public IUniversalHubClient<IGameHub> Game { get; }

    public string Name
    {
        get => Preferences.Get(nameof(this.Name), Environment.UserName);
        set => Preferences.Set(nameof(this.Name), value);
    }

    static string InstallId
    {
        get
        {
            var id = Preferences.Get(nameof(InstallId), null);
            if (id == null)
            {
                id = Guid.NewGuid().ToString("N");
                Preferences.Set(nameof(InstallId), id);
            }
            return id;
        }
    }


    // ---------- chat ----------

    public string? ChatWith { get; private set; }
    public IReadOnlyList<ChatLine> Lines { get { lock (this.lines) return this.lines.ToList(); } }
    public event EventHandler<ChatLine>? LineAdded;
    public event EventHandler<HubDisconnect>? ChatClosed;


    public async Task Visit(HubHostInfo host)
    {
        if (this.Chat.Status != BleHubClientStatus.Disconnected)
            await this.Chat.Disconnect();

        lock (this.lines)
            this.lines.Clear();

        await this.Chat.Connect(host, new HubConnectOptions(this.Name, AppInfo.Current.VersionString, new Dictionary<string, string> { ["installId"] = InstallId })
        {
            Allowed = HubTransports.Http
        });
        this.ChatWith = this.Chat.HostName ?? host.Name;
    }


    public async Task<ChatLine> Say(string text, string clientMessageId)
    {
        var line = await this.Chat.Hub.Send(text, clientMessageId);
        this.OnLine(line);
        return line;
    }


    void OnLine(ChatLine line)
    {
        lock (this.lines)
        {
            if (this.lines.Any(x => x.Id == line.Id))
                return;
            this.lines.Add(line);
        }
        this.LineAdded?.Invoke(this, line);
    }


    // ---------- game ----------

    public Mark MyMark { get; private set; }
    public GameState? State { get; private set; }
    public event EventHandler<GameState>? StateChanged;
    public event EventHandler<string>? Emoted;
    public event EventHandler<HubDisconnect>? GameClosed;


    public async Task Join(HubHostInfo host)
    {
        if (this.Game.Status != BleHubClientStatus.Disconnected)
            await this.Game.Disconnect();

        await this.Game.Connect(host, new HubConnectOptions(this.Name, AppInfo.Current.VersionString) { Allowed = HubTransports.Http });
        var result = await this.Game.Hub.Join(this.Name, null);
        this.MyMark = result.YouAre;
        this.State = result.State;
    }
}
