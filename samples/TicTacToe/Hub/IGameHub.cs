using System.Text.Json.Serialization;
using Shiny.BluetoothLE.Hubs;
using TicTacToe.Game;

namespace TicTacToe.Hub;

/// <summary>
/// The whole conversation between a client and the host. The source generator turns this into GameHubClient (the client
/// proxy) and typed pushes for the hub (Clients.All.StateChanged(...)).
/// </summary>
[BleHubClient]
public interface IGameHub
{
    // client -> host
    Task<JoinResult> Join(string playerName, string? avatarFile);
    Task<MoveResult> MakeMove(int cell);
    Task Rematch();
    Task SendEmote(string emoji);

    // host -> clients
    event Action<GameState> StateChanged;
    event Action<string, string> Emote;
}


public record JoinResult(Mark YouAre, GameState State);
public record MoveResult(bool Accepted, string? Error);


/// <summary>
/// Everything that crosses the wire, for AOT friendly JSON
/// </summary>
[JsonSerializable(typeof(JoinResult))]
[JsonSerializable(typeof(MoveResult))]
[JsonSerializable(typeof(GameState))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
public partial class GameJsonContext : JsonSerializerContext;
