using System.Collections.ObjectModel;
using ApexMapper.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels.Devices;

public sealed class DevicePickerViewModel : ApexMapper.App.ViewModels.ObservableViewModel
{
    private readonly IDeviceSelectorFacade _selector;
    private readonly IDeviceRegistryFacade _registry;

    private ObservableCollection<DeviceListItem> _devices = [];
    private DeviceListItem? _primary;

    public DevicePickerViewModel(
        IDeviceSelectorFacade selector,
        IDeviceRegistryFacade registry)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        RefreshCommand = new RelayCommand(ExecuteRefresh);
        MakePrimaryCommand = new RelayCommand<Guid>(ExecuteMakePrimary, CanMakePrimary);

        _selector.TopologyChanged += OnTopologyChanged;

        LoadFromSelector();
    }

    public ObservableCollection<DeviceListItem> Devices
    {
        get => _devices;
        private set => SetProperty(ref _devices, value);
    }

    /// <summary>Read-only projection: the item in <see cref="Devices"/> whose IsPrimary is true.</summary>
    public DeviceListItem? Primary
    {
        get => _primary;
        private set => SetProperty(ref _primary, value);
    }

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand<Guid> MakePrimaryCommand { get; }

    // ---------------------------------------------------------------------------
    // Command implementations
    // ---------------------------------------------------------------------------

    private void ExecuteRefresh()
    {
        _selector.Refresh();
        LoadFromSelector();
    }

    private void ExecuteMakePrimary(Guid id)
    {
        _selector.SelectPrimary(id);
        LoadFromSelector();
    }

    private bool CanMakePrimary(Guid id)
    {
        var item = _devices.FirstOrDefault(d => d.Id == id);
        return item?.IsConnected == true;
    }

    // ---------------------------------------------------------------------------
    // Event handler
    // ---------------------------------------------------------------------------

    private void OnTopologyChanged(object? sender, TopologyChangedEventArgs e)
    {
        // Merge topology update into the existing collection.
        // Connected set comes from the event; we preserve disconnected rows (IsConnected = false).

        var connectedIds = e.Devices.ToDictionary(d => d.Id);

        // Mark previously-connected items as disconnected if they vanished.
        foreach (var item in _devices)
        {
            if (!connectedIds.ContainsKey(item.Id))
            {
                item.IsConnected = false;
                item.IsPrimary = false;
            }
        }

        // Add or update rows for currently-connected devices.
        var existingIds = _devices.Select(d => d.Id).ToHashSet();
        foreach (var entry in e.Devices)
        {
            if (existingIds.Contains(entry.Id))
            {
                var existing = _devices.First(d => d.Id == entry.Id);
                existing.IsConnected = entry.IsConnected;
                existing.IsPrimary = entry.IsPrimary;
                existing.DisplayName = entry.DisplayName;
            }
            else
            {
                var status = _registry.GetStatus(entry.Id);
                _devices.Add(new DeviceListItem
                {
                    Id = entry.Id,
                    DisplayName = entry.DisplayName,
                    Vid = entry.Vid,
                    Pid = entry.Pid,
                    IsConnected = entry.IsConnected,
                    IsPrimary = entry.IsPrimary,
                    CalibrationStatus = status,
                });
            }
        }

        Primary = _devices.FirstOrDefault(d => d.IsPrimary);
        ((RelayCommand<Guid>)MakePrimaryCommand).NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------------------
    // Load helper
    // ---------------------------------------------------------------------------

    private void LoadFromSelector()
    {
        var entries = _selector.ListAll();

        // Build a fresh collection preserving disconnected rows already tracked.
        var existingDisconnected = _devices
            .Where(d => !d.IsConnected)
            .ToDictionary(d => d.Id);

        var newCollection = new ObservableCollection<DeviceListItem>();

        // Add connected/known entries from selector.
        var connectedIds = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            connectedIds.Add(entry.Id);
            var status = _registry.GetStatus(entry.Id);
            newCollection.Add(new DeviceListItem
            {
                Id = entry.Id,
                DisplayName = entry.DisplayName,
                Vid = entry.Vid,
                Pid = entry.Pid,
                IsConnected = entry.IsConnected,
                IsPrimary = entry.IsPrimary,
                CalibrationStatus = status,
            });
        }

        // Re-append previously-seen disconnected rows that weren't returned by the selector.
        foreach (var (id, item) in existingDisconnected)
        {
            if (!connectedIds.Contains(id))
                newCollection.Add(item);
        }

        _devices = newCollection;
        OnPropertyChanged(nameof(Devices));
        Primary = _devices.FirstOrDefault(d => d.IsPrimary);
        ((RelayCommand<Guid>)MakePrimaryCommand).NotifyCanExecuteChanged();
    }
}
