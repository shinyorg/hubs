using System.Threading.Channels;
using Shiny.Audio;

namespace Sample.Calls;

/// <summary>
/// A call's microphone and speaker: Shiny.Audio's full-duplex voice, with echo cancellation, noise suppression and gain
/// control (<see cref="AudioProcessingOptions.VoiceChat"/>), so what the speaker plays doesn't come back through the microphone
/// </summary>
public class DuplexCallAudio(IAudioDuplex duplex, IAudioDevices? devices = null) : ICallAudio
{
    IAudioDuplexSession? current;
    AudioDevice? inputDevice;


    /// <summary>
    /// The microphones to choose from - empty where Shiny.Audio can't list them
    /// </summary>
    public IReadOnlyList<AudioDevice> GetMicrophones() => devices?.GetInputs() ?? [];


    /// <summary>
    /// The chosen microphone went away mid-call (unplugged, out of range) and the call fell back to the default.
    /// Raised on a platform thread.
    /// </summary>
    public event EventHandler? InputDeviceChanged;


    /// <summary>
    /// The microphone calls use, null for the system default. Changing it switches the call in progress too.
    /// </summary>
    public AudioDevice? InputDevice
    {
        get => this.inputDevice;
        set
        {
            this.inputDevice = value;
            if (this.current is { } session)
                session.InputDevice = value;
        }
    }


    public async Task<bool> RequestAccess() => await duplex.RequestAccess().ConfigureAwait(false) == AccessState.Available;


    public async Task<ICallAudioSession> Start(int sampleRate, CancellationToken cancellationToken)
    {
        if (await duplex.RequestAccess().ConfigureAwait(false) != AccessState.Available)
            throw new InvalidOperationException("The microphone isn't available - permission was refused");

        var session = await duplex.StartAsync(new AudioDuplexOptions
        {
            SampleRate = sampleRate,
            InputDevice = this.inputDevice
        }, cancellationToken).ConfigureAwait(false);
        this.current = session;
        session.InputDeviceChanged += this.OnInputDeviceLost;
        return new Session(session, () =>
        {
            session.InputDeviceChanged -= this.OnInputDeviceLost;
            this.current = null;
        });
    }


    void OnInputDeviceLost(object? sender, EventArgs e)
    {
        this.inputDevice = null;
        this.InputDeviceChanged?.Invoke(this, EventArgs.Empty);
    }


    sealed class Session(IAudioDuplexSession session, Action ended) : ICallAudioSession
    {
        public int SampleRate => session.SampleRate;
        public ChannelReader<ReadOnlyMemory<byte>> Captured => session.Captured;
        public void Play(ReadOnlySpan<byte> pcm16) => session.Play(pcm16);

        public bool Speakerphone
        {
            get => session.Speakerphone;
            set => session.Speakerphone = value;
        }

        public ValueTask DisposeAsync()
        {
            ended();
            return session.DisposeAsync();
        }
    }
}
