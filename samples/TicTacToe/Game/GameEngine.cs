namespace TicTacToe.Game;

/// <summary>
/// Host side game rules. The host is always X, the first client to join is O and everyone after that spectates.
/// </summary>
public class GameEngine
{
    static readonly int[][] Lines =
    [
        [0, 1, 2], [3, 4, 5], [6, 7, 8],
        [0, 3, 6], [1, 4, 7], [2, 5, 8],
        [0, 4, 8], [2, 4, 6]
    ];

    readonly Lock sync = new();
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


    public void NewSession(string hostName, string? hostAvatar)
    {
        lock (this.sync)
        {
            this.xPlayer = hostName;
            this.xAvatar = hostAvatar;
            this.oPlayer = null;
            this.oAvatar = null;
            this.oClientId = null;
            this.spectators.Clear();
            this.xWins = this.oWins = this.draws = 0;
            this.starter = Mark.X;
            this.ResetBoard();
        }
    }


    /// <summary>
    /// Seats a client - returns O for the opponent or None for a spectator
    /// </summary>
    public Mark Join(string clientId, string playerName, string? avatar)
    {
        lock (this.sync)
        {
            if (this.oClientId == clientId)
                return Mark.O;

            if (this.oClientId == null)
            {
                this.oClientId = clientId;
                this.oPlayer = playerName;
                this.oAvatar = avatar;
                this.xWins = this.oWins = this.draws = 0;
                this.starter = Mark.X;
                this.ResetBoard();
                return Mark.O;
            }

            this.spectators.Add(clientId);
            return Mark.None;
        }
    }


    /// <summary>
    /// Removes a client. Returns true when the opponent left (the seat opens and the board resets).
    /// </summary>
    public bool Leave(string clientId)
    {
        lock (this.sync)
        {
            if (this.oClientId != clientId)
            {
                this.spectators.Remove(clientId);
                return false;
            }

            this.oClientId = null;
            this.oPlayer = null;
            this.oAvatar = null;
            this.ResetBoard();
            return true;
        }
    }


    public Mark GetMark(string? clientId)
    {
        // a null client is a local call on the host
        if (clientId == null)
            return Mark.X;

        lock (this.sync)
            return this.oClientId == clientId ? Mark.O : Mark.None;
    }


    public string? TryMove(Mark player, int cell)
    {
        lock (this.sync)
        {
            if (player == Mark.None)
                return "Spectators can't play";

            if (this.oClientId == null)
                return "Waiting for an opponent";

            if (this.winner != Mark.None || this.isDraw)
                return "The game is over - start a rematch";

            if (this.turn != player)
                return "It's not your turn";

            if (cell is < 0 or > 8)
                return "That square doesn't exist";

            if (this.board[cell] != Mark.None)
                return "That square is taken";

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
            return null;
        }
    }


    public void Rematch()
    {
        lock (this.sync)
        {
            // players take turns going first
            this.starter = Other(this.starter);
            this.ResetBoard();
        }
    }


    public GameState Snapshot()
    {
        lock (this.sync)
        {
            return new GameState(
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
            );
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
