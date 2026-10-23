using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sample.Calls;

public enum CallDirection
{
    Outgoing,
    Incoming
}


public enum CallState
{
    /// <summary>
    /// Outgoing: the invite is on its way
    /// </summary>
    Dialing,

    /// <summary>
    /// Outgoing: the other device is ringing
    /// </summary>
    Ringing,

    /// <summary>
    /// Incoming: waiting for Accept or Decline
    /// </summary>
    Incoming,

    /// <summary>
    /// Answered: the microphone and speaker are starting
    /// </summary>
    Connecting,

    /// <summary>
    /// Talking
    /// </summary>
    Active,

    Ended
}


/// <summary>
/// How a call ended
/// </summary>
/// <param name="ByRemote">The other side (or the link) ended it, not this device</param>
public sealed record CallEnd(CallEndReason Reason, string? Message, bool ByRemote);


/// <param name="Duration">Time since the call was answered</param>
public sealed record CallStats(
    TimeSpan Duration,
    long BytesSent,
    long BytesReceived,
    long AudioPacketsSent,
    long AudioPacketsReceived,
    long VideoFramesSent,
    long VideoFramesReceived,
    long VideoFramesDropped
);


/// <summary>
/// One call. Events are raised on whichever thread the change happened on - marshal to the UI thread before binding.
/// </summary>
public interface IHubCall
{
    Guid Id { get; }
    CallDirection Direction { get; }

    /// <summary>
    /// The other side's connection id - empty when it is the host
    /// </summary>
    string RemoteConnectionId { get; }

    /// <summary>
    /// The name the other side gave
    /// </summary>
    string? RemoteName { get; }

    /// <summary>
    /// The hub type (host side) or contract (client side) the call runs through
    /// </summary>
    Type Hub { get; }

    CallState State { get; }

    /// <summary>
    /// Set once <see cref="State"/> is Ended
    /// </summary>
    CallEnd? End { get; }

    /// <summary>
    /// Before the answer, whether video was asked for. After it, whether the call has video.
    /// </summary>
    bool IsVideo { get; }

    /// <summary>
    /// The negotiated video codec, null for an audio call or before the answer
    /// </summary>
    CallVideoCodec? VideoCodec { get; }

    /// <summary>
    /// Ble when any leg of the call is Bluetooth, which sets its quality
    /// </summary>
    CallProfile Profile { get; }

    CallQuality Quality { get; }

    bool IsMuted { get; set; }
    bool IsCameraOn { get; set; }
    bool IsSpeakerphone { get; set; }

    bool IsRemoteMuted { get; }
    bool IsRemoteCameraOn { get; }

    CallStats Stats { get; }

    /// <summary>
    /// Answers an incoming call. <paramref name="video"/> false answers a video call with audio only.
    /// </summary>
    Task Accept(bool video = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses an incoming call
    /// </summary>
    Task Decline(string? message = null);

    /// <summary>
    /// Ends the call - or, before an answer, cancels it
    /// </summary>
    Task HangUp(string? message = null);

    /// <summary>
    /// Where the call's video comes from and goes to. Either may be null, and attaching again replaces them. Frames
    /// start flowing once the call is Active with video.
    /// </summary>
    void AttachVideo(ICallVideoSource? source, ICallVideoSink? sink);

    /// <summary>
    /// Completes when the call ends
    /// </summary>
    Task<CallEnd> Completion { get; }

    event EventHandler<CallState>? StateChanged;

    /// <summary>
    /// The other side muted, or turned its camera on or off
    /// </summary>
    event EventHandler? RemoteMediaChanged;
}


internal sealed class HubCall : IHubCall
{
    readonly HubCalls owner;
    readonly HubCallsOptions options;
    readonly ICallAudio? audio;
    readonly ILogger? logger;
    readonly object gate = new();
    readonly TaskCompletionSource<CallEnd> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch clock = new();
    readonly ImaAdpcm adpcm = new();
    readonly CancellationTokenSource stop = new();
    readonly CallVideoCodecs localCodecs;
    readonly CallVideoCodecs remoteCodecs;
    readonly bool wantsVideo;
    readonly Channel<Action> events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    string inviteTo = "";
    CancellationTokenSource? ringTimer;
    ICallAudioSession? audioSession;
    ICallVideoSource? videoSource;
    ICallVideoSink? videoSink;
    bool videoSourceRunning;
    ushort audioSeq;
    ushort videoSeq;
    bool muted;
    bool cameraOn = true;
    bool speakerphone = true;
    long bytesReceived;
    long audioSent;
    long audioReceived;
    long videoSent;
    long videoReceived;


    HubCall(
        HubCalls owner,
        MediaLink link,
        Guid id,
        CallDirection direction,
        string remoteConnectionId,
        string? remoteName,
        Type hub,
        bool wantsVideo,
        CallProfile profile,
        CallVideoCodecs localCodecs,
        CallVideoCodecs remoteCodecs,
        CallState state
    )
    {
        this.owner = owner;
        this.options = owner.Options;
        this.audio = owner.Audio;
        this.logger = owner.Logger;
        this.Link = link;
        this.Id = id;
        this.Direction = direction;
        this.RemoteConnectionId = remoteConnectionId;
        this.RemoteName = remoteName;
        this.Hub = hub;
        this.wantsVideo = wantsVideo;
        this.Profile = profile;
        this.localCodecs = localCodecs;
        this.remoteCodecs = remoteCodecs;
        this.State = state;
        _ = Task.Run(this.RaiseEvents);
    }


    /// <param name="to">Who the invite is addressed to: a peer's connection id, or empty for whoever is at the other end of <paramref name="link"/></param>
    internal static HubCall Outgoing(HubCalls owner, MediaLink link, string to, Type hub, CallOptions options, CallVideoCodecs localCodecs)
        => new(owner, link, Guid.NewGuid(), CallDirection.Outgoing, to.Length > 0 ? to : link.Id, null, hub, options.Video && localCodecs != CallVideoCodecs.None, link.Profile, localCodecs, CallVideoCodecs.None, CallState.Dialing)
        {
            inviteTo = to
        };


    internal static HubCall Incoming(HubCalls owner, MediaLink link, Guid id, CallInvite invite, Type hub, CallVideoCodecs localCodecs)
    {
        var profile = invite.Profile == CallProfile.Ble || link.Profile == CallProfile.Ble ? CallProfile.Ble : CallProfile.Wifi;
        return new(owner, link, id, CallDirection.Incoming, invite.From, invite.FromName, hub, invite.WantsVideo, profile, localCodecs, invite.VideoCodecs, CallState.Incoming);
    }


    internal MediaLink Link { get; }

    public Guid Id { get; }
    public CallDirection Direction { get; }
    public string RemoteConnectionId { get; }
    public string? RemoteName { get; private set; }
    public Type Hub { get; }
    public CallState State { get; private set; }
    public CallEnd? End { get; private set; }
    public bool IsVideo => this.State is CallState.Connecting or CallState.Active or CallState.Ended ? this.VideoCodec != null : this.wantsVideo;
    public CallVideoCodec? VideoCodec { get; private set; }
    public CallProfile Profile { get; private set; }
    public CallQuality Quality => this.options.For(this.Profile);
    public bool IsRemoteMuted { get; private set; }
    public bool IsRemoteCameraOn { get; private set; } = true;
    public Task<CallEnd> Completion => this.completion.Task;

    public event EventHandler<CallState>? StateChanged;
    public event EventHandler? RemoteMediaChanged;


    public bool IsMuted
    {
        get => this.muted;
        set
        {
            if (this.muted == value)
                return;
            this.muted = value;
            this.SendMediaState();
        }
    }


    public bool IsCameraOn
    {
        get => this.cameraOn;
        set
        {
            if (this.cameraOn == value)
                return;
            this.cameraOn = value;
            this.UpdateVideoSource();
            this.SendMediaState();
        }
    }


    public bool IsSpeakerphone
    {
        get => this.speakerphone;
        set
        {
            this.speakerphone = value;
            if (this.audioSession is { } session)
                session.Speakerphone = value;
        }
    }


    public CallStats Stats => new(
        this.clock.Elapsed,
        this.Link.Sender.BytesSent,
        Interlocked.Read(ref this.bytesReceived),
        Interlocked.Read(ref this.audioSent),
        Interlocked.Read(ref this.audioReceived),
        Interlocked.Read(ref this.videoSent),
        Interlocked.Read(ref this.videoReceived),
        this.Link.Sender.VideoFramesDropped
    );


    // ---- placing ----

    internal void Dial(string? displayName)
    {
        this.Link.Send(CallProtocol.Invite(this.Id, new CallInvite(
            this.inviteTo,
            "",
            displayName,
            this.wantsVideo,
            this.Profile,
            this.localCodecs
        )));
        this.StartRingTimer();
    }


    public async Task Accept(bool video = true, CancellationToken cancellationToken = default)
    {
        CallAccept accept;
        lock (this.gate)
        {
            if (this.Direction != CallDirection.Incoming)
                throw new InvalidOperationException("Only an incoming call can be accepted");
            if (this.State != CallState.Incoming)
                throw new InvalidOperationException($"The call is {this.State}");

            this.VideoCodec = video && this.wantsVideo ? this.ChooseCodec() : null;
            accept = new CallAccept(this.VideoCodec != null, this.VideoCodec, this.Profile);
            this.SetState(CallState.Connecting);
        }
        this.ringTimer?.Cancel();

        try
        {
            await this.StartMedia(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Call media failed to start");
            this.Link.Send(CallProtocol.Decline(this.Id, CallEndReason.Failed, ex.Message));
            await this.Finish(new CallEnd(CallEndReason.Failed, ex.Message, false)).ConfigureAwait(false);
            throw;
        }

        this.Link.Send(CallProtocol.Accept(this.Id, accept));
        this.Activate();
    }


    CallVideoCodec? ChooseCodec()
    {
        var common = this.localCodecs & this.remoteCodecs;
        if (this.options.PreferredVideoCodec is { } preferred && common.HasFlag((CallVideoCodecs)preferred))
            return preferred;
        if (common.HasFlag(CallVideoCodecs.H264))
            return CallVideoCodec.H264;
        if (common.HasFlag(CallVideoCodecs.Jpeg))
            return CallVideoCodec.Jpeg;
        return null;   // nothing in common - answer with audio only
    }


    public Task Decline(string? message = null)
    {
        lock (this.gate)
        {
            if (this.State != CallState.Incoming)
                return Task.CompletedTask;
        }
        this.Link.Send(CallProtocol.Decline(this.Id, CallEndReason.Declined, message));
        return this.Finish(new CallEnd(CallEndReason.Declined, message, false));
    }


    public Task HangUp(string? message = null)
    {
        CallEndReason reason;
        lock (this.gate)
        {
            switch (this.State)
            {
                case CallState.Ended:
                    return Task.CompletedTask;

                case CallState.Incoming:
                    reason = CallEndReason.Declined;
                    break;

                case CallState.Dialing or CallState.Ringing:
                    reason = CallEndReason.Cancelled;
                    break;

                default:
                    reason = CallEndReason.HungUp;
                    break;
            }
        }
        this.Link.Send(reason == CallEndReason.Declined
            ? CallProtocol.Decline(this.Id, reason, message)
            : CallProtocol.HangUp(this.Id, reason, message));
        return this.Finish(new CallEnd(reason, message, false));
    }


    // ---- what the other side sent ----

    internal async Task OnPacket(CallPacketType type, ReadOnlyMemory<byte> packet)
    {
        Interlocked.Add(ref this.bytesReceived, packet.Length);
        switch (type)
        {
            case CallPacketType.Ringing:
                lock (this.gate)
                {
                    if (this.State == CallState.Dialing)
                        this.SetState(CallState.Ringing);
                }
                break;

            case CallPacketType.Accept:
                await this.OnAccepted(CallProtocol.ReadAccept(packet.Span)).ConfigureAwait(false);
                break;

            case CallPacketType.Decline:
            case CallPacketType.HangUp:
                var (reason, message) = CallProtocol.ReadEnd(packet.Span);
                await this.Finish(new CallEnd(reason, message, true)).ConfigureAwait(false);
                break;

            case CallPacketType.Audio:
                this.OnAudio(CallProtocol.ReadAudio(packet));
                break;

            case CallPacketType.Video:
                this.OnVideo(CallProtocol.ReadVideo(packet));
                break;

            case CallPacketType.KeyFrameRequest:
                this.RequestKeyFrame();
                break;

            case CallPacketType.MediaState:
                var (remoteMuted, remoteCamera) = CallProtocol.ReadMediaState(packet.Span);
                this.IsRemoteMuted = remoteMuted;
                this.IsRemoteCameraOn = remoteCamera;
                if (!remoteCamera)
                    this.videoSink?.Clear();
                this.events.Writer.TryWrite(() => this.RemoteMediaChanged?.Invoke(this, EventArgs.Empty));
                break;
        }
    }


    async Task OnAccepted(CallAccept accept)
    {
        lock (this.gate)
        {
            if (this.State is not (CallState.Dialing or CallState.Ringing))
                return;

            this.VideoCodec = accept.Video && this.wantsVideo ? accept.VideoCodec : null;
            this.Profile = accept.Profile;   // a relayed call takes the slower leg's
            this.SetState(CallState.Connecting);
        }
        this.ringTimer?.Cancel();

        try
        {
            await this.StartMedia(this.stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Call media failed to start");
            this.Link.Send(CallProtocol.HangUp(this.Id, CallEndReason.Failed, ex.Message));
            await this.Finish(new CallEnd(CallEndReason.Failed, ex.Message, false)).ConfigureAwait(false);
            return;
        }
        this.Activate();
    }


    internal Task OnLinkLost() => this.Finish(new CallEnd(CallEndReason.Dropped, "The connection was lost", true));


    // ---- media ----

    async Task StartMedia(CancellationToken cancellationToken)
    {
        if (this.audio == null)
            throw new InvalidOperationException("No ICallAudio is registered - call AddHubCallsMaui");

        var session = await this.audio.Start(this.Quality.AudioSampleRate, cancellationToken).ConfigureAwait(false);
        session.Speakerphone = this.speakerphone;
        this.audioSession = session;
        _ = Task.Run(() => this.PumpAudio(session, this.stop.Token));
    }


    void Activate()
    {
        lock (this.gate)
        {
            if (this.State != CallState.Connecting)
                return;
            this.clock.Start();
            this.SetState(CallState.Active);
        }
        this.SendMediaState();
        this.UpdateVideoSource();
        // a decoder starting cold needs a key frame
        this.Link.Send(CallProtocol.KeyFrameRequest(this.Id));
    }


    async Task PumpAudio(ICallAudioSession session, CancellationToken ct)
    {
        var codec = this.Quality.AudioCodec;
        try
        {
            while (await session.Captured.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (session.Captured.TryRead(out var pcm))
                {
                    if (this.muted)
                        continue;

                    var payload = codec == CallAudioCodec.ImaAdpcm ? this.adpcm.Encode(pcm.Span) : pcm;
                    this.Link.Send(CallProtocol.Audio(this.Id, new AudioPacket(
                        this.audioSeq++,
                        codec,
                        (uint)this.clock.ElapsedMilliseconds,
                        payload
                    )));
                    Interlocked.Increment(ref this.audioSent);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Call audio capture stopped");
        }
    }


    void OnAudio(AudioPacket packet)
    {
        if (this.State != CallState.Active || this.audioSession is not { } session)
            return;

        Interlocked.Increment(ref this.audioReceived);
        if (packet.Codec == CallAudioCodec.ImaAdpcm)
            session.Play(ImaAdpcm.Decode(packet.Payload.Span));
        else
            session.Play(packet.Payload.Span);
    }


    void OnVideo(VideoPacket packet)
    {
        if (this.State != CallState.Active || this.VideoCodec == null)
            return;

        Interlocked.Increment(ref this.videoReceived);
        this.videoSink?.Render(new CallVideoFrame(
            packet.Codec,
            packet.Payload,
            packet.Width,
            packet.Height,
            packet.Rotation,
            packet.IsMirrored,
            packet.IsKeyFrame,
            TimeSpan.FromMilliseconds(packet.Timestamp)
        ));
    }


    public void AttachVideo(ICallVideoSource? source, ICallVideoSink? sink)
    {
        lock (this.gate)
        {
            if (!ReferenceEquals(this.videoSource, source))
            {
                if (this.videoSourceRunning)
                    this.videoSource?.Stop();
                this.videoSourceRunning = false;
                this.videoSource = source;
            }
            if (!ReferenceEquals(this.videoSink, sink))
            {
                if (this.videoSink != null)
                    this.videoSink.KeyFrameNeeded -= this.OnSinkNeedsKeyFrame;
                this.videoSink = sink;
                if (sink != null)
                    sink.KeyFrameNeeded += this.OnSinkNeedsKeyFrame;
            }
        }
        this.UpdateVideoSource();
        if (sink != null && this.State == CallState.Active)
            this.Link.Send(CallProtocol.KeyFrameRequest(this.Id));
    }


    void OnSinkNeedsKeyFrame(object? sender, EventArgs e)
    {
        if (this.State == CallState.Active)
            this.Link.Send(CallProtocol.KeyFrameRequest(this.Id));
    }


    // the camera runs while the call is active with video and the camera is on
    void UpdateVideoSource()
    {
        ICallVideoSource? start = null;
        ICallVideoSource? stopSource = null;
        lock (this.gate)
        {
            var shouldRun = this.State == CallState.Active && this.VideoCodec != null && this.cameraOn && this.videoSource != null;
            if (shouldRun && !this.videoSourceRunning)
                start = this.videoSource;
            else if (!shouldRun && this.videoSourceRunning)
                stopSource = this.videoSource;
            this.videoSourceRunning = shouldRun;
        }

        try
        {
            stopSource?.Stop();
            start?.Start(this.Quality.ToVideo(this.VideoCodec!.Value), this.OnFrameEncoded);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "The call's camera could not change");
        }
    }


    void OnFrameEncoded(CallVideoFrame frame)
    {
        if (this.State != CallState.Active || !this.cameraOn)
            return;

        this.Link.Send(CallProtocol.Video(this.Id, new VideoPacket(
            this.videoSeq++,
            frame.Codec,
            frame.IsKeyFrame,
            frame.IsMirrored,
            frame.Rotation,
            frame.Width,
            frame.Height,
            (uint)this.clock.ElapsedMilliseconds,
            frame.Data
        )));
        Interlocked.Increment(ref this.videoSent);
    }


    internal void RequestKeyFrame()
    {
        if (this.videoSourceRunning)
            this.videoSource?.RequestKeyFrame();
    }


    void SendMediaState()
    {
        if (this.State == CallState.Active)
            this.Link.Send(CallProtocol.MediaState(this.Id, this.muted, this.cameraOn));
    }


    // ---- ending ----

    void StartRingTimer()
    {
        var timer = new CancellationTokenSource(this.options.RingTimeout);
        this.ringTimer = timer;
        timer.Token.Register(() =>
        {
            if (this.State is not (CallState.Dialing or CallState.Ringing or CallState.Incoming))
                return;

            if (this.Direction == CallDirection.Outgoing)
                this.Link.Send(CallProtocol.HangUp(this.Id, CallEndReason.Missed, null));
            _ = this.Finish(new CallEnd(CallEndReason.Missed, null, this.Direction == CallDirection.Incoming));
        });
    }


    internal void StartIncomingRingTimer() => this.StartRingTimer();


    internal async Task Finish(CallEnd end)
    {
        ICallAudioSession? session;
        ICallVideoSource? source;
        ICallVideoSink? sink;
        lock (this.gate)
        {
            if (this.State == CallState.Ended)
                return;

            this.End = end;
            this.SetState(CallState.Ended);
            this.clock.Stop();
            session = this.audioSession;
            this.audioSession = null;
            source = this.videoSourceRunning ? this.videoSource : null;
            this.videoSourceRunning = false;
            sink = this.videoSink;
        }

        this.stop.Cancel();
        this.ringTimer?.Dispose();
        try
        {
            source?.Stop();
            sink?.Clear();
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Video did not stop cleanly");
        }

        if (session != null)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "Audio did not stop cleanly");
            }
        }

        this.owner.OnEnded(this);
        this.completion.TrySetResult(end);
    }


    // under gate (or before the call is shared). Events are queued, so handlers see changes in order and off the lock.
    void SetState(CallState state)
    {
        this.State = state;
        this.events.Writer.TryWrite(() => this.StateChanged?.Invoke(this, state));
        if (state == CallState.Ended)
            this.events.Writer.TryComplete();
    }


    async Task RaiseEvents()
    {
        await foreach (var raise in this.events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning(ex, "Call event handler failed");
            }
        }
    }


    public override string ToString() => $"{this.Direction} call {this.Id:N} with {this.RemoteName ?? this.RemoteConnectionId} ({this.State})";
}
