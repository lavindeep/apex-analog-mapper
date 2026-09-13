using ApexMapper.App.ViewModels.Devices;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.App.ViewModels.Tray;
using ApexMapper.App.Services;

namespace ApexMapper.App.ViewModels;

/// <summary>Shares keyboard, game, profile, and mapping controls between the window and tray.</summary>
public sealed class MainWindowViewModel : ObservableViewModel
{
    private readonly MappingSession? _mappingSession;
    public MainWindowViewModel(
        ProfileSelectorViewModel profileSelectorViewModel,
        DevicePickerViewModel devicePickerViewModel,
        TrayMenuViewModel trayMenuViewModel,
        SensorCalibrationViewModel? sensorCalibrationViewModel = null,
        MappingSession? mappingSession = null,
        GamePickerViewModel? gamePickerViewModel = null)
    {
        ProfileSelectorViewModel = profileSelectorViewModel;
        DevicePickerViewModel = devicePickerViewModel;
        TrayMenuViewModel = trayMenuViewModel;
        SensorCalibrationViewModel = sensorCalibrationViewModel;
        GamePickerViewModel = gamePickerViewModel;
        _mappingSession = mappingSession;
    }

    public ProfileSelectorViewModel ProfileSelectorViewModel { get; }
    public DevicePickerViewModel DevicePickerViewModel { get; }
    public TrayMenuViewModel TrayMenuViewModel { get; }
    public SensorCalibrationViewModel? SensorCalibrationViewModel { get; }
    public GamePickerViewModel? GamePickerViewModel { get; }
    public IDisposable? BeginInputEditing() => _mappingSession?.BlockForInputEditing();
}
