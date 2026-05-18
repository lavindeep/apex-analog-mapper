using ApexMapper.App.ViewModels.Calibration;
using ApexMapper.App.ViewModels.Devices;
using ApexMapper.App.ViewModels.Profiles;

namespace ApexMapper.App.ViewModels;

/// <summary>
/// Root view-model for the main application window.
/// Holds the three child view-models surfaced in the tab control.
/// All dependencies are constructor-injected via DI.
/// </summary>
public sealed class MainWindowViewModel : ObservableViewModel
{
    public MainWindowViewModel(
        ProfileSelectorViewModel profileSelectorViewModel,
        DevicePickerViewModel devicePickerViewModel,
        CalibrationWizardViewModel calibrationWizardViewModel)
    {
        ProfileSelectorViewModel   = profileSelectorViewModel   ?? throw new ArgumentNullException(nameof(profileSelectorViewModel));
        DevicePickerViewModel      = devicePickerViewModel      ?? throw new ArgumentNullException(nameof(devicePickerViewModel));
        CalibrationWizardViewModel = calibrationWizardViewModel ?? throw new ArgumentNullException(nameof(calibrationWizardViewModel));
    }

    public ProfileSelectorViewModel   ProfileSelectorViewModel   { get; }
    public DevicePickerViewModel      DevicePickerViewModel      { get; }
    public CalibrationWizardViewModel CalibrationWizardViewModel { get; }
}
