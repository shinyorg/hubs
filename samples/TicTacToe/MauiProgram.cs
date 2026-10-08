using System.Net;
using Microsoft.Extensions.Logging;
using Shiny;
using Shiny.BluetoothLE.Hubs;
using Shiny.Net.HttpServer;
using Shiny.SwitchboardR;
using TicTacToe.Game;
using TicTacToe.Hub;
using TicTacToe.Services;

namespace TicTacToe;

public static class MauiProgram
{
    /// <summary>
    /// Every TicTacToe host advertises this service - clients only see devices advertising it
    /// </summary>
    public const string GameServiceUuid = "37625845-7287-4de0-92f8-0b8a25e755ec";

    /// <summary>
    /// The write+notify characteristic that carries the GameHub over BLE - and names its Switchboard over Wi-Fi
    /// </summary>
    public const string GameHubCharacteristicUuid = "37625846-7287-4de0-92f8-0b8a25e755ec";


    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseShiny()
            .UseShinyShell(x => x.AddGeneratedMaps())
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddDebug();
#endif

        // hub arguments and results travel as JSON on both transports - register the source generated context (AOT safe)
        Json.AddContext(GameJsonContext.Default);

        // the platform BLE stacks - this app can be either the host or a client
        builder.Services.AddBluetoothLE();
        builder.Services.AddBluetoothLeHosting();

        // ---- host: one GameHub, served over BLE and Wi-Fi at once ----
        builder.Services.AddBleHub<GameHub>(GameServiceUuid, GameHubCharacteristicUuid, o =>
        {
            o.MaxClients = 6; // 1 opponent + spectators, counted across both transports
            o.ValidateClient = info => String.IsNullOrWhiteSpace(info.Name) ? "A player name is required" : null;
        });
        builder.Services.ConfigureBleHubHost(o => o.EnableFileTransfers(GameSession.HostFilesDirectory, ft =>
        {
            ft.MaxUploadSize = 512 * 1024;
            ft.Authorize = req => IsAvatar(req.Request.FileName);   // L2CAP
        }));
        builder.Services.AddShinyHttpServer(http =>
        {
            // reachable from other devices on the network, on whatever port is free - mDNS tells clients which
            http.Options.Address = IPAddress.Any;
            http.Options.Port = 0;

            http
                .AddSwitchboardR(o =>
                {
                    o.AuthorizeFile = req => IsAvatar(req.FileName);   // Wi-Fi
                    o.HostId = PlayerSettings.InstallId;
                })
                .AddHub<GameHub>();
        }, autoStart: false); // ISwitchboardRHost starts it when the player taps Host

        // ---- client: discovers hosts over BLE and mDNS, connects over whichever works ----
        builder.Services.AddSwitchboardRClient<IGameHub>(GameServiceUuid, GameHubCharacteristicUuid);

        builder.Services.AddSingleton<GameEngine>();
        builder.Services.AddSingleton<GameSession>();
        builder.Services.AddSingleton<PlayerSettings>();

        return builder.Build();
    }


    // only avatars move as files, on either transport
    static bool IsAvatar(string fileName)
        => fileName.StartsWith("avatar-", StringComparison.Ordinal) && fileName.EndsWith(".jpg", StringComparison.Ordinal);
}
