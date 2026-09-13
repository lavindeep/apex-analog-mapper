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
    private string? _physicalDeviceId;

    [ObservableProperty]
    private string _devicePath = string.Empty;

    [ObservableProperty]
    private string _sourceLabel = string.Empty;
}

public sealed record KeyboardGroup(string Id, string DisplayName, IReadOnlyList<DeviceListItem> Sources)
{
    public bool IsSelected => Sources.Any(source => source.IsPrimary);
    public bool IsConnected => Sources.Any(source => source.IsConnected);
    public DeviceListItem? SelectedSource => Sources.FirstOrDefault(source => source.IsPrimary);
}
