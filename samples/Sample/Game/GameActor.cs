using Shiny.Actors;

namespace Sample.Game;

/// <summary>
/// One hosted game. Every game gets its own actor (its id is the game's), so games never share state, and calls run one
/// at a time - the rules need no locks. The host is always X, the first client to join is O and everyone after that
/// spectates.
/// </summary>
public interface IGameActor : IActor
{
    Task Start(string hostName, string? hostAvatar);

    /// <summary>
    /// Seats a client - returns O for the opponent or None for a spectator
    /// </summary>
    Task<Mark> Join(string clientId, string playerName, string? avatar);

    /// <summary>
    /// Removes a client. Returns true when the opponent left (the seat opens and the board resets).
    /// </summary>
    Task<bool> Leave(string clientId);

    /// <summary>
    /// A null client is the host itself
    /// </summary>
    Task<Mark> GetMark(string? clientId);

    /// <summary>
    /// Plays for a client (null = the host). Returns why the move was refused, or null.
    /// </summary>
    Task<string?> Move(string? clientId, int cell);

    Task Rematch();
    Task<GameState> Snapshot();
}


public class GameActor : Actor, IGameActor
{
    static readonly int[][] Lines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [0, 4, 8], [2, 4, 6]
    ];

    readonly HashSet<string> spectators = new();
    Mark[] board = new Mark[9];
    Mark turn = Mark.X;
    Mark starter = Mark.X;
    Mark winner = Mark.None;
    int[]? winningLine;
    bool isDraw;
    string xPlayer = "Host";
    string? xAvatar;
    string? oPlayer;
    string? oAvatar;
    string? oClientId;
    int xWins, oWins, draws;


    public Task Start(string hostName, string? hostAvatar)
    {
        this.xPlayer = hostName;
        this.xAvatar = hostAvatar;
        this.ResetBoard();
        return Task.CompletedTask;
    }


    public Task<Mark> Join(string clientId, string playerName, string? avatar)
    {
        if (this.oClientId == clientId)
            return Task.FromResult(Mark.O);

        if (this.oClientId == null)
        {
            this.oClientId = clientId;
            this.oPlayer = playerName;
            this.oAvatar = avatar;
            this.xWins = this.oWins = this.draws = 0;
            this.starter = Mark.X;
            this.ResetBoard();
            return Task.FromResult(Mark.O);
        }

        this.spectators.Add(clientId);
        return Task.FromResult(Mark.None);
    }


    public Task<bool> Leave(string clientId)
    {
        if (this.oClientId != clientId)
        {
            this.spectators.Remove(clientId);
            return Task.FromResult(false);
        }

        this.oClientId = null;
        this.oPlayer = null;
        this.oAvatar = null;
        this.ResetBoard();
        return Task.FromResult(true);
    }


    public Task<Mark> GetMark(string? clientId) => Task.FromResult(this.MarkOf(clientId));


    public Task<string?> Move(string? clientId, int cell)
    {
        var player = this.MarkOf(clientId);
        string? error = null;

        if (player == Mark.None)
            error = "Spectators can't play";
        else if (this.oClientId == null)
            error = "Waiting for an opponent";
        else if (this.winner != Mark.None || this.isDraw)
            error = "The game is over - start a rematch";
        else if (this.turn != player)
            error = "It's not your turn";
        else if (cell is < 0 or > 8)
            error = "That square doesn't exist";
        else if (this.board[cell] != Mark.None)
            error = "That square is taken";

        if (error == null)
            this.Play(player, cell);

        return Task.FromResult(error);
    }


    public Task Rematch()
    {
        // players take turns going first
        this.starter = Other(this.starter);
        this.ResetBoard();
        return Task.CompletedTask;
    }


    public Task<GameState> Snapshot() => Task.FromResult(new GameState(
        (Mark[])this.board.Clone(),
        this.turn,
        this.winner,
        this.isDraw,
        this.winningLine,
        this.xPlayer,
        this.oPlayer,
        this.xAvatar,
        this.oAvatar,
        this.xWins,
        this.oWins,
        this.draws,
        this.spectators.Count
    ));


    Mark MarkOf(string? clientId) => clientId == null ? Mark.X : this.oClientId == clientId ? Mark.O : Mark.None;


    void Play(Mark player, int cell)
    {
        this.board[cell] = player;
        var line = Lines.FirstOrDefault(l => l.All(i => this.board[i] == player));
        if (line != null)
        {
            this.winner = player;
            this.winningLine = line;
            if (player == Mark.X) this.xWins++; else this.oWins++;
        }
        else if (this.board.All(x => x != Mark.None))
        {
            this.isDraw = true;
            this.draws++;
        }
        else
        {
            this.turn = Other(player);
        }
    }


    void ResetBoard()
    {
        this.board = new Mark[9];
        this.turn = this.starter;
        this.winner = Mark.None;
        this.winningLine = null;
        this.isDraw = false;
    }


    static Mark Other(Mark mark) => mark == Mark.X ? Mark.O : Mark.X;
}
