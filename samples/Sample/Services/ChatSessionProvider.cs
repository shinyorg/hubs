using Shiny.Maui.Controls.Chat;
using Sample.Hub;

namespace Sample.Services;

/// <summary>
/// Feeds ChatView from ChatService - a session is one conversation
/// </summary>
[Singleton]
public class ChatSessionProvider(ChatService chat) : IChatSessionProvider
{
    public Task<IChatSession> CreateSessionAsync(string[] userIds, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Conversations start when someone connects");

    internal const string FileKey = "file";


    /// <summary>
    /// A file from the "📎 Send a file" action - photos come through ChatView's own attach button. The bubble arrives
    /// through the conversation's live stream, like every other line.
    /// </summary>
    public async Task SendFile(string sessionId, FileResult picked)
    {
        var conversation = chat.Find(sessionId) ?? throw new ChatSessionException($"No conversation {sessionId}");
        var temp = Path.Combine(FileSystem.CacheDirectory, $"send-{Guid.NewGuid():N}{Path.GetExtension(picked.FileName)}");
        await using (var source = await picked.OpenReadAsync())
        await using (var file = File.Create(temp))
            await source.CopyToAsync(file);

        try
        {
            var isImage = picked.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
            await chat.SendFile(conversation, temp, picked.FileName, isImage, "", Guid.NewGuid().ToString("N"));
        }
        finally
        {
            File.Delete(temp);
        }
    }


    /// <summary>
    /// The local copy of a message's photo or file, to open it
    /// </summary>
    public static string? FileFor(ChatMessage message)
        => message.Metadata?.TryGetValue(FileKey, out var path) == true && File.Exists(path) ? path : null;


    public Task<IChatSession> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var conversation = chat.Find(sessionId) ?? throw new ChatSessionException($"No conversation {sessionId}");
        return Task.FromResult<IChatSession>(new ConversationSession(chat, conversation));
    }
}


#pragma warning disable CS0067 // no edits, reactions, typing or membership changes in this chat
sealed class ConversationSession : IChatSession
{
    const string Me = "me";
    const string Them = "them";

    readonly ChatService chat;
    readonly Conversation conversation;
    readonly Dictionary<string, string> unsent = new();
    readonly IDisposable live;


    public ConversationSession(ChatService chat, Conversation conversation)
    {
        this.chat = chat;
        this.conversation = conversation;
        conversation.OnlineChanged += this.OnOnlineChanged;

        // the conversation actor publishes every message it stores
        this.live = chat.Live(conversation).Subscribe((line, _) =>
        {
            this.MessageReceived?.Invoke(this, this.ToMessage(line));
            return ValueTask.CompletedTask;
        });
    }


    public ChatSessionInfo Info => new(
        this.conversation.Id,
        this.conversation.Name,
        [
            new ChatSessionUserInfo(Me, "Me", null, null, DateTimeOffset.MinValue),
            new ChatSessionUserInfo(Them, this.conversation.Name, null, null, DateTimeOffset.MinValue)
        ],
        [],                                   // no reactions
        MessageBodyPermissions.None,
        this.conversation.IsOnline ? ChatSessionPermissions.CanSendMessages | ChatSessionPermissions.CanSendImages : ChatSessionPermissions.None,
        DateTimeOffset.MinValue,
        null,
        0
    );

    public string CurrentUserId => Me;

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<MessageChanged>? MessageUpdated;
    public event EventHandler<string>? MessageDeleted;
    public event EventHandler<UserTypingEvent>? UserTyping;
    public event EventHandler<ChatSessionUserInfo>? UserJoined;
    public event EventHandler<ChatSessionUserInfo>? UserLeft;
    public event EventHandler<ChatSessionInfo>? SessionUpdated;
    public event EventHandler<ChatConnectionState>? ConnectionStateChanged;


    public async Task<MessagePage> GetMessagesAsync(string? cursorMessageId, MessagePageDirection direction, int count, CancellationToken ct = default)
    {
        var all = await this.chat.History(this.conversation).Lines();
        var index = cursorMessageId == null ? -1 : all.ToList().FindIndex(x => x.Id == cursorMessageId);

        List<ChatLine> page;
        bool hasMore;
        if (direction == MessagePageDirection.Older)
        {
            var end = index < 0 ? all.Count : index;
            var start = Math.Max(0, end - count);
            page = all.Skip(start).Take(end - start).ToList();
            hasMore = start > 0;
        }
        else
        {
            var start = index < 0 ? all.Count : index + 1;
            page = all.Skip(start).Take(count).ToList();
            hasMore = start + count < all.Count;
        }
        return new MessagePage(page.Select(this.ToMessage).ToList(), hasMore);
    }


    public async Task<ChatMessage> SendMessageAsync(OutgoingMessage message, CancellationToken ct = default)
    {
        // a photo from ChatView's attach button - it hands over the stream, and the session owns it
        if (message.Attachment is { } attachment)
        {
            var temp = Path.Combine(FileSystem.CacheDirectory, $"send-{Guid.NewGuid():N}{Path.GetExtension(attachment.FileName)}");
            await using (attachment.Content)
            await using (var file = File.Create(temp))
                await attachment.Content.CopyToAsync(file, ct);

            try
            {
                var sent = await this.chat.SendFile(this.conversation, temp, attachment.FileName, true, message.Body?.Trim() ?? "", message.ClientMessageId);
                return this.ToMessage(sent);
            }
            finally
            {
                File.Delete(temp);
            }
        }

        var text = message.Body?.Trim();
        if (String.IsNullOrEmpty(text))
            throw new ChatSendRejectedException("Say something", SendRejectionKind.UnsupportedContent);

        this.unsent[message.ClientMessageId] = text;
        var line = await this.chat.Send(this.conversation, text, message.ClientMessageId);
        this.unsent.Remove(message.ClientMessageId);
        return this.ToMessage(line);
    }


    public Task<ChatMessage> ResendMessageAsync(string clientMessageId, CancellationToken ct = default)
        => this.unsent.TryGetValue(clientMessageId, out var text)
            ? this.SendMessageAsync(new OutgoingMessage(text, null, clientMessageId), ct)
            : throw new ChatSendRejectedException("That message is gone", SendRejectionKind.Other);


    public Task MarkReadAsync(string[] messageIds, CancellationToken ct = default)
    {
        this.conversation.Unread = 0;
        return Task.CompletedTask;
    }

    public Task EditMessageAsync(string messageId, string body, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ReactToMessageAsync(string messageId, string emoji, bool add, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ToggleTypingAsync(bool isTyping, CancellationToken ct = default) => Task.CompletedTask;
    public Task InviteUserAsync(string userId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task LeaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RenameAsync(string sessionName, CancellationToken ct = default) => throw new NotSupportedException();


    public ValueTask DisposeAsync()
    {
        this.live.Dispose();
        this.conversation.OnlineChanged -= this.OnOnlineChanged;
        return ValueTask.CompletedTask;
    }


    void OnOnlineChanged(object? sender, EventArgs e)
    {
        this.ConnectionStateChanged?.Invoke(this, this.conversation.IsOnline ? ChatConnectionState.Connected : ChatConnectionState.Offline);
        this.SessionUpdated?.Invoke(this, this.Info);
    }


    // a photo shows in the bubble (tapping it opens ChatView's image viewer); any other file is a "📎 name" bubble
    ChatMessage ToMessage(ChatLine line)
    {
        var local = this.chat.LocalFile(this.conversation, line);
        var body = line.Text;
        string? image = null;
        if (line.File != null)
        {
            if (line.IsImage && local != null && File.Exists(local))
                image = local;
            else
                body = $"📎 {ChatService.DisplayName(line.File)} ({Size(line.FileSize)})" + (String.IsNullOrEmpty(line.Text) ? "" : "\n" + line.Text);
        }

        return new(
            line.Id,
            line.ClientMessageId,
            this.conversation.IsMine(line) ? Me : Them,
            String.IsNullOrEmpty(body) ? null : body,
            image,
            MessageStatus.Sent,
            null,
            line.Timestamp,
            null,
            [],
            [],
            Metadata: local == null ? null : new Dictionary<string, string> { [ChatSessionProvider.FileKey] = local }
        );
    }


    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB"
    };
}
#pragma warning restore CS0067
