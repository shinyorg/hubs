using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Shiny.UniversalHubs;

/// <summary>
/// A media channel over the hub connection's own peer-link session (Multipeer, Nearby): one MediaPacket message is one packet. Reliable and
/// ordered like L2CAP, so signalling can share the pipe. The same class serves both ends. Packets nobody reads fast enough
/// are dropped oldest first - it is live media - and a packet over the size limit is dropped.
/// </summary>
internal sealed class PeerMediaChannel(
    Action<PeerMessageKind, ReadOnlyMemory<byte>> send,
    string? connectionId,
    int maxPacketSize,
    int inboxCapacity = PeerMediaChannel.DefaultInboxCapacity
) : IHubMediaChannel
{
    /// <summary>
    /// Inbound packets kept for a slow reader before the oldest are dropped
    /// </summary>
    public const int DefaultInboxCapacity = 256;

    readonly Channel<ReadOnlyMemory<byte>> inbox = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(inboxCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int receiving;


    public string? ConnectionId => connectionId;
    public int MaxPacketSize => maxPacketSize;
    public bool IsOpen => !this.completion.Task.IsCompleted;
    public Task Completion => this.completion.Task;


    public ValueTask Send(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (packet.Length > maxPacketSize)
            throw new ArgumentException($"Packets are limited to {maxPacketSize} bytes", nameof(packet));
        if (!this.IsOpen)
            throw new HubDisconnectedException("The media channel is closed");

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            send(PeerMessageKind.MediaPacket, packet);
        }
        catch (Exception ex)
        {
            this.Closed();
            throw new HubDisconnectedException($"The media channel is closed: {ex.Message}");
        }
        return ValueTask.CompletedTask;
    }


    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Receive([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref this.receiving, 1) == 1)
            throw new InvalidOperationException("A media channel has one reader");

        await foreach (var packet in this.inbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return packet;
    }


    /// <summary>
    /// A packet from the other end
    /// </summary>
    public void Deliver(ReadOnlyMemory<byte> packet)
    {
        if (this.IsOpen && packet.Length <= maxPacketSize)
            this.inbox.Writer.TryWrite(packet);
    }


    /// <summary>
    /// The other end closed it, or the session went
    /// </summary>
    public void Closed()
    {
        this.inbox.Writer.TryComplete();
        this.completion.TrySetResult();
    }


    public ValueTask DisposeAsync()
    {
        if (!this.completion.Task.IsCompleted)
        {
            try
            {
                send(PeerMessageKind.MediaClose, ReadOnlyMemory<byte>.Empty);
            }
            catch
            {
                // the session is already gone
            }
        }
        this.Closed();
        return ValueTask.CompletedTask;
    }
}
