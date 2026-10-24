using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.Maui.Controls.Chat;
using Sample.Services;

namespace Sample.Pages;

/// <summary>
/// One conversation, drawn by Shiny.Maui.Controls' ChatView through ChatSessionProvider
/// </summary>
[ShellMap<ChatPage>("Chat")]
public partial class ChatViewModel(ChatService chat, ChatSessionProvider provider, CallService calls, ShellServices shell) : ObservableObject, IPageLifecycleAware
{
    public IChatSessionProvider Provider => provider;
    [ObservableProperty] public partial string? SessionId { get; set; }
    [ObservableProperty] public partial Conversation? Conversation { get; set; }


    public void OnAppearing()
    {
        this.Conversation = chat.Selected;
        this.SessionId = chat.Selected?.Id;
        chat.Viewing = this.SessionId;
        if (this.Conversation != null)
            this.Conversation.Unread = 0;
    }


    public void OnDisappearing() => chat.Viewing = null;


    /// <summary>
    /// "📎 Send a file" in the chat's input menu
    /// </summary>
    public async Task SendFile()
    {
        if (this.SessionId is not { } id || this.Conversation is not { IsOnline: true })
        {
            await shell.Dialogs.Alert("Send a file", $"{this.Conversation?.Name ?? "They"} aren't here right now");
            return;
        }

        try
        {
            var picked = await FilePicker.Default.PickAsync();
            if (picked != null)
                await provider.SendFile(id, picked);
        }
        catch (Exception ex)
        {
            await shell.Dialogs.Alert("Send a file", ex.Message);
        }
    }


    /// <summary>
    /// 📞 or 📹 - FaceTime the person in this conversation, over whichever link they're on
    /// </summary>
    [RelayCommand]
    async Task Call(bool video)
    {
        if (this.Conversation is not { IsOnline: true } conversation)
        {
            await shell.Dialogs.Alert("Call", $"{this.Conversation?.Name ?? "They"} aren't here right now");
            return;
        }
        await calls.Call(conversation, video);
    }
}
