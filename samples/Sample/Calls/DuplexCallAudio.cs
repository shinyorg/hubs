using System.Threading.Channels;
using Shiny.Audio;

namespace Sample.Calls;

/// <summary>
/// A call's microphone and speaker: Shiny.Audio's full-duplex voice, with echo cancellation, noise suppression and gain
/// control (<see cref="AudioProcessingOptions.VoiceChat"/>), so what the speaker plays doesn't come back through the microphone
/// </summary>
public class DuplexCallAudio(IAudioDuplex duplex) : ICallAudio
{
    public async Task<bool> RequestAccess() => await duplex.RequestAccess().ConfigureAwait(false) == AccessState.Available;


    public async Task<ICallAudioSession> Start(int sampleRate, CancellationToken cancellationToken)
    {
        if (await duplex.RequestAccess().ConfigureAwait(false) != AccessState.Available)
            throw new InvalidOperationException("The microphone isn't available - permission was refused");

        var session = await duplex.StartAsync(new AudioDuplexOptions { SampleRate = sampleRate }, cancellationToken).ConfigureAwait(false);
        return new Session(session);
    }


    sealed class Session(IAudioDuplexSession session) : ICallAudioSession
    {
        public int SampleRate => session.SampleRate;
        public ChannelReader<ReadOnlyMemory<byte>> Captured => session.Captured;
        public void Play(ReadOnlySpan<byte> pcm16) => session.Play(pcm16);

        public bool Speakerphone
        {
            get => session.Speakerphone;
            set => session.Speakerphone = value;
        }

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}
