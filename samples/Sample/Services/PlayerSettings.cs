using Microsoft.Maui.Graphics.Platform;

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
    /// Join over Bluetooth even when the host is also on Wi-Fi - handy for trying both transports
    /// </summary>
    public bool PreferBluetooth
    {
        get => Preferences.Get(nameof(this.PreferBluetooth), false);
        set => Preferences.Set(nameof(this.PreferBluetooth), value);
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
