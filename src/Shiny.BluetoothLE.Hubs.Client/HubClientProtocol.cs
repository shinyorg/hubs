using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs.Protocol;

namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Transport independent client side of the protocol - correlates replies with calls and surfaces host pushes
/// </summary>
internal sealed class HubClientProtocol(
    BleHubProtocolOptions options,
    Func<byte[], CancellationToken, Task> write,
    ILogger? logger = null
)
{
    readonly ConcurrentDictionary<ushort, Channel<BleHubMessage>> pending = new();
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly MessageReassembler reassembler = new(options.MaxPayloadSize, options.ReassemblyTimeout, options.MaxPartialMessages);
    int nextId;

    public int Mtu { get; set; } = 23;

    /// <summary>
    /// A push or a host rename from the host, in the order received
    /// </summary>
    public event Action<BleHubMessage>? Pushed;

    /// <summary>
    /// The host asked us to leave
    /// </summary>
    public event Action<DisconnectInfo>? DisconnectRequested;


    public void OnFrame(ReadOnlySpan<byte> frame)
    {
        BleHubMessage? message;
        try
        {
            lock (this.reassembler)
                message = this.reassembler.Add(frame);
        }
        catch (BleHubProtocolException ex)
        {
            logger?.LogWarning(ex, "Bad frame from host");
            if (ex.MessageId is { } id && this.pending.TryGetValue(id, out var ch))
                ch.Writer.TryComplete(ex);

            return;
        }

        if (message == null)
            return;

        switch (message.Kind)
        {
            case FrameKind.Push:
            case FrameKind.HostRenamed:
                this.Pushed?.Invoke(message);
                break;

            case FrameKind.Disconnect:
                this.DisconnectRequested?.Invoke(ProtocolSerializer.ReadDisconnect(message.Payload));
                break;

            default:
                if (this.pending.TryGetValue(message.MessageId, out var channel))
                    channel.Writer.TryWrite(message);
                else
                    logger?.LogDebug("Dropping {Kind} for unknown call {Id}", message.Kind, message.MessageId);
                break;
        }
    }


    /// <summary>
    /// Fails every call in flight - used when the connection drops
    /// </summary>
    public void FailAll(Exception exception)
    {
        foreach (var pair in this.pending.ToList())
            pair.Value.Writer.TryComplete(exception);

        lock (this.reassembler)
            this.reassembler.Clear();
    }


    public async Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken)
    {
        var reply = await this.Call(FrameKind.Handshake, null, ProtocolSerializer.Serialize(info), cancellationToken).ConfigureAwait(false);
        return reply.Kind == FrameKind.HandshakeAck
            ? ProtocolSerializer.ReadHandshakeAck(reply.Payload)
            : throw Unexpected(reply);
    }


    public async Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken)
    {
        var reply = await this.Call(FrameKind.Invoke, method, arguments, cancellationToken).ConfigureAwait(false);
        return reply.Kind == FrameKind.Completion ? reply.Payload : throw Unexpected(reply);
    }


    public async Task Rename(string? name, CancellationToken cancellationToken)
    {
        var reply = await this.Call(FrameKind.Rename, null, ProtocolSerializer.Serialize(new RenameInfo(name)), cancellationToken).ConfigureAwait(false);
        if (reply.Kind != FrameKind.Completion)
            throw Unexpected(reply);
    }


    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = this.NextId();
        var channel = Channel.CreateUnbounded<BleHubMessage>(new UnboundedChannelOptions { SingleReader = true });
        this.pending[id] = channel;
        var completed = false;

        try
        {
            await this.WriteMessage(FrameKind.StreamInvoke, id, method, arguments, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var message = await Read(channel, cancellationToken).ConfigureAwait(false);
                if (message.Kind == FrameKind.StreamEnd)
                {
                    completed = true;
                    yield break;
                }
                if (message.Kind != FrameKind.StreamItem)
                {
                    completed = true;
                    throw Unexpected(message);
                }
                yield return message.Payload;
            }
        }
        finally
        {
            this.pending.TryRemove(id, out _);
            if (!completed)
                this.SendCancel(id);
        }
    }


    async Task<BleHubMessage> Call(FrameKind kind, string? name, byte[] payload, CancellationToken cancellationToken)
    {
        var id = this.NextId();
        var channel = Channel.CreateUnbounded<BleHubMessage>(new UnboundedChannelOptions { SingleReader = true });
        this.pending[id] = channel;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            await this.WriteMessage(kind, id, name, payload, timeout.Token).ConfigureAwait(false);
            return await Read(channel, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            this.SendCancel(id);
            throw new TimeoutException($"No reply from the host within {options.RequestTimeout}");
        }
        catch (OperationCanceledException)
        {
            this.SendCancel(id);
            throw;
        }
        finally
        {
            this.pending.TryRemove(id, out _);
        }
    }


    async Task WriteMessage(FrameKind kind, ushort id, string? name, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > options.MaxPayloadSize)
            throw new BleHubException($"Payload is {payload.Length} bytes which exceeds MaxPayloadSize ({options.MaxPayloadSize}) - send large content as a file");

        var frames = FrameCodec.Encode(kind, id, name, payload, FrameCodec.GetFrameSize(this.Mtu));
        await this.writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var frame in frames)
                await write(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.writeLock.Release();
        }
    }


    /// <summary>
    /// Tells the host the client is leaving, so it isn't mistaken for a dropped link
    /// </summary>
    public Task SendDisconnect(HubDisconnect disconnect, CancellationToken cancellationToken)
        => this.WriteMessage(FrameKind.Disconnect, this.NextId(), null, ProtocolSerializer.Serialize(DisconnectInfo.From(disconnect)), cancellationToken);


    void SendCancel(ushort id)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await this.WriteMessage(FrameKind.Cancel, id, null, [], cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Could not send cancel for {Id}", id);
            }
        });
    }


    ushort NextId()
    {
        while (true)
        {
            var id = (ushort)(Interlocked.Increment(ref this.nextId) & 0x7FFF);
            if (id != 0 && !this.pending.ContainsKey(id))
                return id;
        }
    }


    static async Task<BleHubMessage> Read(Channel<BleHubMessage> channel, CancellationToken cancellationToken)
    {
        BleHubMessage message;
        try
        {
            message = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }

        if (message.Kind == FrameKind.Error)
        {
            var error = ProtocolSerializer.ReadError(message.Payload);
            throw new BleHubRemoteException(error.ErrorType, error.Message);
        }
        return message;
    }


    static BleHubProtocolException Unexpected(BleHubMessage message)
        => new($"Unexpected {message.Kind} reply", message.MessageId);
}
