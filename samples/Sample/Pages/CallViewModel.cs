using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using Shiny.Maui.Controls.Camera;
using Sample.Calls;
using Sample.Services;

namespace Sample.Pages;

/// <summary>
/// The call screen: ringing, then the other person full screen with this device's camera in the corner
/// </summary>
[ShellMap<CallPage>("Call")]
public partial class CallViewModel(CallService calls, ShellServices shell) : ObservableObject, IPageLifecycleAware
{
    public IHubCall? Call { get; private set; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsIncoming { get; set; }
    [ObservableProperty] public partial bool IsInCall { get; set; }
    [ObservableProperty] public partial bool IsDialing { get; set; }
    [ObservableProperty] public partial bool IsVideo { get; set; }
    [ObservableProperty] public partial bool IsMuted { get; set; }
    [ObservableProperty] public partial bool IsCameraOn { get; set; } = true;
    [ObservableProperty] public partial bool IsSpeakerphone { get; set; } = true;
    [ObservableProperty] public partial bool ShowRemoteVideo { get; set; }

    /// <summary>
    /// The camera filming this side - bound to the CameraView's CameraId; null is the front camera
    /// </summary>
    [ObservableProperty] public partial string? CameraId { get; set; }

    [ObservableProperty] public partial string MicrophoneName { get; set; } = "Default mic";

    /// <summary>
    /// Set by the page: lists its CameraView's cameras
    /// </summary>
    public Func<Task<IReadOnlyList<CameraInfo>>>? LoadCameras { get; set; }

    /// <summary>
    /// The page attaches its CameraView and RemoteVideoView to the call
    /// </summary>
    public event EventHandler? VideoReady;


    public void OnAppearing()
    {
        this.Call = calls.Current;
        if (this.Call is not { } call)
            return;

        this.Name = call.RemoteName ?? (call.RemoteConnectionId.Length == 0 ? "The host" : "Someone");
        call.StateChanged += this.OnStateChanged;
        call.RemoteMediaChanged += this.OnRemoteMediaChanged;
        calls.Audio.InputDeviceChanged += this.OnMicrophoneLost;
        this.MicrophoneName = calls.Audio.InputDevice?.Name ?? "Default mic";
        this.Update();
        this.VideoReady?.Invoke(this, EventArgs.Empty);
    }


    public void OnDisappearing()
    {
        if (this.Call is { } call)
        {
            call.StateChanged -= this.OnStateChanged;
            call.RemoteMediaChanged -= this.OnRemoteMediaChanged;
            calls.Audio.InputDeviceChanged -= this.OnMicrophoneLost;
            _ = call.HangUp();   // leaving the screen ends the call
        }
    }


    void OnStateChanged(object? sender, CallState state) => shell.MainThread.BeginInvokeOnMainThread(async () =>
    {
        this.Update();
        if (state == CallState.Ended)
        {
            await Task.Delay(1500);   // long enough to read why
            await shell.Navigator.GoBack();
        }
    });


    void OnMicrophoneLost(object? sender, EventArgs e)
        => shell.MainThread.BeginInvokeOnMainThread(() => this.MicrophoneName = "Default mic");


    void OnRemoteMediaChanged(object? sender, EventArgs e) => shell.MainThread.BeginInvokeOnMainThread(this.Update);


    void Update()
    {
        if (this.Call is not { } call)
            return;

        this.IsIncoming = call.State == CallState.Incoming;
        this.IsDialing = call.State is CallState.Dialing or CallState.Ringing;
        this.IsInCall = call.State is CallState.Connecting or CallState.Active;
        this.IsVideo = call.IsVideo;
        this.ShowRemoteVideo = call.State == CallState.Active && call.IsVideo && call.IsRemoteCameraOn;
        this.Status = call.State switch
        {
            CallState.Dialing => "Calling...",
            CallState.Ringing => "Ringing...",
            CallState.Incoming => call.IsVideo ? "📹 Video call" : "📞 Audio call",
            CallState.Connecting => "Connecting...",
            CallState.Active => Describe(call),
            _ => call.End?.Reason switch
            {
                CallEndReason.Declined => "Declined",
                CallEndReason.Busy => "Busy",
                CallEndReason.Missed => "No answer",
                CallEndReason.Cancelled => call.Direction == CallDirection.Incoming ? "Missed call" : "Cancelled",
                CallEndReason.Unreachable => "Not reachable",
                CallEndReason.Unsupported => "Their app can't take this call",
                CallEndReason.Dropped => "📡 Connection lost",
                CallEndReason.Failed => "Couldn't start: " + call.End.Message,
                _ => "Call ended"
            }
        };
    }


    static string Describe(IHubCall call)
    {
        var link = call.Profile == CallProfile.Ble ? "🔵 Bluetooth" : "📶 Wi-Fi";
        var video = call.VideoCodec is { } codec ? $" · {(codec == CallVideoCodec.H264 ? "H.264" : "JPEG")}" : "";
        var muted = call.IsRemoteMuted ? " · 🔇 muted" : "";
        return $"{link}{video}{muted}";
    }


    [RelayCommand]
    Task Accept() => this.Answer(true);

    [RelayCommand]
    Task AcceptAudio() => this.Answer(false);

    async Task Answer(bool video)
    {
        try
        {
            await this.Call!.Accept(video);
        }
        catch (Exception ex)
        {
            await shell.Dialogs.Alert("Call", ex.Message);
        }
    }


    [RelayCommand]
    Task Decline() => this.Call?.Decline() ?? Task.CompletedTask;

    [RelayCommand]
    Task HangUp() => this.Call?.HangUp() ?? Task.CompletedTask;

    [RelayCommand]
    void ToggleMute()
    {
        this.IsMuted = !this.IsMuted;
        this.Call!.IsMuted = this.IsMuted;
    }

    [RelayCommand]
    void ToggleCamera()
    {
        this.IsCameraOn = !this.IsCameraOn;
        this.Call!.IsCameraOn = this.IsCameraOn;
    }

    [RelayCommand]
    async Task PickCamera()
    {
        IReadOnlyList<CameraInfo> cameras = [];
        try
        {
            if (this.LoadCameras != null)
                cameras = await this.LoadCameras();
        }
        catch
        {
            // no camera permission yet
        }
        if (cameras.Count < 2)
        {
            await shell.Dialogs.Alert("Camera", cameras.Count == 0 ? "No cameras are available" : "This device has only one camera");
            return;
        }
        var choice = await shell.Dialogs.ActionSheet("Camera", "Cancel", null, cameras.Select(x => x.Name).ToArray());
        if (cameras.FirstOrDefault(x => x.Name == choice) is { } camera)
            this.CameraId = camera.Id;
    }


    [RelayCommand]
    async Task PickMicrophone()
    {
        var mics = calls.Audio.GetMicrophones();
        if (mics.Count == 0)
        {
            await shell.Dialogs.Alert("Microphone", "This device can't list its microphones");
            return;
        }
        const string system = "Default";
        var choice = await shell.Dialogs.ActionSheet("Microphone", "Cancel", null, [system, .. mics.Select(x => x.Name)]);
        if (choice == system)
            calls.Audio.InputDevice = null;
        else if (mics.FirstOrDefault(x => x.Name == choice) is { } mic)
            calls.Audio.InputDevice = mic;
        else
            return;

        this.MicrophoneName = calls.Audio.InputDevice?.Name ?? "Default mic";
    }


    [RelayCommand]
    void ToggleSpeaker()
    {
        this.IsSpeakerphone = !this.IsSpeakerphone;
        this.Call!.IsSpeakerphone = this.IsSpeakerphone;
    }
}
