using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Hosting;
using Shiny.Maui.Controls.Camera;

namespace Sample.Calls;

public static class HubCallsMauiExtensions
{
    /// <summary>
    /// Calls on this device: the microphone and speaker through Shiny.Audio's full-duplex voice (echo cancelled), and
    /// video through Shiny.Maui.Controls.Camera. Creates <see cref="IHubCalls"/> as the app starts, so incoming calls
    /// are answered from the first connection. Call AddHubCalls too, and UseShinyCamera on the MauiAppBuilder for the
    /// CameraView and RemoteVideoView handlers.
    /// </summary>
    public static IServiceCollection AddHubCallsMaui(this IServiceCollection services)
    {
        services.AddAudioDuplex();
        services.AddAudioDevices();
        services.TryAddSingleton<DuplexCallAudio>();
        services.TryAddSingleton<ICallAudio>(sp => sp.GetRequiredService<DuplexCallAudio>());
        services.TryAddSingleton<ICallVideoCapabilities, CameraCallVideoCapabilities>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMauiInitializeService, HubCallsStartup>());
        return services;
    }


    /// <summary>
    /// <paramref name="camera"/> films this side while the call has video and the camera is on;
    /// <paramref name="remote"/> shows the other side. Either may be null.
    /// </summary>
    public static void AttachVideo(this IHubCall call, CameraView? camera, RemoteVideoView? remote)
        => call.AttachVideo(
            camera == null ? null : new CameraCallVideoSource(camera),
            remote == null ? null : new RemoteVideoCallSink(remote)
        );


    sealed class HubCallsStartup : IMauiInitializeService
    {
        public void Initialize(IServiceProvider services) => services.GetRequiredService<IHubCalls>();
    }
}
