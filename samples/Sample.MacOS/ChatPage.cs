using Shiny.BluetoothLE.Hubs;
using Shiny.Maui.Controls.Chat;
using Sample.Hub;

namespace Sample.MacOS;

/// <summary>
/// The conversation with the chat's owner, drawn by Shiny.Maui.Controls' ChatView
/// </summary>
public class ChatPage : ContentPage
{
    public ChatPage(MacClient client)
    {
        this.Title = client.ChatWith ?? "Chat";
        this.Content = new ChatView
        {
            Provider = new MacChatProvider(client),
            SessionId = "visit"
        };
    }


    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (this.Content is ChatView { Provider: MacChatProvider p })
            await p.Client.Chat.Disconnect();
    }
}


public class MacChatProvider(MacClient client) : IChatSessionProvider
{
    public MacClient Client => client;
    public Task<IChatSession> CreateSessionAsync(string[] userIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IChatSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<IChatSession>(new MacChatSession(client));
}


#pragma warning disable CS0067
sealed class MacChatSession : IChatSession
{
    readonly MacClient client;

    public MacChatSession(MacClient client)
    {
        this.client = client;
        client.LineAdded += this.OnLine;
        client.ChatClosed += this.OnClosed;
    }

    public ChatSessionInfo Info => new(
        "visit",
        this.client.ChatWith ?? "Chat",
        [
            new ChatSessionUserInfo("me", "Me", null, null, DateTimeOffset.MinValue),
            new ChatSessionUserInfo("them", this.client.ChatWith ?? "Them", null, null, DateTimeOffset.MinValue)
        ],
        [],
        MessageBodyPermissions.None,
        this.client.Chat.Status == BleHubClientStatus.Connected ? ChatSessionPermissions.CanSendMessages : ChatSessionPermissions.None,
        DateTimeOffset.MinValue,
        null,
        0
    );

    public string CurrentUserId => "me";

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<MessageChanged>? MessageUpdated;
    public event EventHandler<string>? MessageDeleted;
    public event EventHandler<UserTypingEvent>? UserTyping;
    public event EventHandler<ChatSessionUserInfo>? UserJoined;
    public event EventHandler<ChatSessionUserInfo>? UserLeft;
    public event EventHandler<ChatSessionInfo>? SessionUpdated;
    public event EventHandler<ChatConnectionState>? ConnectionStateChanged;

    public Task<MessagePage> GetMessagesAsync(string? cursorMessageId, MessagePageDirection direction, int count, CancellationToken ct = default)
        => Task.FromResult(new MessagePage(cursorMessageId == null && direction == MessagePageDirection.Older ? this.client.Lines.Select(ToMessage).ToList() : [], false));

    public async Task<ChatMessage> SendMessageAsync(OutgoingMessage message, CancellationToken ct = default)
        => ToMessage(await this.client.Say(message.Body ?? "", message.ClientMessageId));

    public Task<ChatMessage> ResendMessageAsync(string clientMessageId, CancellationToken ct = default) => throw new ChatSendRejectedException("Type it again", SendRejectionKind.Other);
    public Task EditMessageAsync(string messageId, string body, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ReactToMessageAsync(string messageId, string emoji, bool add, CancellationToken ct = default) => throw new NotSupportedException();
    public Task MarkReadAsync(string[] messageIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task ToggleTypingAsync(bool isTyping, CancellationToken ct = default) => Task.CompletedTask;
    public Task InviteUserAsync(string userId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task LeaveAsync(CancellationToken ct = default) => this.client.Chat.Disconnect();
    public Task RenameAsync(string sessionName, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        this.client.LineAdded -= this.OnLine;
        this.client.ChatClosed -= this.OnClosed;
        return ValueTask.CompletedTask;
    }

    void OnLine(object? sender, ChatLine line) => this.MessageReceived?.Invoke(this, ToMessage(line));
    void OnClosed(object? sender, HubDisconnect disconnect) => this.ConnectionStateChanged?.Invoke(this, ChatConnectionState.Offline);

    // the visitor wrote everything that didn't come from the host
    static ChatMessage ToMessage(ChatLine line) => new(line.Id, line.ClientMessageId, line.FromHost ? "them" : "me", line.Text, null, MessageStatus.Sent, null, line.Timestamp, null, [], []);
}
#pragma warning restore CS0067
