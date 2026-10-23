using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;

namespace Sample.Calls;

/// <summary>
/// One hub media channel as calls use it: a paced sender, and a receive loop handing each packet on in order
/// </summary>
internal sealed class MediaLink : IAsyncDisposable
{
    readonly IBleHubMediaChannel channel;
    readonly MediaSender sender;
    readonly CancellationTokenSource stop = new();
    Task? receiving;


    /// <param name="id">Host side: the client's connection id. Client side: empty - the link goes to the host.</param>
    public MediaLink(IBleHubMediaChannel channel, string id, CallProfile profile, ILogger? logger)
    {
        this.channel = channel;
        this.Id = id;
        this.Profile = profile;
        this.sender = new MediaSender((packet, ct) => channel.Send(packet, ct), logger);
        this.sender.KeyFrameNeeded += callId => this.KeyFrameNeeded?.Invoke(this, callId);
    }


    public string Id { get; }

    /// <summary>
    /// The quality this link can carry - BLE or Wi-Fi
    /// </summary>
    public CallProfile Profile { get; }

    public MediaSender Sender => this.sender;

    /// <summary>
    /// A call's outgoing video lost a frame on this link
    /// </summary>
    public event Action<MediaLink, Guid>? KeyFrameNeeded;


    public void Send(byte[] packet) => this.sender.Enqueue(packet);


    /// <summary>
    /// Starts reading. <paramref name="onPacket"/> runs for one packet at a time; <paramref name="onClosed"/> runs once,
    /// when the channel ends.
    /// </summary>
    public void Start(Func<MediaLink, ReadOnlyMemory<byte>, Task> onPacket, Func<MediaLink, Task> onClosed, ILogger? logger)
        => this.receiving = Task.Run(async () =>
        {
            try
            {
                await foreach (var packet in this.channel.Receive(this.stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await onPacket(this, packet).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Dropped a call packet");
                    }
                }
            }
            catch (OperationCanceledException) when (this.stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Media channel {Id} ended", this.Id);
            }

            try
            {
                await onClosed(this).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Media channel close handling failed");
            }
        });


    public async ValueTask DisposeAsync()
    {
        if (this.stop.IsCancellationRequested)
            return;

        this.stop.Cancel();
        await this.sender.DisposeAsync().ConfigureAwait(false);
        await this.channel.DisposeAsync().ConfigureAwait(false);
    }
}
