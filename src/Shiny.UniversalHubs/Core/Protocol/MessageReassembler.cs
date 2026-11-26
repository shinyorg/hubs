namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// Rebuilds logical messages from frames for a single peer. Not thread safe - feed it from one place per peer.
/// </summary>
public sealed class MessageReassembler(
    int maxPayloadSize,
    TimeSpan timeout,
    int maxPartialMessages = 16,
    TimeProvider? timeProvider = null
)
{
    readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    readonly Dictionary<ushort, Partial> partials = new();


    public int PendingCount => this.partials.Count;


    /// <summary>
    /// Adds a frame. Returns the message once its last frame arrives, otherwise null.
    /// Throws <see cref="BleHubProtocolException"/> (with the offending message id when known) for bad frames - the partial message is dropped.
    /// </summary>
    public BleHubMessage? Add(ReadOnlySpan<byte> frame)
    {
        this.PurgeExpired();
        var header = FrameCodec.ReadHeader(frame);
        var chunk = frame[header.HeaderSize..];

        if (header.IsFirst)
        {
            // a new first frame for an id in flight replaces it - the sender gave up on the old one
            this.partials.Remove(header.MessageId);

            if (header.TotalLength > maxPayloadSize + 256)
                throw new BleHubProtocolException($"Message {header.MessageId} is {header.TotalLength} bytes which exceeds the {maxPayloadSize} byte limit", header.MessageId);

            if (header.TotalLength < chunk.Length)
                throw new BleHubProtocolException($"Message {header.MessageId} first frame is larger than its declared length", header.MessageId);

            var body = new byte[header.TotalLength];
            chunk.CopyTo(body);

            if (header.IsLast)
            {
                if (chunk.Length != body.Length)
                    throw new BleHubProtocolException($"Message {header.MessageId} ended before its declared length", header.MessageId);

                return FrameCodec.ParseBody(header.Kind, header.MessageId, body);
            }

            if (this.partials.Count >= maxPartialMessages)
                throw new BleHubProtocolException("Too many partial messages in flight", header.MessageId);

            this.partials[header.MessageId] = new Partial(header.Kind, body, chunk.Length, 1, this.time.GetUtcNow());
            return null;
        }

        if (!this.partials.TryGetValue(header.MessageId, out var partial))
            throw new BleHubProtocolException($"Continuation frame for unknown message {header.MessageId}", header.MessageId);

        if (partial.Kind != header.Kind || partial.NextSequence != header.Sequence)
        {
            this.partials.Remove(header.MessageId);
            throw new BleHubProtocolException($"Out of order frame for message {header.MessageId} (expected {partial.NextSequence}, got {header.Sequence})", header.MessageId);
        }

        if (partial.Received + chunk.Length > partial.Body.Length)
        {
            this.partials.Remove(header.MessageId);
            throw new BleHubProtocolException($"Message {header.MessageId} overflowed its declared length", header.MessageId);
        }

        chunk.CopyTo(partial.Body.AsSpan(partial.Received));
        partial.Received += chunk.Length;
        partial.NextSequence++;
        partial.LastActivity = this.time.GetUtcNow();

        if (!header.IsLast)
            return null;

        this.partials.Remove(header.MessageId);
        if (partial.Received != partial.Body.Length)
            throw new BleHubProtocolException($"Message {header.MessageId} ended before its declared length", header.MessageId);

        return FrameCodec.ParseBody(header.Kind, header.MessageId, partial.Body);
    }


    public void Clear() => this.partials.Clear();


    void PurgeExpired()
    {
        if (this.partials.Count == 0)
            return;

        var now = this.time.GetUtcNow();
        foreach (var pair in this.partials.ToList())
        {
            if (now - pair.Value.LastActivity > timeout)
                this.partials.Remove(pair.Key);
        }
    }


    sealed class Partial(FrameKind kind, byte[] body, int received, ushort nextSequence, DateTimeOffset lastActivity)
    {
        public FrameKind Kind { get; } = kind;
        public byte[] Body { get; } = body;
        public int Received { get; set; } = received;
        public ushort NextSequence { get; set; } = nextSequence;
        public DateTimeOffset LastActivity { get; set; } = lastActivity;
    }
}
