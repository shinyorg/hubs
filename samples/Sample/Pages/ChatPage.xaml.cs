using Shiny.Maui.Controls.Chat;
using Sample.Services;

namespace Sample.Pages;

public partial class ChatPage : ContentPage
{
    public ChatPage()
    {
        this.InitializeComponent();
        this.Chat.InputActions!.Add(new ChatInputAction
        {
            Text = "📎 Send a file",
            Handler = _ => ((ChatViewModel)this.BindingContext).SendFile()
        });
    }


    // photos open in ChatView's image viewer on their own; this opens the other files
    async void OnMessageTapped(object? sender, ChatMessage message)
    {
        if (message.ImageUrl != null || ChatSessionProvider.FileFor(message) is not { } path)
            return;

        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest(Path.GetFileName(path), new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            await this.DisplayAlertAsync("Open", ex.Message, "OK");
        }
    }
}
