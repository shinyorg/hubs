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


    readonly HubCallsOptions options;
    readonly DuplexCallAudio audio;


    public CallService(IHubCalls calls, HubCallsOptions options, DuplexCallAudio audio, ShellServices shell, PlayerSettings settings, ILogger<CallService> logger)
    {
        this.calls = calls;
        this.options = options;
        this.audio = audio;
        this.shell = shell;
        this.settings = settings;
        this.logger = logger;

        calls.Incoming += (_, call) => shell.MainThread.BeginInvokeOnMainThread(() => _ = this.Show(call));
        this.ApplyVideoCodec();
    }


    /// <summary>
    /// The microphone calls use - picked on the call screen, and kept for the next call
    /// </summary>
    public DuplexCallAudio Audio => this.audio;


    public static IReadOnlyList<string> VideoCodecChoices { get; } = ["Auto", "H264", "Jpeg"];

    /// <summary>
    /// Auto offers H.264 and JPEG, and a call uses H.264 when both phones have it. H264 or Jpeg offers only that one, so
    /// a call with this phone uses it - or, for H264 with a phone that lacks it, has no video.
    /// </summary>
    public string VideoCodec
    {
        get => this.settings.VideoCodec;
        set
        {
            this.settings.VideoCodec = value;
            this.ApplyVideoCodec();
        }
    }


    void ApplyVideoCodec() => this.options.AllowedVideoCodecs = this.settings.VideoCodec switch
    {
        "H264" => CallVideoCodecs.H264,
        "Jpeg" => CallVideoCodecs.Jpeg,
        _ => CallVideoCodecs.Jpeg | CallVideoCodecs.H264
    };


    /// <summary>
    /// The call the call screen shows
    /// </summary>
    public IHubCall? Current { get; private set; }

    /// <summary>
    /// A call is ringing, connecting or active
    /// </summary>
    public bool IsInCall => this.calls.Current != null;


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
