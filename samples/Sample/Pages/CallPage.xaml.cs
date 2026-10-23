using Sample.Calls;

namespace Sample.Pages;

public partial class CallPage : ContentPage
{
    public CallPage() => this.InitializeComponent();


    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (this.BindingContext is CallViewModel vm)
            vm.VideoReady += (_, _) => vm.Call?.AttachVideo(this.Camera, this.Remote);
    }
}
