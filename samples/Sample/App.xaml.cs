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
        return new(new AppShell());
    }
}
