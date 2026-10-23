using Microsoft.Extensions.Logging;
using Shiny;
using Sample.Calls;
using Sample.Hub;

namespace Sample.Services;

/// <summary>
/// FaceTime for the chat: calls the person in a conversation, and opens the call screen when someone rings. The owner
/// of a chat calls a visitor through its ChatHub (CallClient); a visitor calls the owner through the IChatHub client
/// (CallHost). Created with the app, so it hears every incoming call.
/// </summary>
public class CallService
{
    readonly IHubCalls calls;
    readonly ShellServices shell;
    readonly PlayerSettings settings;
    readonly ILogger<CallService> logger;


    public CallService(IHubCalls calls, ShellServices shell, PlayerSettings settings, ILogger<CallService> logger)
    {
        this.calls = calls;
        this.shell = shell;
        this.settings = settings;
        this.logger = logger;

        calls.Incoming += (_, call) => shell.MainThread.BeginInvokeOnMainThread(() => _ = this.Show(call));
    }


    /// <summary>
    /// The call the call screen shows
    /// </summary>
    public IHubCall? Current { get; private set; }


    public async Task Call(Conversation conversation, bool video)
    {
        var options = new CallOptions(video, this.settings.Name);
        try
        {
            var call = conversation.IsVisiting
                ? await this.calls.CallHost<IChatHub>(options)
                : await this.calls.CallClient<ChatHub>(conversation.ConnectionId ?? throw new InvalidOperationException($"{conversation.Name} has left"), options);

            await this.Show(call);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Could not call {Name}", conversation.Name);
            await this.shell.Dialogs.Alert("Call", ex.Message);
        }
    }


    async Task Show(IHubCall call)
    {
        this.Current = call;
        await this.shell.Navigator.NavigateToCall();
    }
}
