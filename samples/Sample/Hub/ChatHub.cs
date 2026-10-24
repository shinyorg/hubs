using Shiny.BluetoothLE.Hubs;
using Shiny.UniversalHubs;
using Sample.Services;

namespace Sample.Hub;

/// <summary>
/// Runs on the chat's owner. A new instance handles every call, so conversations live in the ChatService singleton.
/// </summary>
public class ChatHub(ChatService chat) : UniversalHub<IChatHub>
{
    public override Task OnConnectedAsync(HubConnection connection) => chat.OnVisitorConnected(connection);


    public Task<ChatLine> Send(string text, string clientMessageId)
    {
        if (String.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Say something");

        return chat.OnVisitorMessage(this.Connection, text.Trim(), clientMessageId);
    }


    public Task<ChatLine> SendFile(string fileName, bool isImage, long size, string clientMessageId)
        => chat.OnVisitorFile(this.Connection, fileName, isImage, size, clientMessageId);


    public override Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect)
    {
        chat.OnVisitorDisconnected(connection, disconnect);
        return Task.CompletedTask;
    }
}
