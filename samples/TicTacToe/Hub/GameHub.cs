using Shiny.BluetoothLE.Hubs;
using TicTacToe.Game;
using TicTacToe.Services;

namespace TicTacToe.Hub;

/// <summary>
/// Runs on the host. A new instance handles every call, so game state lives in the GameEngine singleton.
/// </summary>
public class GameHub(GameEngine engine, GameSession session) : BleHub<IGameHub>
{
    public const string Spectators = "spectators";


    public async Task<JoinResult> Join(string playerName, string? avatarFile)
    {
        var mark = engine.Join(this.Context.ConnectionId, playerName, avatarFile);
        if (mark == Mark.None)
            await this.Groups.AddToGroupAsync(this.Context.ConnectionId, Spectators);

        // say hi to everyone already here (the host UI hears it through the session)
        var greeting = mark == Mark.None ? "👀 is watching" : "🎮 joined";
        await this.Clients.Others.Emote(playerName, greeting);
        session.ShowEmoteLocally(playerName, greeting);

        await session.BroadcastState();
        return new JoinResult(mark, engine.Snapshot());
    }


    public async Task<MoveResult> MakeMove(int cell)
    {
        var error = engine.TryMove(engine.GetMark(this.Context.ConnectionId), cell);
        if (error != null)
            return new MoveResult(false, error);

        await session.BroadcastState();
        return new MoveResult(true, null);
    }


    public Task Rematch()
    {
        if (engine.GetMark(this.Context.ConnectionId) == Mark.None)
            throw new InvalidOperationException("Spectators can't start a rematch");

        engine.Rematch();
        return session.BroadcastState();
    }


    public Task SendEmote(string emoji) => session.BroadcastEmote(this.Context.Client.Name ?? "?", emoji);


    public override Task OnConnectedAsync()
    {
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(string? reason)
    {
        if (engine.Leave(this.Context.ConnectionId))
            session.ShowEmoteLocally(this.Context.Client.Name ?? "Opponent", "🚪 left");

        await session.BroadcastState();
    }
}
