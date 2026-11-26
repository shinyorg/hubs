using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.Net.HttpServer.Ssh;
using Shiny.UniversalHubs;
using Sample.Calls;
using Sample.Game;
using Sample.Hub;
using Sample.Services;

namespace Sample;

public static class MauiProgram
{
    /// <summary>
    /// The hubs' names - how clients ask for them on every transport
    /// </summary>
    public const string GameHubName = "game";
    public const string ChatHubName = "chat";

    /// <summary>
    /// The BLE service both hubs are characteristics in - the one UUID a host advertises and the one clients scan for. Each
    /// hub could have its own (a 16-bit one, or advertise: false, keeps the advertisement within 31 bytes); sharing one
    /// keeps a scan finding the host whichever hub it runs.
    /// </summary>
    public const string ServiceUuid = "37625845-7287-4de0-92f8-0b8a25e755ec";

    /// <summary>
    /// The write+notify characteristic that carries the GameHub over BLE
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
    /// The MultipeerConnectivity service type: one per host, so both hubs are listed in its one advertisement. iPhones and
    /// Macs reach each other through it with no shared network. NSBonjourServices lists _unihub-mc._tcp and
    /// _unihub-mc._udp. On Android it isn't supported, and is left out.
    /// </summary>
    public const string MultipeerServiceType = "unihub-mc";

    /// <summary>
    /// The Nearby Connections service id: Android, iPhones and Macs reach each other through it with no shared network.
    /// One per app, for the host and every hub client. NSBonjourServices lists _84102701D61D._tcp
    /// (NearbyProtocol.BonjourServices).
    /// </summary>
    public const string NearbyServiceId = "org.shiny.universalhubs.sample";


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

        // ---- host: a universal server - each hub served over BLE, HTTP, Multipeer and Nearby at once ----
        builder.Services.AddUniversalServer(server => server
            .Configure(o =>
            {
                o.HostId = PlayerSettings.InstallId;
                // chat photos and files, and avatars - the same rules on every transport
                o.EnableFileTransfers(GameSession.HostFilesDirectory, ft => ft.MaxUploadSize = 25 * 1024 * 1024);
                o.AuthorizeFile = req => IsSharedFile(req.FileName);
                o.EnableMediaChannels();   // calls ride a media channel beside each hub connection
            })
            .Multipeer(MultipeerServiceType)   // Apple to Apple with no network: Wi-Fi speed instead of BLE
            .Nearby(NearbyServiceId)            // Android and Apple with no network, through Google's Nearby Connections
            // "Share on the internet" in the chat: a public HTTPS address with no account (pinggy). Started from a button.
            .HttpServer(http => http.AddQuickTunnel())
            .AddHub<GameHub>(GameHubName, hub => hub   // started when the player taps Host
                .MaxClients(6)   // 1 opponent + spectators, counted across every transport
                .ValidateClient(info => String.IsNullOrWhiteSpace(info.Name) ? "A player name is required" : null)
                .UseHttp(GameMdnsServiceType)
                .UseMultipeer()
                .UseNearby()
                .UseBluetoothLE(ServiceUuid, GameHubCharacteristicUuid))
            .AddHub<ChatHub>(ChatHubName, hub => hub   // started with the app, and left running
                .MaxClients(20)
                .ValidateClient(info => String.IsNullOrWhiteSpace(info.Name) ? "A name is required" : null)
                .UseHttp(ChatMdnsServiceType, allowTunnel: true)   // the only hub reachable through the tunnel
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

        // turns each transport on and off, hosting and connecting - the Transports page
        builder.Services.AddSingleton<TransportDriver>();

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
