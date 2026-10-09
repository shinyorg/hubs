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
    /// Host to client: the host answered
    /// </summary>
    event Action<ChatLine> MessageReceived;
}


/// <param name="Id">Unique per message</param>
/// <param name="ClientMessageId">The id the sender's ChatView gave the message, so its optimistic bubble is matched up</param>
/// <param name="FromHost">Written by the chat's owner rather than the visitor</param>
public record ChatLine(
    string Id,
    string ClientMessageId,
    bool FromHost,
    string SenderName,
    string Text,
    DateTimeOffset Timestamp
);


[JsonSerializable(typeof(ChatLine))]
[JsonSerializable(typeof(string))]
public partial class ChatJsonContext : JsonSerializerContext;
