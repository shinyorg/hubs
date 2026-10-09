using AppKit;
using CoreGraphics;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platforms.MacOS.Essentials;
using Microsoft.Maui.Platforms.MacOS.Handlers;
using Microsoft.Maui.Platforms.MacOS.Hosting;
using Shiny;
using Shiny.BluetoothLE.Hubs;
using Shiny.UniversalHubs;
using Sample.Hub;

namespace Sample.MacOS;

public static class MauiProgram
{
    // the same hubs as the phone app - see samples/Sample/MauiProgram.cs
    const string GameServiceUuid = "37625845-7287-4de0-92f8-0b8a25e755ec";
    const string GameHubCharacteristicUuid = "37625846-7287-4de0-92f8-0b8a25e755ec";
    const string GameMdnsServiceType = "_unigame._tcp";
    const string ChatServiceUuid = "37625850-7287-4de0-92f8-0b8a25e755ec";
    const string ChatHubCharacteristicUuid = "37625851-7287-4de0-92f8-0b8a25e755ec";
    const string ChatMdnsServiceType = "_unichat._tcp";


    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiAppMacOS<App>()
            .AddMacOSEssentials()
            .UseShinyControls()
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

        // Wi-Fi only on the Mac: hosts are found over mDNS, or reached at an address someone shared
        builder.Services.AddUniversalHubClient<IGameHub>(GameServiceUuid, GameHubCharacteristicUuid, GameMdnsServiceType);
        builder.Services.AddUniversalHubClient<IChatHub>(ChatServiceUuid, ChatHubCharacteristicUuid, ChatMdnsServiceType);
        builder.Services.AddSingleton<MacClient>();

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
}
