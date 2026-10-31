using Microsoft.Maui.Graphics.Platform;
using Shiny.UniversalHubs;

namespace Sample.Services;

[Singleton]
public class PlayerSettings
{
    /// <summary>
    /// Identifies this device when it hosts, so a client seeing it over both BLE and Wi-Fi lists it once
    /// </summary>
    public static string InstallId
    {
        get
        {
            var id = Preferences.Get(nameof(InstallId), null);
            if (id == null)
            {
                id = Guid.NewGuid().ToString("N");
                Preferences.Set(nameof(InstallId), id);
            }
            return id;
        }
    }


    /// <summary>
    /// Multipeer is Apple to Apple - elsewhere it is not offered
    /// </summary>
    public static bool IsMultipeerSupported => OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS();

    /// <summary>
    /// The choices for <see cref="ConnectOver"/>, as the pickers show them
    /// </summary>
    /// <summary>
    /// Nearby Connections is on Android, iOS and macOS
    /// </summary>
    public static bool IsNearbySupported => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsMacOS();

    public static IReadOnlyList<string> ConnectChoices { get; } = new[]
    {
        "Auto",
        "Wi-Fi",
        IsMultipeerSupported ? "Multipeer" : null,
        IsNearbySupported ? "Nearby" : null,
        "Bluetooth"
    }.OfType<string>().ToList();


    /// <summary>
    /// How to join a host - None is Auto (Wi-Fi, then Multipeer, Nearby, Bluetooth). Any one transport means only that one,
    /// so each can be tried on its own.
    /// </summary>
    public HubTransports ConnectOver
    {
        get => (HubTransports)Preferences.Get(nameof(this.ConnectOver), (int)HubTransports.None);
        set => Preferences.Set(nameof(this.ConnectOver), (int)value);
    }


    /// <summary>
    /// <see cref="ConnectOver"/> as a <see cref="ConnectChoices"/> label
    /// </summary>
    public string ConnectChoice
    {
        get => this.ConnectOver switch
        {
            HubTransports.Http => "Wi-Fi",
            HubTransports.Multipeer when IsMultipeerSupported => "Multipeer",
            HubTransports.Nearby when IsNearbySupported => "Nearby",
            HubTransports.Ble => "Bluetooth",
            _ => "Auto"
        };
        set => this.ConnectOver = value switch
        {
            "Wi-Fi" => HubTransports.Http,
            "Multipeer" => HubTransports.Multipeer,
            "Nearby" => HubTransports.Nearby,
            "Bluetooth" => HubTransports.Ble,
            _ => HubTransports.None
        };
    }


    /// <summary>
    /// Applies <see cref="ConnectOver"/> to a connection
    /// </summary>
    public HubConnectOptions Route(HubConnectOptions options) => this.ConnectOver switch
    {
        HubTransports.None => options,
        var only => options with { Prefer = only, Allowed = only }
    };


    /// <summary>
    /// What this device hosts its chat and games over - at least one
    /// </summary>
    public HubTransports BroadcastOver
    {
        get
        {
            var over = (HubTransports)Preferences.Get(nameof(this.BroadcastOver), (int)HubTransports.All) & HubTransports.All;
            return over == HubTransports.None ? HubTransports.All : over;
        }
        set => Preferences.Set(nameof(this.BroadcastOver), (int)value);
    }


    /// <summary>
    /// Auto, H264 or Jpeg - forcing one on either phone lets you try each codec on a call
    /// </summary>
    public string VideoCodec
    {
        get => Preferences.Get(nameof(this.VideoCodec), "Auto");
        set => Preferences.Set(nameof(this.VideoCodec), value);
    }


    public string Name
    {
        get
        {
            var name = Preferences.Get(nameof(this.Name), DeviceInfo.Current.Name);
            return name.Length > 12 ? name[..12].Trim() : name;
        }
        set => Preferences.Set(nameof(this.Name), value);
    }


    public string? AvatarPath
    {
        get
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "my-avatar.jpg");
            return File.Exists(path) ? path : null;
        }
    }


    /// <summary>
    /// Shrinks the picked photo so the transfer is quick, especially over L2CAP
    /// </summary>
    public async Task SaveAvatar(FileResult photo)
    {
        await using var input = await photo.OpenReadAsync();
        var image = PlatformImage.FromStream(input);
        using var resized = image.Downsize(256, true);

        var path = Path.Combine(FileSystem.AppDataDirectory, "my-avatar.jpg");
        await using var output = File.Create(path);
        await resized.SaveAsync(output, ImageFormat.Jpeg, 0.8f);
    }


    public void ClearAvatar()
    {
        if (this.AvatarPath is { } path)
            File.Delete(path);
    }
}
