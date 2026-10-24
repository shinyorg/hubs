using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Shiny.BluetoothLE.Hubs.Protocol;

/// <summary>
/// A media channel over a byte stream (an L2CAP channel), framed by <see cref="MediaFrameCodec"/>. Transport-free: the
/// owner feeds it received bytes (<see cref="OnData"/>) and reports the stream closing (<see cref="Close"/>); it writes
/// through the delegate it was given.
/// </summary>
internal sealed class FramedMediaChannel : IBleHubMediaChannel
{
    readonly Func<byte[], CancellationToken, Task> write;
    readonly Action? onClose;
    readonly MediaFrameCodec decoder;
    readonly int maxWriteSize;
    readonly ILogger? logger;
    readonly Channel<ReadOnlyMemory<byte>> inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });
    readonly SemaphoreSlim sendLock = new(1, 1);
    readonly CancellationTokenSource lifetime = new();
    readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    IDisposable? subscription;
    int closed;
    int reading;


    /// <param name="maxPacketSize">The largest packet either side may send</param>
    /// <param name="write">Writes bytes to the stream, in call order</param>
    /// <param name="onClose">Closes the underlying stream - called once</param>
    public FramedMediaChannel(
        int maxPacketSize,
        Func<byte[], CancellationToken, Task> write,
        Action? onClose,
        ILogger? logger = null,
        int maxWriteSize = MediaFrameCodec.DefaultMaxWriteSize
    )
    {
        this.MaxPacketSize = maxPacketSize;
        this.write = write;
        this.onClose = onClose;
        this.logger = logger;
        this.maxWriteSize = maxWriteSize;
        this.decoder = new MediaFrameCodec(maxPacketSize);
    }


    /// <summary>
    /// A channel fed by <paramref name="received"/> - subscribed right away, so nothing the far side sends is missed. The
    /// observable completing or failing closes the channel.
    /// </summary>
    public static FramedMediaChannel Over(
        IObservable<byte[]> received,
        int maxPacketSize,
        Func<byte[], CancellationToken, Task> write,
        Action? onClose,
        ILogger? logger = null
    )
    {
        var channel = new FramedMediaChannel(maxPacketSize, write, onClose, logger);
        channel.subscription = received.Subscribe(new Observer(channel));
        if (!channel.IsOpen)
            channel.subscription.Dispose();

        return channel;
    }


    public string? ConnectionId { get; internal set; }
    public int MaxPacketSize { get; }
    public bool IsOpen => Volatile.Read(ref this.closed) == 0;
    public Task Completion => this.completion.Task;


    public ValueTask Send(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (packet.Length > this.MaxPacketSize)
            throw new ArgumentException($"Packet is {packet.Length} bytes which exceeds the media channel's MaxPacketSize ({this.MaxPacketSize})", nameof(packet));

        return this.SendFrame(packet, cancellationToken);
    }


    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Receive([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref this.reading, 1) == 1)
            throw new InvalidOperationException("The media channel already has a reader");

        try
        {
            await foreach (var packet in this.inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return packet;
        }
        finally
        {
            Volatile.Write(ref this.reading, 0);
        }
    }


    public ValueTask DisposeAsync()
    {
        this.Close();
        return ValueTask.CompletedTask;
    }


    /// <summary>
    /// Bytes read from the stream
    /// </summary>
    public void OnData(ReadOnlySpan<byte> data)
    {
        if (!this.IsOpen)
            return;

        try
        {
            IReadOnlyList<byte[]> frames;
            lock (this.decoder)
                frames = this.decoder.Add(data);

            foreach (var frame in frames)
                this.inbound.Writer.TryWrite(frame);
        }
        catch (BleHubProtocolException ex)
        {
            this.logger?.LogWarning(ex, "Bad media frame - closing the channel");
            this.Close();
        }
    }


    /// <summary>
    /// Closes the channel: the reader completes once it has drained what arrived, sends fail, the stream is closed
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref this.closed, 1) == 1)
            return;

        this.inbound.Writer.TryComplete();
        try { this.lifetime.Cancel(); }
        catch (ObjectDisposedException) { }

        this.subscription?.Dispose();
        try
        {
            this.onClose?.Invoke();
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Media stream did not close cleanly");
        }
        this.completion.TrySetResult();
    }


    // ---- opening handshake (before the channel is handed out) ----

    /// <summary>
    /// Sends a control frame (the token, or the host's answer) - framed like a packet, but not held to MaxPacketSize
    /// </summary>
    internal ValueTask SendControl(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) => this.SendFrame(frame, cancellationToken);


    /// <summary>
    /// Reads the next frame as a control frame. Throws <see cref="BleHubException"/> when the channel closes first and
    /// <see cref="TimeoutException"/> when nothing arrives in time.
    /// </summary>
    internal async Task<byte[]> ReadControl(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var frame = await this.inbound.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
            return frame.ToArray();
        }
        catch (ChannelClosedException)
        {
            throw new BleHubException("The media channel was closed before it opened");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The media channel did not open within {timeout}");
        }
    }


    async ValueTask SendFrame(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (!this.IsOpen)
            throw new BleHubException("The media channel is closed");

        var writes = MediaFrameCodec.Encode(packet.Span, this.maxWriteSize);
        await this.sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!this.IsOpen)
                throw new BleHubException("The media channel is closed");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
            try
            {
                foreach (var bytes in writes)
                    await this.write(bytes, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // half a frame is on the wire - the far side can't find the next frame boundary, so the channel is done
                this.Close();
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;

                throw new BleHubException("The media channel is closed", ex);
            }
        }
        finally
        {
            this.sendLock.Release();
        }
    }


    sealed class Observer(FramedMediaChannel channel) : IObserver<byte[]>
    {
        public void OnNext(byte[] value) => channel.OnData(value);
        public void OnError(Exception error)
        {
            channel.logger?.LogDebug(error, "Media stream failed");
            channel.Close();
        }
        public void OnCompleted() => channel.Close();
    }
}
