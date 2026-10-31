using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.Net.HttpServer.Ssh;
using Shiny.BluetoothLE.Hubs;
using Shiny.UniversalHubs;
using Sample.Calls;
using Sample.Game;
using Sample.Hub;
using Sample.Services;

namespace Sample;

public static class MauiProgram
{
    /// <summary>
    /// Every hub (game and chat) is a characteristic in this one BLE service - the only UUID a host advertises and the one
    /// clients scan for. The library has a default; the sample's own keeps other apps' hosts out of its lists.
    /// </summary>
    public const string ServiceUuid = "37625845-7287-4de0-92f8-0b8a25e755ec";

    /// <summary>
    /// The write+notify characteristic that carries the GameHub over BLE - and names its Switchboard over Wi-Fi
    /// </summary>
    public const string GameHubCharacteristicUuid = "37625846-7287-4de0-92f8-0b8a25e755ec";

    /// <summary>
    /// The mDNS service type the GameHub is advertised under and clients browse for - also in iOS's NSBonjourServices
    /// </summary>
    public const string GameMdnsServiceType = "_unigame._tcp";

    /// <summary>
    /// The always-on chat. It has its own mDNS service type, so over Wi-Fi the game's join list never shows someone who is
    /// only chatting. Over BLE it shares the service - a scan can't tell which hubs a host is running.
    /// </summary>
    public const string ChatHubCharacteristicUuid = "37625851-7287-4de0-92f8-0b8a25e755ec";
    public const string ChatMdnsServiceType = "_unichat._tcp";

    /// <summary>
    /// The MultipeerConnectivity service type: one per host, like the BLE service, so both hubs are listed in its one
    /// advertisement. iPhones and Macs reach each other through it with no shared network. NSBonjourServices lists
    /// _unihub-mc._tcp and _unihub-mc._udp. On Android it does nothing - Android peers use Wi-Fi or BLE.
    /// </summary>
    public const string MultipeerServiceType = "unihub-mc";


    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
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
        builder.Logging.AddConsole();   // shows up in `xcrun devicectl device process launch --console` and logcat
#endif

        // hub arguments and results travel as JSON on both transports - register the source generated context (AOT safe)
        Json.AddContext(GameJsonContext.Default);
        Json.AddContext(ChatJsonContext.Default);

        // every chat conversation and every hosted game is an actor - conversations are kept on disk, games in memory
        builder.Services.AddShinyActors(actors => actors.UseFileStorage(Path.Combine(FileSystem.AppDataDirectory, "actors")));

        // ---- host: a universal server - GameHub served over BLE, HTTP and Multipeer at once (adds the BLE hosting stack too) ----
        builder.Services.AddUniversalServer(server => server
            .ServiceUuid(ServiceUuid)   // the one BLE service every hub lives in - clients use the same one
            .Multipeer(MultipeerServiceType)   // Apple to Apple with no network: Wi-Fi speed instead of BLE
            .Ble(o => o.EnableFileTransfers(GameSession.HostFilesDirectory, ft =>
            {
                ft.MaxUploadSize = 25 * 1024 * 1024;   // chat photos and files; avatars are much smaller
                ft.Authorize = req => IsSharedFile(req.Request.FileName);   // L2CAP
            }).EnableMediaChannels())   // calls ride a media channel beside each hub connection
            .Http(o =>
            {
                o.AuthorizeFile = req => IsSharedFile(req.FileName);       // HTTP
                o.HostId = PlayerSettings.InstallId;
            })
            // "Share on the internet" in the chat: a public HTTPS address with no account (pinggy). Started from a button.
            .HttpServer(http => http.AddQuickTunnel())
            .AddHub<GameHub>(GameHubCharacteristicUuid, GameMdnsServiceType, o =>
            {
                o.MaxClients = 6; // 1 opponent + spectators, counted across both transports
                o.ValidateClient = info => String.IsNullOrWhiteSpace(info.Name) ? "A player name is required" : null;
            })   // started when the player taps Host
            .AddHub<ChatHub>(ChatHubCharacteristicUuid, ChatMdnsServiceType, o =>
            {
                o.MaxClients = 20;
                o.AllowTunnel = true;   // the only hub reachable through the tunnel
                o.ValidateClient = info => String.IsNullOrWhiteSpace(info.Name) ? "A name is required" : null;
            })   // started with the app, and left running
        );

        // ---- client: discovers hosts over BLE, mDNS and Multipeer, connects over whichever works (adds the BLE stack too) ----
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

        // ---- calls: FaceTime the person in a chat, over Wi-Fi or Bluetooth ----
        builder.Services.AddHubCalls(calls => calls
            .Host<ChatHub>()        // visitors call this device's owner, and the owner calls them back
            .Client<IChatHub>());   // and this device calls the owner of a chat it is visiting
        builder.Services.AddHubCallsMaui();   // Shiny.Audio echo-cancelled voice + the Shiny Controls camera
        builder.Services.AddSingleton<CallService>();

        builder.Services.AddNotifications();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<ChatSessionProvider>();

        builder.Services.AddSingleton<GameSession>();
        builder.Services.AddSingleton<PlayerSettings>();

        return builder.Build();
    }


    // only avatars move as files, on either transport
    // avatars (the game) and chat photos and files - nothing else moves, on either transport
    static bool IsSharedFile(string fileName) => IsAvatar(fileName) || ChatService.IsChatFile(fileName);

    static bool IsAvatar(string fileName)
        => fileName.StartsWith("avatar-", StringComparison.Ordinal) && fileName.EndsWith(".jpg", StringComparison.Ordinal);
}
