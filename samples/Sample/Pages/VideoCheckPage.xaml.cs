using Shiny.Controls.Camera;
using Shiny.Maui.Controls.Camera;

namespace Sample.Pages;

public partial class VideoCheckPage : ContentPage
{
    VideoEncoder? encoder;
    long encoded;
    long keyFramesNeeded;
    EncodedVideoFrame? last;

    public VideoCheckPage() => this.InitializeComponent();


    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await this.Camera.RequestPermissionAsync();

        var codec = Environment.GetEnvironmentVariable("UH_VIDEOCHECK_CODEC") != "jpeg"
                    && VideoEncoder.IsSupported(VideoCodec.H264) && RemoteVideoView.CanDecode(VideoCodec.H264)
            ? VideoCodec.H264
            : VideoCodec.Jpeg;
        this.encoder = new VideoEncoder { Codec = codec, MaxDimension = 640, FrameRate = 15 };
        this.encoder.FrameEncoded += frame =>
        {
            Interlocked.Increment(ref this.encoded);
            this.last = frame;
            this.Remote.Render(frame);
        };
        this.Remote.KeyFrameNeeded += (_, _) =>
        {
            Interlocked.Increment(ref this.keyFramesNeeded);
            this.encoder?.RequestKeyFrame();
        };
        this.Camera.Analyzer = this.encoder;
        this.Camera.IsActive = true;

        while (this.encoder != null)
        {
            var f = this.last;
            var vm = (VideoCheckViewModel)this.BindingContext;
            vm.Status = $"{codec}: {Interlocked.Read(ref this.encoded)} frames encoded"
                + (f == null ? "" : $", last {f.Width}x{f.Height} rot {f.Rotation} mirrored {f.IsMirrored} key {f.IsKeyFrame} {f.Data.Length}B")
                + $", key frames asked {Interlocked.Read(ref this.keyFramesNeeded)}, view {this.Remote.Width:0}x{this.Remote.Height:0}";
            Console.WriteLine("[videocheck] " + vm.Status);
            await Task.Delay(1000);
        }
    }


    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        this.Camera.Analyzer = null;
        this.Camera.IsActive = false;
        this.encoder = null;
    }

}
