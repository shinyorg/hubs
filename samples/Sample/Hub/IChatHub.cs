using System.Text.Json.Serialization;
using Shiny.BluetoothLE.Hubs;

namespace Sample.Hub;

/// <summary>
/// The always-on chat. Every running app hosts one, so anyone nearby, on the same network or - through a tunnel - on the
/// internet can message its owner. The owner answers each person in their own conversation.
/// </summary>
[BleHubClient]
public interface IChatHub
{
    /// <summary>
    /// Sends a message to the host. Returns it as the host stored it.
    /// </summary>
    Task<ChatLine> Send(string text, string clientMessageId);

    /// <summary>
    /// Sends a photo or file the visitor has already uploaded (UploadFile) as <paramref name="fileName"/>
    /// </summary>
    Task<ChatLine> SendFile(string fileName, bool isImage, long size, string clientMessageId);

    /// <summary>
    /// Host to client: the host answered
    /// </summary>
    event Action<ChatLine> MessageReceived;
}


/// <param name="Id">Unique per message</param>
/// <param name="ClientMessageId">The id the sender's ChatView gave the message, so its optimistic bubble is matched up</param>
/// <param name="FromHost">Written by the chat's owner rather than the visitor</param>
/// <param name="File">A photo or file sent with the message: its name in the owner's files (chat-{id}-{original})</param>
/// <param name="IsImage">The file is a photo - shown in the bubble, opened in the image viewer</param>
public record ChatLine(
    string Id,
    string ClientMessageId,
    bool FromHost,
    string SenderName,
    string Text,
    DateTimeOffset Timestamp,
    string? File = null,
    bool IsImage = false,
    long FileSize = 0
);


[JsonSerializable(typeof(ChatLine))]
[JsonSerializable(typeof(string))]
public partial class ChatJsonContext : JsonSerializerContext;
