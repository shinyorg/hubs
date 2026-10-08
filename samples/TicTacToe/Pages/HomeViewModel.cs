using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shiny;
using TicTacToe.Services;

namespace TicTacToe.Pages;

[ShellMap<HomePage>("Home", registerRoute: false)]
public partial class HomeViewModel(
    ShellServices shell,
    PlayerSettings settings,
    GameSession session
) : ObservableObject, IPageLifecycleAware
{
    [ObservableProperty] public partial string PlayerName { get; set; } = settings.Name;
    [ObservableProperty] public partial ImageSource? Avatar { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }


    public void OnAppearing() => this.Avatar = settings.AvatarPath is { } p ? ImageSource.FromFile(p) : null;
    public void OnDisappearing() { }


    [RelayCommand]
    async Task PickAvatar()
    {
        try
        {
            var photos = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1 });
            var photo = photos?.FirstOrDefault();
            if (photo == null)
                return;

            await settings.SaveAvatar(photo);
            // new stream source so the image refreshes even though the path is unchanged
            var bytes = await File.ReadAllBytesAsync(settings.AvatarPath!);
            this.Avatar = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            await shell.Dialogs.Alert("Avatar", ex.Message);
        }
    }


    [RelayCommand]
    void ClearAvatar()
    {
        settings.ClearAvatar();
        this.Avatar = null;
    }


    [RelayCommand]
    async Task Host()
    {
        if (!this.SaveName())
            return;

        this.IsBusy = true;
        try
        {
            await session.StartHosting(settings.Name, settings.AvatarPath);
            await shell.Navigator.NavigateToGame();
        }
        catch (Exception ex)
        {
            await shell.Dialogs.Alert("Could not host", ex.Message);
        }
        finally
        {
            this.IsBusy = false;
        }
    }


    [RelayCommand]
    async Task Join()
    {
        if (this.SaveName())
            await shell.Navigator.NavigateToJoin();
    }


    bool SaveName()
    {
        var name = this.PlayerName?.Trim();
        if (String.IsNullOrEmpty(name))
        {
            _ = shell.Dialogs.Alert("Name", "Enter a player name first");
            return false;
        }
        // short names keep the advertisement packet small
        settings.Name = name.Length > 12 ? name[..12] : name;
        this.PlayerName = settings.Name;
        return true;
    }
}
