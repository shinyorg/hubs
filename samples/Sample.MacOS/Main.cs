using AppKit;

namespace Sample.MacOS;

public static class MainClass
{
    static void Main(string[] args)
    {
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new MauiMacOSAppDelegate();
        NSApplication.Main(args);
    }
}
