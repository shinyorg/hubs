using Shiny.BluetoothLE.Hubs;
using Sample.Game;

namespace Sample.MacOS;

/// <summary>
/// A seat (or a spectator's view) in someone's game. Moves are hub calls; the board arrives as hub pushes.
/// </summary>
public class GamePage : ContentPage
{
    readonly MacClient client;
    readonly Button[] cells = new Button[9];
    readonly Label status = new() { FontSize = 18, HorizontalOptions = LayoutOptions.Center };
    readonly Label players = new() { HorizontalOptions = LayoutOptions.Center };
    readonly Label emote = new() { HorizontalOptions = LayoutOptions.Center };
    readonly Button rematch = new() { Text = "Rematch" };


    public GamePage(MacClient client)
    {
        this.client = client;
        this.Title = "Tic Tac Toe";

        var board = new Grid { RowSpacing = 6, ColumnSpacing = 6, WidthRequest = 330, HeightRequest = 330, HorizontalOptions = LayoutOptions.Center };
        for (var i = 0; i < 3; i++)
        {
            board.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            board.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        for (var i = 0; i < 9; i++)
        {
            var cell = i;
            var button = new Button { FontSize = 44, FontAttributes = FontAttributes.Bold };
            button.Clicked += async (_, _) => await this.Move(cell);
            Grid.SetRow(button, i / 3);
            Grid.SetColumn(button, i % 3);
            board.Children.Add(button);
            this.cells[i] = button;
        }
        this.rematch.Clicked += async (_, _) => await this.Try(() => client.Game.Hub.Rematch());

        this.Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 16,
            Children = { this.players, this.status, board, this.emote, this.rematch }
        };

        client.StateChanged += this.OnState;
        client.Emoted += this.OnEmote;
        client.GameClosed += this.OnClosed;
        if (client.State is { } state)
            this.Draw(state);
    }


    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        this.client.StateChanged -= this.OnState;
        this.client.Emoted -= this.OnEmote;
        this.client.GameClosed -= this.OnClosed;
        if (this.client.Game.Status == BleHubClientStatus.Connected)
            await this.client.Game.Disconnect();
    }


    void OnState(object? sender, GameState state) => this.Dispatcher.Dispatch(() => this.Draw(state));
    void OnEmote(object? sender, string text) => this.Dispatcher.Dispatch(() => this.emote.Text = text);
    void OnClosed(object? sender, HubDisconnect disconnect) => this.Dispatcher.Dispatch(() => this.status.Text = "Game over: " + disconnect.Description);


    void Draw(GameState state)
    {
        for (var i = 0; i < 9; i++)
        {
            this.cells[i].Text = state.Board[i] == Mark.None ? "" : state.Board[i].ToString();
            this.cells[i].BackgroundColor = state.WinningLine?.Contains(i) == true ? Color.FromArgb("#FFD54F") : null;
        }

        this.players.Text = $"❌ {state.XPlayer} ({state.XWins})   vs   ⭕ {state.OPlayer ?? "..."} ({state.OWins})   draws {state.Draws}";
        var me = this.client.MyMark;
        this.status.Text = me == Mark.None ? "👀 Spectating"
            : state.Winner != Mark.None ? (state.Winner == me ? "🎉 You win!" : "You lose")
            : state.IsDraw ? "Draw"
            : state.Turn == me ? "Your turn" : "Their turn";
        this.rematch.IsVisible = me != Mark.None && state.IsOver;
    }


    Task Move(int cell) => this.Try(async () =>
    {
        var result = await this.client.Game.Hub.MakeMove(cell);
        if (!result.Accepted)
            this.status.Text = result.Error;
    });


    async Task Try(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            this.status.Text = ex.Message;
        }
    }
}
