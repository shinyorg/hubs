using CommunityToolkit.Mvvm.ComponentModel;
using Shiny;
using Shiny.Maui.Controls.Chat;
using Sample.Services;

namespace Sample.Pages;

/// <summary>
/// One conversation, drawn by Shiny.Maui.Controls' ChatView through ChatSessionProvider
/// </summary>
[ShellMap<ChatPage>("Chat")]
public partial class ChatViewModel(ChatService chat, ChatSessionProvider provider) : ObservableObject, IPageLifecycleAware
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
}
