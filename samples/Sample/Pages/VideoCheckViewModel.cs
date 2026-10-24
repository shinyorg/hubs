using CommunityToolkit.Mvvm.ComponentModel;
using Shiny;

namespace Sample.Pages;

/// <summary>
/// The call's video path with no network: the camera's VideoEncoder output goes straight into a RemoteVideoView on the
/// same screen. If this shows a picture, video problems are in transport; if not, they're in encode, decode or drawing.
/// </summary>
[ShellMap<VideoCheckPage>("VideoCheck")]
public partial class VideoCheckViewModel : ObservableObject
{
    [ObservableProperty] public partial string Status { get; set; } = "Starting the camera...";
}
