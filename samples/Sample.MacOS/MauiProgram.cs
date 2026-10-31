using AppKit;
using CoreGraphics;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Handlers;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Shiny;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer.Ssh;
using Shiny.UniversalHubs;
using Sample.Calls;
using Sample.Hub;
using Sample.Services;

namespace Sample.MacOS;

public static class MauiProgram
{
    // the same hubs as the phone app - see samples/Sample/MauiProgram.cs
    const string ServiceUuid = "37625845-7287-4de0-92f8-0b8a25e755ec";
    const string GameHubCharacteristicUuid = "37625846-7287-4de0-92f8-0b8a25e755ec";
    const string GameMdnsServiceType = "_unigame._tcp";
    const string ChatHubCharacteristicUuid = "37625851-7287-4de0-92f8-0b8a25e755ec";
    const string ChatMdnsServiceType = "_unichat._tcp";
    const string MultipeerServiceType = "unihub-mc";


    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiAppMacOS<App>()
            .AddMacOSEssentials()
            .UseShiny()
            .UseShinyShell(x => x.AddGeneratedMaps())
            .UseShinyControls()
            .UseShinyCamera()   // CameraView and RemoteVideoView, for calls
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddDebug();
        builder.Logging.AddConsole();
#endif

        Json.AddContext(GameJsonContext.Default);
        Json.AddContext(ChatJsonContext.Default);

        builder.Services.AddShinyActors(actors => actors.UseFileStorage(Path.Combine(FileSystem.AppDataDirectory, "actors")));

        // blehubs only registers the BLE stacks on Android, iOS and Mac Catalyst: register Shiny's macOS builds here
        builder.Services.AddBluetoothLE();
        builder.Services.AddBluetoothLeHosting();

        // ---- host: the same universal server as the phone app ----
        builder.Services.AddUniversalServer(server => server
            .ServiceUuid(ServiceUuid)
            .Multipeer(MultipeerServiceType)   // iPhones reach the Mac with no shared network
            .Ble(o => o.EnableFileTransfers(GameSession.HostFilesDirectory, ft =>
            {
                ft.MaxUploadSize = 25 * 1024 * 1024;   // chat photos and files; avatars are much smaller
                ft.Authorize = req => IsSharedFile(req.Request.FileName);
            }).EnableMediaChannels())   // calls ride a media channel beside each hub connection
            .Http(o =>
            {
                o.AuthorizeFile = req => IsSharedFile(req.FileName);
                o.HostId = PlayerSettings.InstallId;
            })
            .HttpServer(http => http.AddQuickTunnel())
            .AddHub<GameHub>(GameHubCharacteristicUuid, GameMdnsServiceType, o =>
            {
                o.MaxClients = 6;
                o.ValidateClient = info => String.IsNullOrWhiteSpace(info.Name) ? "A player name is required" : null;
            })
            .AddHub<ChatHub>(ChatHubCharacteristicUuid, ChatMdnsServiceType, o =>
            {
                o.MaxClients = 20;
                o.AllowTunnel = true;
                o.ValidateClient = info => String.IsNullOrWhiteSpace(info.Name) ? "A name is required" : null;
            })
        );

        // ---- client: finds hosts over BLE, mDNS and Multipeer ----
        builder.Services.AddUniversalHubClient<IGameHub>(GameHubCharacteristicUuid, GameMdnsServiceType, o =>
        {
            o.ServiceUuid = ServiceUuid;
            o.Multipeer(MultipeerServiceType);
        });
        builder.Services.AddUniversalHubClient<IChatHub>(ChatHubCharacteristicUuid, ChatMdnsServiceType, o =>
        {
            o.ServiceUuid = ServiceUuid;
            o.Multipeer(MultipeerServiceType);
        });

        // calls, as on the phones
        builder.Services.AddHubCalls(calls => calls.Host<ChatHub>().Client<IChatHub>());
        builder.Services.AddHubCallsMaui();
        builder.Services.AddSingleton<CallService>();

        builder.Services.AddNotifications();
        builder.Services.AddGeneratedServices();

        // maui-labs' Editor leaves its NSTextView at a zero frame with no minimum height, so an empty one (ChatView's
        // message box) has nothing to click into. Keep the text view at least as big as the scroll view showing it.
        EditorHandler.Mapper.AppendToMapping("FillScrollView", (handler, _) =>
        {
            var clip = handler.PlatformView.ContentView;
            var textView = handler.PlatformView.TextView;
            clip.PostsFrameChangedNotifications = true;
            NSView.Notifications.ObserveFrameChanged(clip, (_, _) =>
            {
                var size = clip.Bounds.Size;
                textView.MinSize = new CGSize(0, size.Height);
                textView.MaxSize = new CGSize(nfloat.MaxValue, nfloat.MaxValue);
                textView.SetFrameSize(new CGSize(size.Width, Math.Max(textView.Frame.Height, size.Height)));
            });
        });

        return builder.Build();
    }


    // only avatars move as files, on either transport
    // avatars (the game) and chat photos and files - nothing else moves, on either transport
    static bool IsSharedFile(string fileName) => IsAvatar(fileName) || ChatService.IsChatFile(fileName);

    static bool IsAvatar(string fileName)
        => fileName.StartsWith("avatar-", StringComparison.Ordinal) && fileName.EndsWith(".jpg", StringComparison.Ordinal);
}
