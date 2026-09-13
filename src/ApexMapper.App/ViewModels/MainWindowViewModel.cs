using ApexMapper.App.ViewModels.Devices;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.App.ViewModels.Tray;

namespace ApexMapper.App.ViewModels;

/// <summary>Shares device, profile, and mapping controls between the window and tray.</summary>
public sealed class MainWindowViewModel : ObservableViewModel
{
    public MainWindowViewModel(
        ProfileSelectorViewModel profileSelectorViewModel,
        DevicePickerViewModel devicePickerViewModel,
        TrayMenuViewModel trayMenuViewModel)
    {
        ProfileSelectorViewModel = profileSelectorViewModel;
        DevicePickerViewModel = devicePickerViewModel;
        TrayMenuViewModel = trayMenuViewModel;
    }

    public ProfileSelectorViewModel ProfileSelectorViewModel { get; }
    public DevicePickerViewModel DevicePickerViewModel { get; }
    public TrayMenuViewModel TrayMenuViewModel { get; }
}
