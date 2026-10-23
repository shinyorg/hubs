using Shiny.Controls.Camera;
using Shiny.Maui.Controls.Camera;

namespace Sample.Calls;

/// <summary>
/// A call's camera: puts a <see cref="VideoEncoder"/> on the <see cref="CameraView"/> while the call sends video, and
/// turns the camera on for it. Stopping puts back whatever analyzer the camera had and turns the camera off again if
/// this turned it on.
/// </summary>
public class CameraCallVideoSource(CameraView camera) : ICallVideoSource
{
    readonly object gate = new();
    VideoEncoder? encoder;
    IFrameAnalyzer? previous;
    bool startedCamera;
    Action<CallVideoFrame>? frameEncoded;


    public void Start(CallVideoSettings settings, Action<CallVideoFrame> frameEncoded)
    {
        lock (this.gate)
            this.frameEncoded = frameEncoded;

        camera.Dispatcher.Dispatch(() =>
        {
            if (this.encoder == null)
            {
                var encoder = new VideoEncoder();
                encoder.FrameEncoded += this.OnFrameEncoded;
                this.previous = camera.Analyzer;
                this.encoder = encoder;
                Apply(encoder, settings);
                camera.Analyzer = encoder;
            }
            else
            {
                Apply(this.encoder, settings);
            }

            if (!camera.IsActive)
            {
                this.startedCamera = true;
                camera.IsActive = true;
            }
        });
    }


    public void RequestKeyFrame() => this.encoder?.RequestKeyFrame();


    public void Stop()
    {
        lock (this.gate)
            this.frameEncoded = null;

        camera.Dispatcher.Dispatch(() =>
        {
            if (this.encoder is not { } encoder)
                return;

            encoder.FrameEncoded -= this.OnFrameEncoded;
            if (ReferenceEquals(camera.Analyzer, encoder))
                camera.Analyzer = this.previous;
            this.encoder = null;
            this.previous = null;

            if (this.startedCamera)
            {
                this.startedCamera = false;
                camera.IsActive = false;
            }
        });
    }


    // the encoder's thread - hand it straight on
    void OnFrameEncoded(EncodedVideoFrame frame)
    {
        Action<CallVideoFrame>? handler;
        lock (this.gate)
            handler = this.frameEncoded;

        handler?.Invoke(new CallVideoFrame(
            CallVideoMapping.ToCall(frame.Codec),
            frame.Data,
            frame.Width,
            frame.Height,
            frame.Rotation,
            frame.IsMirrored,
            frame.IsKeyFrame,
            frame.Timestamp
        ));
    }


    static void Apply(VideoEncoder encoder, CallVideoSettings settings)
    {
        encoder.Codec = CallVideoMapping.ToCamera(settings.Codec);
        encoder.MaxDimension = settings.MaxDimension;
        encoder.FrameRate = settings.FrameRate;
        encoder.Bitrate = settings.Bitrate;
        encoder.JpegQuality = settings.JpegQuality;
    }
}


/// <summary>
/// Shows the other side of a call in a <see cref="RemoteVideoView"/>
/// </summary>
public class RemoteVideoCallSink(RemoteVideoView view) : ICallVideoSink
{
    readonly RemoteVideoView view = view;

    // forwarded, so a call that detaches this sink leaves nothing subscribed to the view
    public event EventHandler? KeyFrameNeeded
    {
        add => this.view.KeyFrameNeeded += value;
        remove => this.view.KeyFrameNeeded -= value;
    }

    public void Render(CallVideoFrame frame) => this.view.Render(new EncodedVideoFrame(
        CallVideoMapping.ToCamera(frame.Codec),
        frame.Data,
        frame.Width,
        frame.Height,
        frame.Rotation,
        frame.IsMirrored,
        frame.IsKeyFrame,
        frame.Timestamp
    ));

    public void Clear() => this.view.Clear();
}


/// <summary>
/// The codecs this device can both encode (<see cref="VideoEncoder.IsSupported"/>) and decode
/// (<see cref="RemoteVideoView.CanDecode"/>)
/// </summary>
public class CameraCallVideoCapabilities : ICallVideoCapabilities
{
    public CallVideoCodecs Supported { get; } =
        (Both(VideoCodec.Jpeg) ? CallVideoCodecs.Jpeg : CallVideoCodecs.None)
        | (Both(VideoCodec.H264) ? CallVideoCodecs.H264 : CallVideoCodecs.None);

    static bool Both(VideoCodec codec) => VideoEncoder.IsSupported(codec) && RemoteVideoView.CanDecode(codec);
}


static class CallVideoMapping
{
    public static VideoCodec ToCamera(CallVideoCodec codec) => codec == CallVideoCodec.H264 ? VideoCodec.H264 : VideoCodec.Jpeg;
    public static CallVideoCodec ToCall(VideoCodec codec) => codec == VideoCodec.H264 ? CallVideoCodec.H264 : CallVideoCodec.Jpeg;
}
