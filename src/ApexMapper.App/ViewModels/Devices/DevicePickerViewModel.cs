using System.Collections.ObjectModel;
using ApexMapper.App.Services;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels.Devices;

public sealed class DevicePickerViewModel : ObservableViewModel
{
    private readonly IDeviceSelectorFacade _selector;
    private readonly SynchronizationContext? _syncContext;
    private ObservableCollection<DeviceListItem> _devices = [];
    private IReadOnlyList<KeyboardGroup> _keyboardGroups = [];
    private DeviceListItem? _primary;
    private KeyboardGroup? _selectedKeyboard;

    public DevicePickerViewModel(IDeviceSelectorFacade selector)
    {
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _syncContext = SynchronizationContext.Current;
        RefreshCommand = new RelayCommand(() => { _selector.Refresh(); LoadFromSelector(); });
        MakePrimaryCommand = new RelayCommand<Guid>(ExecuteMakePrimary,
            id => Devices.Any(device => device.Id == id && device.IsConnected));
        SelectKeyboardCommand = new RelayCommand<KeyboardGroup>(SelectKeyboard);
        _selector.TopologyChanged += OnTopologyChanged;
        LoadFromSelector();
    }

    public ObservableCollection<DeviceListItem> Devices => _devices;
    public IReadOnlyList<KeyboardGroup> KeyboardGroups => _keyboardGroups;
    public DeviceListItem? Primary => _primary;

    public KeyboardGroup? SelectedKeyboard
    {
        get => _selectedKeyboard;
        set { if (value is { IsConnected: true } && value.Id != _selectedKeyboard?.Id) SelectKeyboard(value); }
    }

    public DeviceListItem? SelectedSource
    {
        get => _primary;
        set
        {
            if (value is { IsConnected: true } && value.Id != _primary?.Id)
                ExecuteMakePrimary(value.Id);
        }
    }

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand<Guid> MakePrimaryCommand { get; }
    public IRelayCommand<KeyboardGroup> SelectKeyboardCommand { get; }

    private void SelectKeyboard(KeyboardGroup? keyboard)
    {
        if (keyboard is not { IsConnected: true }) return;
        var source = keyboard.SelectedSource ?? keyboard.Sources.FirstOrDefault(item => item.IsConnected);
        if (source is not null) ExecuteMakePrimary(source.Id);
    }

    private void ExecuteMakePrimary(Guid id)
    {
        _selector.SelectPrimary(id);
        LoadFromSelector();
    }

    private void LoadFromSelector() => ApplySnapshot(_selector.ListAll());

    private void OnTopologyChanged(object? sender, TopologyChangedEventArgs change)
    {
        if (_syncContext is not null && _syncContext != SynchronizationContext.Current)
            _syncContext.Post(_ => ApplySnapshot(change.Devices), null);
        else
            ApplySnapshot(change.Devices);
    }

    // Preserve disconnected choices, while treating Windows container identity
    // as the only evidence that multiple input sources share one keyboard.
    private void ApplySnapshot(IReadOnlyList<DeviceFacadeEntry> entries)
    {
        var previousKeyboardId = _selectedKeyboard?.Id;
        var previous = _devices.ToDictionary(item => item.Id);
        var current = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            current.Add(entry.Id);
            if (!previous.TryGetValue(entry.Id, out var item))
                previous[entry.Id] = item = new DeviceListItem { Id = entry.Id };
            item.DisplayName = entry.DisplayName;
            item.Vid = entry.Vid;
            item.Pid = entry.Pid;
            item.IsConnected = entry.IsConnected;
            item.IsPrimary = entry.IsPrimary;
            item.PhysicalDeviceId = entry.PhysicalDeviceId;
            item.DevicePath = entry.DevicePath ?? string.Empty;
            item.SourceLabel = entry.SourceLabel ?? "Keyboard input";
        }
        foreach (var item in previous.Values.Where(item => !current.Contains(item.Id)))
        {
            item.IsConnected = false;
            item.IsPrimary = false;
        }

        _devices = new ObservableCollection<DeviceListItem>(previous.Values);
        _primary = _devices.FirstOrDefault(item => item.IsPrimary);
        _keyboardGroups = _devices
            .GroupBy(item => item.PhysicalDeviceId ?? item.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new KeyboardGroup(group.Key, group.First().DisplayName,
                group.OrderBy(item => item.DevicePath, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderByDescending(group => group.IsConnected)
            .ThenBy(group => group.DisplayName)
            .ToArray();
        _selectedKeyboard = _keyboardGroups.FirstOrDefault(group => group.IsSelected)
            ?? _keyboardGroups.FirstOrDefault(group => group.Id == previousKeyboardId);

        OnPropertyChanged(nameof(Devices));
        OnPropertyChanged(nameof(Primary));
        OnPropertyChanged(nameof(KeyboardGroups));
        OnPropertyChanged(nameof(SelectedKeyboard));
        OnPropertyChanged(nameof(SelectedSource));
        MakePrimaryCommand.NotifyCanExecuteChanged();
    }
}
