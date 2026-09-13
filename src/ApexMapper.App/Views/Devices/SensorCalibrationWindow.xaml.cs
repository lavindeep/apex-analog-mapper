using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using ApexMapper.App.Views.Profiles;
using ApexMapper.App.ViewModels.Devices;

namespace ApexMapper.App.Views.Devices;

public partial class SensorCalibrationWindow : Window
{
    private readonly SensorCalibrationViewModel _viewModel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private HwndSource? _source;
    public SensorCalibrationWindow(SensorCalibrationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.Begin();
        _timer.Tick += Refresh;
        Loaded += (_, _) =>
        {
            _source = PresentationSource.FromVisual(this) as HwndSource;
            ComponentDispatcher.ThreadFilterMessage += FilterCalibrationKeys;
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            ComponentDispatcher.ThreadFilterMessage -= FilterCalibrationKeys;
            _timer.Stop();
            _timer.Tick -= Refresh;
            _viewModel.End();
        };
    }
    // A measured Enter, Space or Tab must not activate or move between capture buttons.
    private void FilterCalibrationKeys(ref MSG message, ref bool handled)
    {
        if (handled || message.hwnd != _source?.Handle
            || message.message is not (0x0100 or 0x0101 or 0x0104 or 0x0105)) return;
        var key = KeyCaptureButton.DecodeKey((int)message.wParam, message.lParam.ToInt64());
        if (key is { } measured && _viewModel.Rows.Any(row => row.Key == measured)) handled = true;
    }
    private void Refresh(object? sender, EventArgs e) => _viewModel.Refresh();
    private void Save(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Save()) DialogResult = true;
    }
}
