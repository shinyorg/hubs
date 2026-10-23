using System.Threading.Channels;

namespace Sample.Calls;

/// <summary>
/// The microphone and speaker a call talks through. AddHubCallsMaui registers one built on Shiny.Audio's
/// full-duplex voice, which cancels the speaker's echo out of the microphone.
/// </summary>
public interface ICallAudio
{
    /// <summary>
    /// Asks for the microphone. False when it was refused.
    /// </summary>
    Task<bool> RequestAccess();

    /// <summary>
    /// Opens the microphone and speaker at <paramref name="sampleRate"/> (8000 or 16000), PCM16 mono
    /// </summary>
    Task<ICallAudioSession> Start(int sampleRate, CancellationToken cancellationToken);
}


/// <summary>
/// A running microphone and speaker
/// </summary>
public interface ICallAudioSession : IAsyncDisposable
{
    int SampleRate { get; }

    /// <summary>
    /// What the microphone hears: PCM16 mono little-endian, one frame (about 20 ms) per item
    /// </summary>
    ChannelReader<ReadOnlyMemory<byte>> Captured { get; }

    /// <summary>
    /// Queues PCM16 mono for the speaker. Called from the receive thread; must not block.
    /// </summary>
    void Play(ReadOnlySpan<byte> pcm16);

    /// <summary>
    /// The loudspeaker rather than the earpiece
    /// </summary>
    bool Speakerphone { get; set; }
}


/// <summary>
/// One encoded frame of video, as it travels
/// </summary>
/// <param name="Rotation">Clockwise degrees (0, 90, 180, 270) that make the frame upright</param>
/// <param name="IsKeyFrame">Decodes on its own. JPEG frames always are; H.264 key frames carry SPS/PPS (Annex B)</param>
public sealed record CallVideoFrame(
    CallVideoCodec Codec,
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int Rotation,
    bool IsMirrored,
    bool IsKeyFrame,
    TimeSpan Timestamp
);


/// <summary>
/// What the camera should produce for a call
/// </summary>
/// <param name="MaxDimension">The long edge, in pixels</param>
/// <param name="Bitrate">H.264 bits per second</param>
/// <param name="JpegQuality">0 to 1</param>
public sealed record CallVideoSettings(CallVideoCodec Codec, int MaxDimension, int FrameRate, int Bitrate, double JpegQuality);


/// <summary>
/// The camera side of a call: encodes frames and hands them over. Calls.Maui adapts a CameraView.
/// </summary>
public interface ICallVideoSource
{
    /// <summary>
    /// Starts encoding, or changes the settings of a running encoder. <paramref name="frameEncoded"/> may be called on any
    /// thread and must not block.
    /// </summary>
    void Start(CallVideoSettings settings, Action<CallVideoFrame> frameEncoded);

    /// <summary>
    /// The other side lost its place - make the next frame a key frame
    /// </summary>
    void RequestKeyFrame();

    void Stop();
}


/// <summary>
/// Shows the other side's video. Calls.Maui adapts a RemoteVideoView.
/// </summary>
public interface ICallVideoSink
{
    /// <summary>
    /// Called on the receive thread
    /// </summary>
    void Render(CallVideoFrame frame);

    void Clear();

    /// <summary>
    /// The decoder lost sync - the call asks the sender for a key frame
    /// </summary>
    event EventHandler? KeyFrameNeeded;
}


/// <summary>
/// Which video codecs this device can both encode and decode. Calls.Maui registers one; without one, calls are audio only.
/// </summary>
public interface ICallVideoCapabilities
{
    CallVideoCodecs Supported { get; }
}


/// <summary>
/// What a call sends, for one kind of link
/// </summary>
/// <param name="MaxDimension">The video's long edge, in pixels</param>
/// <param name="H264Bitrate">Bits per second</param>
/// <param name="JpegQuality">0 to 1</param>
/// <param name="AudioSampleRate">8000 or 16000</param>
public sealed record CallQuality(
    int MaxDimension,
    int FrameRate,
    int H264Bitrate,
    double JpegQuality,
    int AudioSampleRate,
    CallAudioCodec AudioCodec
)
{
    /// <summary>
    /// 640 px at 15 fps, PCM16 at 16 kHz (32 KB/s)
    /// </summary>
    public static CallQuality Wifi { get; } = new(640, 15, 800_000, 0.6, 16000, CallAudioCodec.Pcm16);

    /// <summary>
    /// 160 px at 3 fps, IMA ADPCM at 8 kHz (4 KB/s)
    /// </summary>
    public static CallQuality Ble { get; } = new(160, 3, 64_000, 0.4, 8000, CallAudioCodec.ImaAdpcm);

    internal CallVideoSettings ToVideo(CallVideoCodec codec) => new(codec, this.MaxDimension, this.FrameRate, this.H264Bitrate, this.JpegQuality);
}


/// <summary>
/// How to place a call
/// </summary>
/// <param name="Video">Ask for video. The callee may still answer with audio only.</param>
/// <param name="DisplayName">The name the callee sees - the client's name (or the host's local name) when null</param>
public sealed record CallOptions(bool Video = true, string? DisplayName = null)
{
    public static CallOptions AudioOnly { get; } = new(false);
}


public class HubCallsOptions
{
    /// <summary>
    /// What calls send over Wi-Fi
    /// </summary>
    public CallQuality Wifi { get; set; } = CallQuality.Wifi;

    /// <summary>
    /// What calls send when any leg is Bluetooth
    /// </summary>
    public CallQuality Ble { get; set; } = CallQuality.Ble;

    /// <summary>
    /// How long a call rings before it is Missed
    /// </summary>
    public TimeSpan RingTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Chosen when the other side has it too. Otherwise H.264, then JPEG.
    /// </summary>
    public CallVideoCodec? PreferredVideoCodec { get; set; }

    internal CallQuality For(CallProfile profile) => profile == CallProfile.Ble ? this.Ble : this.Wifi;
}
