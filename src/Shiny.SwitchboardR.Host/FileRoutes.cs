using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;

namespace Shiny.SwitchboardR;

/// <summary>
/// Files over Wi-Fi: plain HTTP, under the same BleHubHostOptions.EnableFileTransfers settings L2CAP uses
/// <code>
/// PUT {BasePath}/files/{name}   body = the file, Content-Length required
/// GET {BasePath}/files/{name}
/// </code>
/// Every request carries the file token from the caller's handshake reply.
/// </summary>
internal static class FileRoutes
{
    public static void Map(HttpServer server, SwitchboardRHostOptions options)
    {
        var pattern = SwitchboardRProtocol.FilesPath(options.BasePath) + "/{name}";
        server.MapRoute(HttpMethods.Put, pattern, ctx => Upload(ctx, options), new DisableRequestTimeoutAttribute());
        server.MapRoute(HttpMethods.Get, pattern, ctx => Download(ctx, options), new DisableRequestTimeoutAttribute());
    }


    static async ValueTask Upload(HttpContext ctx, SwitchboardRHostOptions options)
    {
        if (Prepare(ctx, out var ft, out var client, out var name, out var path) is { } refused)
        {
            await Refuse(ctx, refused);
            return;
        }

        var length = ctx.Request.ContentLength;
        if (!ft!.AllowUploads)
        {
            await Refuse(ctx, (StatusCodes.Status403Forbidden, "Uploads are not allowed"));
            return;
        }
        if (length == null)
        {
            await Refuse(ctx, (StatusCodes.Status411LengthRequired, "Content-Length is required"));
            return;
        }
        if (ft.MaxUploadSize is { } max && length > max)
        {
            await Refuse(ctx, (StatusCodes.Status413PayloadTooLarge, $"Uploads are limited to {max} bytes"));
            return;
        }
        if (!ft.OverwriteExistingUploads && File.Exists(path))
        {
            await Refuse(ctx, (StatusCodes.Status409Conflict, "The file already exists"));
            return;
        }
        if (options.AuthorizeFile?.Invoke(new SwitchboardRFileRequest(L2CapTransferType.Upload, name!, length, client!)) == false)
        {
            await Refuse(ctx, (StatusCodes.Status403Forbidden, "The host refused the transfer"));
            return;
        }

        ctx.Request.MaxBodySize = length;
        Directory.CreateDirectory(ft.RootDirectory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        var sw = Stopwatch.StartNew();
        long written;
        try
        {
            await using (var file = File.Create(temp))
            {
                await ctx.Request.Body.CopyToAsync(file, ctx.RequestAborted);
                written = file.Length;
            }
            if (written != length)
            {
                await Refuse(ctx, (StatusCodes.Status400BadRequest, $"Expected {length} bytes but received {written}"));
                return;
            }
            File.Move(temp, path!, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        Raise(ctx, client!, L2CapTransferType.Upload, name!, path!, written, sw.Elapsed);
    }


    static async ValueTask Download(HttpContext ctx, SwitchboardRHostOptions options)
    {
        if (Prepare(ctx, out var ft, out var client, out var name, out var path) is { } refused)
        {
            await Refuse(ctx, refused);
            return;
        }
        if (!ft!.AllowDownloads)
        {
            await Refuse(ctx, (StatusCodes.Status403Forbidden, "Downloads are not allowed"));
            return;
        }
        if (!File.Exists(path))
        {
            await Refuse(ctx, (StatusCodes.Status404NotFound, "No such file"));
            return;
        }
        if (options.AuthorizeFile?.Invoke(new SwitchboardRFileRequest(L2CapTransferType.Download, name!, null, client!)) == false)
        {
            await Refuse(ctx, (StatusCodes.Status403Forbidden, "The host refused the transfer"));
            return;
        }

        var sw = Stopwatch.StartNew();
        await using var file = File.OpenRead(path!);
        var length = file.Length;
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = length;
        await file.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);

        Raise(ctx, client!, L2CapTransferType.Download, name!, path!, length, sw.Elapsed);
    }


    static (int Status, string Message)? Prepare(
        HttpContext ctx,
        out BleHubFileTransferOptions? ft,
        out BleHubConnectedClient? client,
        out string? name,
        out string? path
    )
    {
        ft = null;
        client = null;
        path = null;
        name = ctx.Request.RouteValues["name"];

        var host = ctx.RequestServices.GetRequiredService<SwitchboardRHost>();
        ft = ctx.RequestServices.GetRequiredService<BleHubHostOptions>().FileTransfers;
        if (!host.IsWifiRunning || ft == null)
            return (StatusCodes.Status404NotFound, "This host does not serve files");

        client = ctx.RequestServices.GetRequiredService<FileTokens>().Find(ctx.Request.Headers[SwitchboardRProtocol.FileTokenHeader].ToString());
        if (client == null)
            return (StatusCodes.Status401Unauthorized, "A file token from a connected hub client is required");

        if (String.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0 || Path.GetFileName(name) != name)
            return (StatusCodes.Status400BadRequest, "Invalid file name");

        var root = Path.GetFullPath(ft.RootDirectory);
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return (StatusCodes.Status400BadRequest, "Invalid file name");

        path = full;
        return null;
    }


    static async ValueTask Refuse(HttpContext ctx, (int Status, string Message) refusal)
    {
        ctx.Response.StatusCode = refusal.Status;
        await ctx.Response.WriteTextAsync(refusal.Message);
    }


    static void Raise(HttpContext ctx, BleHubConnectedClient client, L2CapTransferType type, string name, string path, long bytes, TimeSpan elapsed)
    {
        try
        {
            ctx.RequestServices.GetRequiredService<SwitchboardRHost>().OnFileTransferred(
                new BleHubFileTransferredEventArgs(client, client.Id, type, name, path, bytes, elapsed)
            );
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetService<ILogger<SwitchboardRHost>>()?.LogWarning(ex, "FileTransferred handler failed");
        }
    }
}
