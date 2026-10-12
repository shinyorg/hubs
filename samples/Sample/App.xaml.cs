using Sample.Services;

namespace Sample;

public partial class App : Application
{
    readonly ChatService chat;

    public App(ChatService chat)
    {
        this.chat = chat;
        this.InitializeComponent();
    }


    protected override Window CreateWindow(IActivationState? activationState)
    {
        // the chat is open for as long as the app is - unlike a game, which runs only while hosting
        _ = this.chat.Start();
#if MACOS
        return new(new AppShell()) { Width = 720, Height = 860 };
#else
        return new(new AppShell());
#endif
    }
}
