using Microsoft.Extensions.Logging;
using Shiny.Actors;
using Shiny.BluetoothLE.Hubs;
using Shiny.UniversalHubs;
using Sample.Game;
using Sample.Hub;

namespace Sample.Services;

/// <summary>
/// Hides whether this device is the host or a client from the UI - and which transport the game runs over.
/// The host plays through the game's GameActor directly and pushes to clients with IHubContext&lt;GameHub&gt; (BLE and
/// Wi-Fi clients alike); a client plays through the generated IGameHub proxy, over Wi-Fi or BLE. Every hosted game is its
/// own actor, so a new game never inherits the last one's state.
/// </summary>
public class GameSession
{
    public const string HostAvatarFile = "avatar-host.jpg";

    readonly BleHubHostOptions hostOptions;
    readonly IHubContext<GameHub> hub;
    readonly IUniversalHubHost host;
    readonly IUniversalHubClient<IGameHub> client;
    readonly IActorSystem actors;
    string? gameId;
    readonly ILogger<GameSession> logger;


    public GameSession(
        BleHubHostOptions hostOptions,
        IHubContext<GameHub> hub,
        IUniversalHubHost host,
        IUniversalHubClient<IGameHub> client,
        IActorSystem actors,
        ILogger<GameSession> logger
    )
    {
        this.hostOptions = hostOptions;
        this.hub = hub;
        this.host = host;
        this.client = client;
        this.actors = actors;
        this.logger = logger;
    }


    public bool IsActive { get; private set; }

    /// <summary>
    /// The game this device is hosting
    /// </summary>
    public IGameActor Game => this.actors.Get<IGameActor>(this.gameId ?? throw new InvalidOperationException("Not hosting a game"));
    public bool IsHosting => this.IsHost && this.gameId != null;
    public bool IsHost { get; private set; }
    public Mark MyMark { get; private set; }
    public GameState? State { get; private set; }

    /// <summary>
    /// Host: what it is hosting over. Client: what it is connected over.
    /// </summary>
    public HubTransports Transports { get; private set; }

    public event EventHandler<GameState>? StateChanged;
    public event EventHandler<(string From, string Emoji)>? EmoteReceived;

    /// <summary>
    /// A client's session ended - the host left, removed us or the connection dropped
    /// </summary>
    public event EventHandler<string?>? Ended;


    /// <summary>
    /// Where the host serves files from (and where client uploads land)
    /// </summary>
    public static string HostFilesDirectory => Path.Combine(FileSystem.AppDataDirectory, "host-files");

    static string ClientCacheDirectory => Path.Combine(FileSystem.CacheDirectory, "remote-avatars");


    // ---------- host ----------

    public async Task StartHosting(string playerName, string? avatarPath)
    {
        Directory.CreateDirectory(HostFilesDirectory);
        var hostAvatar = Path.Combine(HostFilesDirectory, HostAvatarFile);
        if (avatarPath != null && File.Exists(avatarPath))
            File.Copy(avatarPath, hostAvatar, true);
        else if (File.Exists(hostAvatar))
            File.Delete(hostAvatar);

        // the advertisement only has room for a short name next to a 128-bit service UUID
        this.hostOptions.LocalName = playerName.Length > 8 ? playerName[..8] : playerName;
        this.gameId = Guid.NewGuid().ToString("N");
        await this.Game.Start(playerName, avatarPath == null ? null : HostAvatarFile);

        // BLE for nearby phones (on a plane), Wi-Fi for anyone on the same network (at home) - either one is enough.
        // Only the game starts: the chat is already running and stays running.
        this.Transports = await this.host.StartHub<GameHub>(HubTransports.All);

        this.IsHost = true;
        this.IsActive = true;
        this.MyMark = Mark.X;
        this.State = await this.Game.Snapshot();
    }


    /// <summary>
    /// Host only - pushes the current board to every client and to the host's own UI
    /// </summary>
    public async Task BroadcastState()
    {
        var state = await this.Game.Snapshot();
        this.State = state;
        this.StateChanged?.Invoke(this, state);
        await this.hub.Clients.All.StateChanged(state);
    }


    public async Task BroadcastEmote(string from, string emoji)
    {
        this.ShowEmoteLocally(from, emoji);
        await this.hub.Clients.All.Emote(from, emoji);
    }


    public void ShowEmoteLocally(string from, string emoji) => this.EmoteReceived?.Invoke(this, (from, emoji));


    /// <summary>
    /// Disconnects everyone in the spectators group - shows off host initiated disconnects from outside a hub
    /// </summary>
    public async Task<int> KickSpectators()
    {
        var spectators = this.hub.Groups.GetMembers(GameHub.Spectators);
        foreach (var id in spectators)
            await this.hub.Disconnect(id, $"{this.State?.XPlayer} removed spectators");

        return spectators.Count;
    }


    // ---------- client pushes ----------

    /// <summary>
    /// Starts relaying pushes from the host - call from the game page's OnAppearing
    /// </summary>
    public void Attach()
    {
        this.Detach(); // guard against a double attach
        this.client.Hub.StateChanged += this.OnHubStateChanged;
        this.client.Hub.Emote += this.OnHubEmote;
        this.client.Disconnected += this.OnClientDisconnected;
    }


    /// <summary>
    /// Stops relaying pushes from the host - call from the game page's OnDisappearing
    /// </summary>
    public void Detach()
    {
        this.client.Hub.StateChanged -= this.OnHubStateChanged;
        this.client.Hub.Emote -= this.OnHubEmote;
        this.client.Disconnected -= this.OnClientDisconnected;
    }


    // pushes from the host arrive as plain .NET events on the generated proxy
    void OnHubStateChanged(GameState state)
    {
        this.State = state;
        this.StateChanged?.Invoke(this, state);
    }

    void OnHubEmote(string from, string emoji) => this.EmoteReceived?.Invoke(this, (from, emoji));

    void OnClientDisconnected(object? sender, HubDisconnect reason)
    {
        if (!this.IsHost && this.IsActive)
        {
            this.IsActive = false;
            this.Ended?.Invoke(this, reason.Description);
        }
    }


    // ---------- client ----------

    public async Task JoinHost(HubHostInfo hostInfo, string playerName, string? avatarPath, bool preferBluetooth)
    {
        // pushes arrive before Join returns - they must not be read as this device's last role (a past host would look
        // up the remote host's avatar in its own files)
        this.IsHost = false;
        this.MyMark = Mark.None;
        this.State = null;

        // every host serves its avatar as avatar-host.jpg - don't show the last host's
        if (Directory.Exists(ClientCacheDirectory))
            Directory.Delete(ClientCacheDirectory, true);

        await this.client.Connect(hostInfo, new HubConnectOptions(playerName, AppInfo.Current.VersionString)
        {
            Prefer = preferBluetooth ? HubTransports.Ble : HubTransports.Http
        });
        this.Transports = this.client.Transport;
        try
        {
            string? avatarFile = null;
            if (avatarPath != null && File.Exists(avatarPath) && this.client.CanTransferFiles)
            {
                try
                {
                    // a stalled transfer must not hold up joining the game
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    avatarFile = $"avatar-{Guid.NewGuid():N}.jpg";
                    await this.client.UploadFile(avatarPath, avatarFile, cancellationToken: cts.Token);
                }
                catch (Exception ex)
                {
                    // the game doesn't need an avatar
                    this.logger.LogWarning(ex, "Avatar upload failed");
                    avatarFile = null;
                }
            }

            var result = await this.client.Hub.Join(playerName, avatarFile);
            this.IsHost = false;
            this.IsActive = true;
            this.MyMark = result.YouAre;
            this.State = result.State;
        }
        catch
        {
            await this.client.Disconnect();
            throw;
        }
    }


    // ---------- both ----------

    public async Task<MoveResult> MakeMove(int cell)
    {
        if (!this.IsHost)
            return await this.client.Hub.MakeMove(cell);

        var error = await this.Game.Move(null, cell);
        if (error == null)
            await this.BroadcastState();

        return new MoveResult(error == null, error);
    }


    public async Task Rematch()
    {
        if (!this.IsHost)
        {
            await this.client.Hub.Rematch();
            return;
        }
        await this.Game.Rematch();
        await this.BroadcastState();
    }


    public async Task Emote(string emoji)
    {
        if (this.IsHost)
            await this.BroadcastEmote((await this.Game.Snapshot()).XPlayer, emoji);
        else
            await this.client.Hub.SendEmote(emoji);
    }


    public async Task Leave()
    {
        this.IsActive = false;
        if (this.IsHost)
        {
            await this.host.StopHub<GameHub>("The host ended the game");
            this.gameId = null;
        }
        else
            await this.client.Disconnect();
    }


    /// <summary>
    /// Resolves an avatar file name from the game state to a local path - downloading it from the host (L2CAP or HTTP) when needed
    /// </summary>
    public async Task<string?> GetAvatar(string? file)
    {
        if (file == null)
            return null;

        if (this.IsHost)
        {
            var hostPath = Path.Combine(HostFilesDirectory, file);
            return File.Exists(hostPath) ? hostPath : null;
        }

        var local = Path.Combine(ClientCacheDirectory, file);
        if (File.Exists(local))
            return local;

        if (!this.client.CanTransferFiles)
            return null;

        try
        {
            Directory.CreateDirectory(ClientCacheDirectory);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await this.client.DownloadFile(file, local, cancellationToken: cts.Token);
            return local;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Could not download avatar {File}", file);
            return null;
        }
    }
}
