using Microsoft.Extensions.Logging;

namespace Sample.Calls;

/// <summary>
/// Paces one media channel. Signalling and audio go out first, in order. Video is latest-wins, one waiting frame per
/// call: a newer frame replaces the one still waiting. Dropping a delta frame breaks the decoder's chain, so after one
/// is dropped nothing more of that call's video goes out until a key frame, and <see cref="KeyFrameNeeded"/> asks
/// the encoder for one.
/// Audio that queues up past <see cref="MaxQueuedAudio"/> packets loses its oldest, so a slow link costs a gap rather
/// than ever-growing latency.
/// </summary>
internal sealed class MediaSender : IAsyncDisposable
{
    readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send;
    readonly ILogger? logger;
    readonly object gate = new();
    readonly LinkedList<byte[]> ordered = new();
    readonly Dictionary<Guid, PendingVideo> video = new();
    readonly HashSet<Guid> awaitingKey = new();
    readonly SemaphoreSlim signal = new(0);
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    int queuedAudio;


    public MediaSender(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send, ILogger? logger = null)
    {
        this.send = send;
        this.logger = logger;
        this.loop = Task.Run(this.Run);
    }


    /// <summary>
    /// 10 packets of 20 ms - 200 ms of audio
    /// </summary>
    public int MaxQueuedAudio { get; init; } = 10;

    /// <summary>
    /// A call's video lost a frame and needs a key frame to recover. Raised on the sending thread.
    /// </summary>
    public event Action<Guid>? KeyFrameNeeded;

    public long PacketsSent => Interlocked.Read(ref this.packetsSent);
    public long BytesSent => Interlocked.Read(ref this.bytesSent);
    public long VideoFramesDropped => Interlocked.Read(ref this.videoDropped);
    public long AudioPacketsDropped => Interlocked.Read(ref this.audioDropped);

    long packetsSent;
    long bytesSent;
    long videoDropped;
    long audioDropped;

    /// <summary>
    /// The send loop ended: disposed, or the channel failed
    /// </summary>
    public Task Completion => this.loop;


    /// <summary>
    /// Queues any call packet, routing it by type. Never blocks.
    /// </summary>
    public void Enqueue(byte[] packet)
    {
        if (!CallProtocol.TryReadHeader(packet, out _, out var type, out var callId))
            return;

        var needKey = false;
        lock (this.gate)
        {
            if (this.stop.IsCancellationRequested)
                return;

            if (type == CallPacketType.Video)
            {
                needKey = this.QueueVideo(callId, packet);
            }
            else
            {
                if (type == CallPacketType.Audio)
                {
                    if (this.queuedAudio >= this.MaxQueuedAudio && this.DropOldestAudio())
                        Interlocked.Increment(ref this.audioDropped);
                    this.queuedAudio++;
                }
                else if (type is CallPacketType.HangUp or CallPacketType.Decline)
                {
                    // nothing more of this call's video is worth sending
                    this.video.Remove(callId);
                    this.awaitingKey.Remove(callId);
                }
                this.ordered.AddLast(packet);
            }
            this.signal.Release();
        }

        if (needKey)
            this.RaiseKeyFrameNeeded(callId);
    }


    // under gate: returns true when this call now needs a key frame
    bool QueueVideo(Guid callId, byte[] packet)
    {
        var isKey = IsKeyFrame(packet);
        if (!isKey && this.awaitingKey.Contains(callId))
        {
            Interlocked.Increment(ref this.videoDropped);
            return false;
        }
        if (isKey)
            this.awaitingKey.Remove(callId);

        if (this.video.TryGetValue(callId, out var waiting))
        {
            Interlocked.Increment(ref this.videoDropped);
            if (waiting.IsKey && !isKey)
            {
                // keep the key frame - the delta that refers to it is the one lost
                this.awaitingKey.Add(callId);
                return true;
            }
            this.video[callId] = new PendingVideo(packet, isKey);
            if (!isKey && !waiting.IsKey)
            {
                // the waiting delta is lost, and the new one refers to it
                this.video.Remove(callId);
                Interlocked.Increment(ref this.videoDropped);
                this.awaitingKey.Add(callId);
                return true;
            }
            return false;
        }

        this.video[callId] = new PendingVideo(packet, isKey);
        return false;
    }


    bool DropOldestAudio()
    {
        for (var node = this.ordered.First; node != null; node = node.Next)
        {
            if (node.Value[1] == (byte)CallPacketType.Audio)
            {
                this.ordered.Remove(node);
                this.queuedAudio--;
                return true;
            }
        }
        return false;
    }


    static bool IsKeyFrame(byte[] packet)
        => packet.Length > CallProtocol.HeaderSize + 3 && (packet[CallProtocol.HeaderSize + 3] & 1) != 0;


    async Task Run()
    {
        var ct = this.stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await this.signal.WaitAsync(ct).ConfigureAwait(false);
                byte[]? next;
                lock (this.gate)
                {
                    next = this.TakeNext();
                }
                if (next == null)
                    continue;

                await this.send(next, ct).ConfigureAwait(false);
                Interlocked.Increment(ref this.packetsSent);
                Interlocked.Add(ref this.bytesSent, next.Length);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Media channel send failed");
        }
    }


    // under gate: signalling and audio first, then one waiting video frame
    byte[]? TakeNext()
    {
        if (this.ordered.First is { } first)
        {
            this.ordered.RemoveFirst();
            if (first.Value[1] == (byte)CallPacketType.Audio)
                this.queuedAudio--;
            return first.Value;
        }
        foreach (var pair in this.video)
        {
            this.video.Remove(pair.Key);
            return pair.Value.Packet;
        }
        return null;
    }


    void RaiseKeyFrameNeeded(Guid callId)
    {
        try
        {
            this.KeyFrameNeeded?.Invoke(callId);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "KeyFrameNeeded handler failed");
        }
    }


    public async ValueTask DisposeAsync()
    {
        lock (this.gate)
        {
            if (this.stop.IsCancellationRequested)
                return;
            this.stop.Cancel();
        }
        await this.loop.ConfigureAwait(false);
        this.stop.Dispose();
        this.signal.Dispose();
    }


    readonly record struct PendingVideo(byte[] Packet, bool IsKey);
}
