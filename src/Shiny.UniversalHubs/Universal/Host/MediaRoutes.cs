using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.WebSockets;

namespace Shiny.UniversalHubs;

/// <summary>
/// Media channels over Wi-Fi: a WebSocket per client, carrying binary packets both ways
/// <code>
/// GET {BasePath}/media   Upgrade: websocket   X-Hub-Media-Token: {token from the handshake reply}
/// </code>
/// One binary message is one packet. The channel reaches the hub through the blehubs seam, exactly as an L2CAP media
/// channel does over BLE.
/// </summary>
internal static class MediaRoutes
{
    public static void Map(HttpServer server, UniversalHubHostOptions options)
        => server.MapRoute(HttpMethods.Get, UniversalHubProtocol.MediaPath(options.BasePath), Accept, new DisableRequestTimeoutAttribute());


    static async ValueTask Accept(HttpContext ctx)
    {
        var host = ctx.RequestServices.GetRequiredService<UniversalHubHost>();
        var maxPacketSize = host.MediaMaxPacketSize;
        if (!host.IsHttpRunning || maxPacketSize == null)
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, "This host has no media channels");
            return;
        }

        var token = ctx.Request.Headers[UniversalHubProtocol.MediaTokenHeader].ToString();
        if (ctx.RequestServices.GetRequiredService<MediaTokens>().Find(token) is not { } entry)
        {
            await Refuse(ctx, StatusCodes.Status401Unauthorized, "A media token from a connected hub client is required");
            return;
        }
        if (ctx.Connection.IsTunneled && !entry.Binding.AllowTunnel)
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, "Not found");
            return;
        }
        if (!ctx.Request.IsWebSocketRequest())
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, "A WebSocket upgrade is required");
            return;
        }

        var accept = new WebSocketAcceptOptions
        {
            MaxMessageLength = maxPacketSize.Value,
            EnablePerMessageDeflate = false   // audio and video don't compress
        };
        await using var socket = await ctx.AcceptWebSocketAsync(accept);
        var channel = new ServerMediaChannel(socket, entry.ConnectionId, maxPacketSize.Value);

        if (!entry.Binding.OpenMedia(entry.ConnectionId, channel))
        {
            await channel.DisposeAsync();
            return;
        }

        // the connection lives as long as this handler does
        try
        {
            await channel.Completion.WaitAsync(ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
        await channel.DisposeAsync();
    }


    static async ValueTask Refuse(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteTextAsync(message);
    }
}


/// <summary>
/// One client's media channel, on the host
/// </summary>
internal sealed class ServerMediaChannel(WebSocket socket, string connectionId, int maxPacketSize) : IBleHubMediaChannel
{
    readonly SemaphoreSlim sending = new(1, 1);
    readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int receiving;

    public string? ConnectionId => connectionId;
    public int MaxPacketSize => maxPacketSize;
    public bool IsOpen => !this.completion.Task.IsCompleted && socket.IsOpen;
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
            await socket.SendAsync(packet, cancellationToken).ConfigureAwait(false);
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

        try
        {
            while (true)
            {
                WebSocketMessage? message;
                try
                {
                    message = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    break;
                }
                if (message == null)
                    break;
                if (!message.IsText)
                    yield return message.Payload;
            }
        }
        finally
        {
            this.completion.TrySetResult();
        }
    }


    public async ValueTask DisposeAsync()
    {
        if (!this.completion.TrySetResult())
            return;

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed").ConfigureAwait(false);
        }
        catch
        {
            // already gone
        }
    }
}


/// <summary>
/// Media tokens handed out in handshake replies - each opens one client's media channel
/// </summary>
internal sealed class MediaTokens
{
    readonly ConcurrentDictionary<string, (string ConnectionId, IHubBinding Binding)> tokens = new(StringComparer.Ordinal);

    public string Issue(string connectionId, IHubBinding binding)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        this.tokens[token] = (connectionId, binding);
        return token;
    }

    public (string ConnectionId, IHubBinding Binding)? Find(string? token)
        => !String.IsNullOrEmpty(token) && this.tokens.TryGetValue(token, out var entry) ? entry : null;

    public void Remove(string connectionId)
    {
        foreach (var pair in this.tokens.Where(x => x.Value.ConnectionId == connectionId).ToList())
            this.tokens.TryRemove(pair.Key, out _);
    }

    public void Clear() => this.tokens.Clear();
}
