using AppKit;
using CoreGraphics;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Handlers;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Shiny;
using Shiny.UniversalHubs;
using Shiny.Net.HttpServer.Ssh;
using Sample.Calls;
using Sample.Hub;
using Sample.Services;

namespace Sample.MacOS;

public static class MauiProgram
{
    // the same hubs as the phone app - see samples/Sample/MauiProgram.cs
    const string GameHubName = "game";
    const string ChatHubName = "chat";
    const string ServiceUuid = "37625845-7287-4de0-92f8-0b8a25e755ec";
    const string GameHubCharacteristicUuid = "37625846-7287-4de0-92f8-0b8a25e755ec";
    const string GameMdnsServiceType = "_unigame._tcp";
    const string ChatHubCharacteristicUuid = "37625851-7287-4de0-92f8-0b8a25e755ec";
    const string ChatMdnsServiceType = "_unichat._tcp";
    const string MultipeerServiceType = "unihub-mc";
    const string NearbyServiceId = "org.shiny.universalhubs.sample";


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

        // Shiny.UniversalHubs.BluetoothLE only registers the BLE stacks on Android, iOS and Mac Catalyst: register Shiny's macOS builds here
        builder.Services.AddBluetoothLE();
        builder.Services.AddBluetoothLeHosting();

        // ---- host: the same universal server as the phone app ----
        builder.Services.AddUniversalServer(server => server
            .Configure(o =>
            {
                o.HostId = PlayerSettings.InstallId;
                o.EnableFileTransfers(GameSession.HostFilesDirectory, ft => ft.MaxUploadSize = 25 * 1024 * 1024);
                o.AuthorizeFile = req => IsSharedFile(req.FileName);
                o.EnableMediaChannels();   // calls ride a media channel beside each hub connection
            })
            .Multipeer(MultipeerServiceType)   // iPhones reach the Mac with no shared network
            .Nearby(NearbyServiceId)            // and Android phones too
            .HttpServer(http => http.AddQuickTunnel())
            .AddHub<GameHub>(GameHubName, hub => hub
                .MaxClients(6)
                .ValidateClient(info => String.IsNullOrWhiteSpace(info.Name) ? "A player name is required" : null)
                .UseHttp(GameMdnsServiceType)
                .UseMultipeer()
                .UseNearby()
                .UseBluetoothLE(ServiceUuid, GameHubCharacteristicUuid))
            .AddHub<ChatHub>(ChatHubName, hub => hub
                .MaxClients(20)
                .ValidateClient(info => String.IsNullOrWhiteSpace(info.Name) ? "A name is required" : null)
                .UseHttp(ChatMdnsServiceType, allowTunnel: true)
                .UseMultipeer()
                .UseNearby()
                .UseBluetoothLE(ServiceUuid, ChatHubCharacteristicUuid))
        );

        // ---- clients: find hosts over every transport, and connect over the first that works, in this order ----
        builder.Services.AddUniversalHubClient<IGameHub>(GameHubName, o => o
            .UseHttp(GameMdnsServiceType)
            .UseMultipeer(MultipeerServiceType)
            .UseNearby(NearbyServiceId)
            .UseBluetoothLE(ServiceUuid, GameHubCharacteristicUuid));
        builder.Services.AddUniversalHubClient<IChatHub>(ChatHubName, o => o
            .UseHttp(ChatMdnsServiceType)
            .UseMultipeer(MultipeerServiceType)
            .UseNearby(NearbyServiceId)
            .UseBluetoothLE(ServiceUuid, ChatHubCharacteristicUuid));

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
