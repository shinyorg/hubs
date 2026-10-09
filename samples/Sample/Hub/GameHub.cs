using Shiny.BluetoothLE.Hubs;
using Shiny.UniversalHubs;
using Sample.Game;
using Sample.Services;

namespace Sample.Hub;

/// <summary>
/// Runs on the host. A new instance handles every call; the game itself is the current game's GameActor.
/// </summary>
public class GameHub(GameSession session) : UniversalHub<IGameHub>
{
    public const string Spectators = "spectators";


    public async Task<JoinResult> Join(string playerName, string? avatarFile)
    {
        var game = session.Game;
        var mark = await game.Join(this.Context.ConnectionId, playerName, avatarFile);
        if (mark == Mark.None)
            await this.Groups.AddToGroupAsync(this.Context.ConnectionId, Spectators);

        // say hi to everyone already here (the host UI hears it through the session)
        var greeting = mark == Mark.None ? "👀 is watching" : "🎮 joined";
        await this.Clients.Others.Emote(playerName, greeting);
        session.ShowEmoteLocally(playerName, greeting);

        await session.BroadcastState();
        return new JoinResult(mark, await game.Snapshot());
    }


    public async Task<MoveResult> MakeMove(int cell)
    {
        var error = await session.Game.Move(this.Context.ConnectionId, cell);
        if (error != null)
            return new MoveResult(false, error);

        await session.BroadcastState();
        return new MoveResult(true, null);
    }


    public async Task Rematch()
    {
        var game = session.Game;
        if (await game.GetMark(this.Context.ConnectionId) == Mark.None)
            throw new InvalidOperationException("Spectators can't start a rematch");

        await game.Rematch();
        await session.BroadcastState();
    }


    public Task SendEmote(string emoji) => session.BroadcastEmote(this.Context.Client.Name ?? "?", emoji);


    public override async Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect)
    {
        if (session.IsHosting && await session.Game.Leave(connection.Id))
            session.ShowEmoteLocally(connection.Client.Name ?? "Opponent", disconnect.Reason == HubDisconnectReason.ClientTimeout ? "📡 lost connection" : "🚪 left");

        await session.BroadcastState();
    }
}
