using ApexMapper.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ApexMapper.App.ViewModels.Devices;

public sealed partial class DeviceListItem : ApexMapper.App.ViewModels.ObservableViewModel
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private ushort _vid;

    [ObservableProperty]
    private ushort _pid;

    [ObservableProperty]
    private bool _isPrimary;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private DeviceCalibrationStatus _calibrationStatus;
}
