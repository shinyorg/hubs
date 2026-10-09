using Foundation;
using Microsoft.Maui.Platforms.MacOS.Platform;

namespace Sample.MacOS;

[Register("MauiMacOSAppDelegate")]
public class MauiMacOSAppDelegate : MacOSMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
