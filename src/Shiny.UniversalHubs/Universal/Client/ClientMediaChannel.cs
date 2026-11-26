using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// The client end of a media channel over Wi-Fi: a WebSocket where one binary message is one packet
/// </summary>
internal sealed class ClientMediaChannel(ClientWebSocket socket, HttpMessageInvoker? invoker, int maxPacketSize) : IBleHubMediaChannel
{
    readonly SemaphoreSlim sending = new(1, 1);
    readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int receiving;
    int disposed;


    /// <summary>
    /// Connects to <c>{BasePath}/media</c> with the handshake's token. <paramref name="handlerFactory"/> is the transport's
    /// HTTP stack when it has a custom one (tests); otherwise the WebSocket uses its own sockets, which every platform supports.
    /// </summary>
    public static async Task<ClientMediaChannel> Connect(Uri mediaUri, string token, int maxPacketSize, Func<HttpMessageHandler>? handlerFactory, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(UniversalHubProtocol.MediaTokenHeader, token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var invoker = handlerFactory is { } factory ? new HttpMessageInvoker(factory()) : null;
        try
        {
            await socket.ConnectAsync(mediaUri, invoker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            invoker?.Dispose();
            throw new BleHubException($"Could not open a media channel: {ex.Message}", ex);
        }
        return new ClientMediaChannel(socket, invoker, maxPacketSize);
    }


    /// <summary>
    /// Media WebSockets live next to the hub routes: http becomes ws, https wss
    /// </summary>
    public static Uri ToWebSocketUri(Uri mediaUri)
    {
        var builder = new UriBuilder(mediaUri)
        {
            Scheme = mediaUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
        };
        return builder.Uri;
    }


    public string? ConnectionId => null;
    public int MaxPacketSize => maxPacketSize;
    public bool IsOpen => !this.completion.Task.IsCompleted && socket.State == WebSocketState.Open;
    public Task Completion => this.completion.Task;


    public async ValueTask Send(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (packet.Length > maxPacketSize)
            throw new ArgumentException($"Packets are limited to {maxPacketSize} bytes", nameof(packet));
        if (!this.IsOpen)
            throw new BleHubDisconnectedException("The media channel is closed");

        await this.sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.sending.Release();
        }
    }


    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Receive([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref this.receiving, 1) == 1)
            throw new InvalidOperationException("A media channel has one reader");

        var buffer = new byte[Math.Min(maxPacketSize, 64 * 1024)];
        var message = new MemoryStream();
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer, 0, result.Count);
                if (message.Length > maxPacketSize)
                    break;   // the host broke the limit - the channel is unusable

                if (!result.EndOfMessage)
                    continue;

                var packet = message.ToArray();
                message.SetLength(0);
                if (result.MessageType == WebSocketMessageType.Binary)
                    yield return packet;
            }
        }
        finally
        {
            this.completion.TrySetResult();
        }
    }


    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

        this.completion.TrySetResult();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // already gone
        }
        socket.Dispose();
        invoker?.Dispose();
    }
}
