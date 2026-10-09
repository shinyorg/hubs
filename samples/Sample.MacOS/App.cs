namespace Sample.MacOS;

public class App(MacClient client) : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(new NavigationPage(new HomePage(client)))
    {
        Title = "Universal Hubs",
        Width = 720,
        Height = 820
    };
}
