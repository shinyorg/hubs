using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.BluetoothLE.Hubs.Protocol;
using Shiny.Net.HttpServer.Switchboard.Client;

namespace Shiny.UniversalHubs;

/// <summary>
/// Carries one hub connection over a Switchboard line. Calls go through hub.invoke / hub.stream, pushes arrive as
/// hub.push, and files use the host's plain HTTP routes.
/// </summary>
internal sealed class SwitchboardTransport : IBleHubClientTransport, IHubTransportKind
{
    public HubTransports Transport => HubTransports.Http;

    readonly IBleHubClientTransportEvents events;
    readonly HttpClient http;
    readonly SwitchboardLine line;
    readonly Uri filesUri;
    readonly Uri mediaUri;
    readonly UniversalHubClientOptions options;
    readonly ILogger? logger;
    readonly SemaphoreSlim mediaGate = new(1, 1);
    string? fileToken;
    string? mediaToken;
    int mediaMaxPacketSize;
    ClientMediaChannel? media;
    volatile bool closing;


    public SwitchboardTransport(
        Uri hubUri,
        Uri filesUri,
        Uri mediaUri,
        IBleHubClientTransportEvents events,
        UniversalHubClientOptions options,
        ILoggerFactory? loggerFactory
    )
    {
        this.events = events;
        this.filesUri = filesUri;
        this.mediaUri = ClientMediaChannel.ToWebSocketUri(mediaUri);
        this.options = options;
        this.logger = loggerFactory?.CreateLogger<SwitchboardTransport>();
        this.http = CreateHttpClient(options);

        var builder = new SwitchboardLineBuilder().WithUrl(hubUri, o => o.HttpClient = this.http);
        if (options.AutomaticReconnect)
            builder.WithAutomaticReconnect();
        if (loggerFactory != null)
            builder.WithLoggerFactory(loggerFactory);

        this.line = builder.Build();

        // one dispatch loop per line, so pushes are handed over one at a time, in order
        this.line.Handle(UniversalHubProtocol.PushMethod, args =>
        {
            this.events.Pushed(args.Get<string>(0), HubJson.ArgumentsFromJson(args.Get<JsonElement>(1)));
            return Task.CompletedTask;
        });
        this.line.Closed += (_, e) =>
        {
            if (this.closing)
                return;

            this.closing = true;
            this.events.Closed(ToHubDisconnect(e.Reason, e.Message));
            _ = Task.Run(this.DisposeAsync);
        };
    }


    /// <summary>
    /// The line's event stream stays open for the whole connection, so no overall timeout - HttpClient's default 100
    /// seconds would cut it (and any slow file transfer) off. Calls and transfers time out through their cancellation tokens.
    /// </summary>
    internal static HttpClient CreateHttpClient(UniversalHubClientOptions options)
    {
        var http = options.HttpMessageHandlerFactory is { } factory ? new HttpClient(factory()) : new HttpClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }


    public bool CanTransferFiles => this.fileToken != null;


    public async Task<HandshakeAck> Handshake(HandshakeInfo info, CancellationToken cancellationToken)
    {
        await this.line.StartAsync(cancellationToken).ConfigureAwait(false);
        var envelope = await this.line.InvokeCoreAsync<JsonElement>(
            UniversalHubProtocol.HandshakeMethod,
            [HubJson.Serialize(new HubHandshake(UniversalHubProtocol.Version, info.Name, info.AppVersion, info.Properties))],
            cancellationToken
        ).ConfigureAwait(false);

        var reply = HubJson.ReadHandshakeReply(envelope);
        this.fileToken = reply.FileToken;
        this.mediaToken = reply.MediaToken;
        this.mediaMaxPacketSize = reply.MediaMaxPacketSize ?? 0;
        this.HostId = reply.HostId;

        if (!reply.Accepted)
            await this.Shutdown().ConfigureAwait(false);

        return new HandshakeAck(reply.Accepted, reply.Reason, reply.HostName, 0, false);
    }


    /// <summary>
    /// The host's id, from the handshake reply
    /// </summary>
    public string? HostId { get; private set; }


    public async Task<ReadOnlyMemory<byte>> Invoke(string method, byte[] arguments, CancellationToken cancellationToken)
    {
        var envelope = await this.line.InvokeCoreAsync<JsonElement>(
            UniversalHubProtocol.InvokeMethod,
            [method, HubJson.ArgumentsToJson(arguments)],
            cancellationToken
        ).ConfigureAwait(false);

        return HubJson.ReadResult(envelope);
    }


    public async IAsyncEnumerable<ReadOnlyMemory<byte>> Stream(string method, byte[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var items = this.line.StreamCoreAsync<JsonElement>(
            UniversalHubProtocol.StreamMethod,
            [method, HubJson.ArgumentsToJson(arguments)],
            cancellationToken
        );
        await foreach (var envelope in items.ConfigureAwait(false))
            yield return HubJson.ReadItem(envelope);
    }


    public bool CanOpenMediaChannel => this.mediaToken != null && this.mediaMaxPacketSize > 0 && !this.closing;


    /// <summary>
    /// The media WebSocket, opened once per connection and closed with it
    /// </summary>
    public async Task<IBleHubMediaChannel> OpenMediaChannel(CancellationToken cancellationToken)
    {
        if (!this.CanOpenMediaChannel)
            throw new NotSupportedException("The host has no media channels");

        await this.mediaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.media is { IsOpen: true } open)
                return open;

            this.media = await ClientMediaChannel.Connect(
                this.mediaUri,
                this.mediaToken!,
                this.mediaMaxPacketSize,
                this.options.HttpMessageHandlerFactory,
                cancellationToken
            ).ConfigureAwait(false);
            return this.media;
        }
        finally
        {
            this.mediaGate.Release();
        }
    }


    public async Task<L2CapTransferResult> Upload(Stream source, long length, string remoteFileName, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var request = this.FileRequest(HttpMethod.Put, remoteFileName);
        request.Content = new ProgressContent(source, length, progress);

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "Upload", cancellationToken).ConfigureAwait(false);
        return new L2CapTransferResult(L2CapTransferType.Upload, remoteFileName, length, sw.Elapsed);
    }


    public async Task<L2CapTransferResult> Download(string remoteFileName, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var request = this.FileRequest(HttpMethod.Get, remoteFileName);
        using var response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "Download", cancellationToken).ConfigureAwait(false);

        var total = response.Content.Headers.ContentLength;
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var copied = await ProgressContent.Copy(body, destination, total, progress, cancellationToken).ConfigureAwait(false);
        return new L2CapTransferResult(L2CapTransferType.Download, remoteFileName, copied, sw.Elapsed);
    }


    public async Task Close(CancellationToken cancellationToken)
    {
        if (this.closing)
            return;

        await this.Shutdown().ConfigureAwait(false);
    }


    async Task Shutdown()
    {
        this.closing = true;
        await this.DisposeAsync().ConfigureAwait(false);
    }


    async Task DisposeAsync()
    {
        if (this.media is { } media)
            await media.DisposeAsync().ConfigureAwait(false);

        try
        {
            await this.line.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger?.LogDebug(ex, "Line did not close cleanly");
        }
        this.http.Dispose();
    }


    HttpRequestMessage FileRequest(HttpMethod method, string fileName)
    {
        var token = this.fileToken ?? throw new BleHubFileTransferNotSupportedException("The host does not serve file transfers");
        var request = new HttpRequestMessage(method, new Uri(this.filesUri.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(fileName)));
        request.Headers.Add(UniversalHubProtocol.FileTokenHeader, token);
        return request;
    }


    static async Task EnsureSuccess(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var message = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (String.IsNullOrWhiteSpace(message))
            message = response.ReasonPhrase ?? response.StatusCode.ToString();

        throw response.StatusCode == HttpStatusCode.NotFound && message.Contains("does not serve files", StringComparison.Ordinal)
            ? new BleHubFileTransferNotSupportedException(message)
            : new BleHubException($"{what} failed ({(int)response.StatusCode}): {message}");
    }


    /// <summary>
    /// A hang-up from the host carries its own reason; everything else maps from how the line closed
    /// </summary>
    internal static HubDisconnect ToHubDisconnect(LineCloseReason reason, string? message)
        => HubJson.ReadHangUp(message) ?? new HubDisconnect(reason switch
        {
            LineCloseReason.ClientClosed => HubDisconnectReason.ClientDisconnect,
            LineCloseReason.HungUp => HubDisconnectReason.ServerDisconnect,
            LineCloseReason.ServerClosed => HubDisconnectReason.ServerShutdown,
            LineCloseReason.Faulted => HubDisconnectReason.ConnectionFailed,
            _ => HubDisconnectReason.ClientTimeout   // dropped, or resume gave up
        }, message);


    /// <summary>
    /// Streams an upload with a known length, reporting progress as it goes
    /// </summary>
    sealed class ProgressContent : HttpContent
    {
        readonly Stream source;
        readonly long length;
        readonly IProgress<TransferProgress>? progress;

        public ProgressContent(Stream source, long length, IProgress<TransferProgress>? progress)
        {
            this.source = source;
            this.length = length;
            this.progress = progress;
            this.Headers.ContentLength = length;
            this.Headers.ContentType = new("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Copy(this.source, stream, this.length, this.progress, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => Copy(this.source, stream, this.length, this.progress, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = this.length;
            return true;
        }


        public static async Task<long> Copy(Stream from, Stream to, long? total, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long copied = 0;
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (progress != null && sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
                {
                    lastReport = sw.Elapsed;
                    progress.Report(new TransferProgress(Rate(copied, sw.Elapsed), total, copied));
                }
            }
            progress?.Report(new TransferProgress(Rate(copied, sw.Elapsed), total ?? copied, copied));
            return copied;
        }

        static long Rate(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 ? 0 : (long)(bytes / elapsed.TotalSeconds);
    }
}
