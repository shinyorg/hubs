using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// A media channel over the hub connection's own Multipeer session: one MediaPacket message is one packet. Reliable and
/// ordered like L2CAP and the Wi-Fi WebSocket, so signalling can share the pipe. The same class serves both ends.
/// </summary>
internal sealed class MultipeerMediaChannel(Action<MultipeerMessageKind, ReadOnlyMemory<byte>> send, string? connectionId, int maxPacketSize) : IBleHubMediaChannel
{
    readonly Channel<ReadOnlyMemory<byte>> inbox = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });
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
            throw new BleHubDisconnectedException("The media channel is closed");

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            send(MultipeerMessageKind.MediaPacket, packet);
        }
        catch (Exception ex)
        {
            this.Closed();
            throw new BleHubDisconnectedException($"The media channel is closed: {ex.Message}");
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
        if (this.IsOpen)
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
                send(MultipeerMessageKind.MediaClose, ReadOnlyMemory<byte>.Empty);
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
