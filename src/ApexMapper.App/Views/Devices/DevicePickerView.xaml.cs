using System.Windows.Controls;
using System.Windows;
using ApexMapper.App.ViewModels;

namespace ApexMapper.App.Views.Devices;

public partial class DevicePickerView : UserControl
{
    public DevicePickerView() => InitializeComponent();
    private void CalibrateKeys(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner?.DataContext is MainWindowViewModel { SensorCalibrationViewModel: { } calibration })
            new SensorCalibrationWindow(calibration) { Owner = owner }.ShowDialog();
    }
}
