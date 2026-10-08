namespace TicTacToe.Game;

public enum Mark
{
    None,
    X,
    O
}


/// <summary>
/// Everything a device needs to draw the game. The host owns the truth and broadcasts this after every change.
/// </summary>
public record GameState(
    Mark[] Board,
    Mark Turn,
    Mark Winner,
    bool IsDraw,
    int[]? WinningLine,
    string XPlayer,
    string? OPlayer,
    string? XAvatar,
    string? OAvatar,
    int XWins,
    int OWins,
    int Draws,
    int Spectators
)
{
    public bool IsOver => this.Winner != Mark.None || this.IsDraw;
    public bool HasOpponent => this.OPlayer != null;
}
