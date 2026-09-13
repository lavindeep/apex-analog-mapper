using System.Collections.ObjectModel;
using ApexMapper.App.Services;
using ApexMapper.App.ViewModels.Profiles;
using ApexMapper.Core.Keys;
using ApexMapper.Input.Abstractions.Backends;
using ApexMapper.Input.Abstractions.Devices;
using ApexMapper.Input.Hid;
using ApexMapper.Persistence.Devices;
using CommunityToolkit.Mvvm.Input;

namespace ApexMapper.App.ViewModels.Devices;

public sealed class SensorCalibrationViewModel(
    DeviceSelector selector, ApexProAnalogInput input, MappingSession session) : ObservableViewModel
{
    private DiscoveredDevice? _device;
    private IDisposable? _mappingBlock;
    private string? _error;
    public ObservableCollection<SensorCalibrationRow> Rows { get; } = new();
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    public void Begin()
    {
        _mappingBlock ??= session.BlockForInputEditing();
        _device = selector.SelectedDevice;
        Error = null;
        Rows.Clear();
        if (_device?.PhysicalDeviceId is null)
        {
            Error = "Select a connected keyboard first.";
            return;
        }
        var saved = selector.GetCalibrations(_device, "4.9.1");
        foreach (var key in input.RequiredKeys.OrderBy(key => key.ScanCode))
            Rows.Add(new SensorCalibrationRow(key, saved.FirstOrDefault(c => c.Key == key), Capture));
        if (Rows.Count == 0) Error = "This profile has no analog keys.";
    }

    public void Refresh()
    {
        foreach (var row in Rows)
        {
            var live = input.TryGetRaw(row.Key, out var raw);
            row.Update(live, raw);
        }
    }

    public void End()
    {
        _mappingBlock?.Dispose();
        _mappingBlock = null;
    }

    private void Capture(SensorCalibrationRow row, bool released)
    {
        Error = null;
        if (!SameSelection() || !input.TryGetRaw(row.Key, out var raw))
        {
            Error = "No sensor reading. Check the keyboard connection.";
            return;
        }
        if (!released && (row.Rest is null || MathF.Abs(raw - row.Rest.Value) < 100))
        {
            Error = "Set released first, then hold the key fully down.";
            return;
        }
        row.Capture(raw, released);
    }

    public bool Save()
    {
        Error = null;
        if (!SameSelection() || !input.RequiredKeys.ToHashSet().SetEquals(Rows.Select(row => row.Key)))
        {
            Error = "The keyboard or profile changed. Open calibration again.";
            return false;
        }
        if (Rows.Count == 0 || Rows.Any(row => row.Measurement is null))
        {
            Error = "Set both endpoints for each key.";
            return false;
        }
        try
        {
            var keys = Rows.Select(row => row.Key).ToHashSet();
            var measurements = selector.GetCalibrations(_device!, "4.9.1")
                .Where(c => !keys.Contains(c.Key))
                .Concat(Rows.Select(row => row.Measurement!)).ToArray();
            selector.SaveCalibrations(_device!.PhysicalDeviceId!, "4.9.1", measurements);
            input.ReloadCalibration();
            return true;
        }
        catch (Exception error)
        {
            Error = error.Message;
            return false;
        }
    }

    private bool SameSelection() => _device?.PhysicalDeviceId is { } id
        && string.Equals(selector.SelectedDevice?.PhysicalDeviceId, id, StringComparison.OrdinalIgnoreCase);
}

public sealed class SensorCalibrationRow : ObservableViewModel
{
    private float _travelPercent;
    private bool _isLive;
    public SensorCalibrationRow(KeyId key, KeyCalibration? saved, Action<SensorCalibrationRow, bool> capture)
    {
        Key = key;
        Rest = saved?.RestValue;
        Pressed = saved?.MaxPressValue;
        SetReleasedCommand = new RelayCommand(() => capture(this, true));
        SetPressedCommand = new RelayCommand(() => capture(this, false));
    }

    public KeyId Key { get; }
    public string Name => BindingSummaryItem.KeyName(Key);
    public float? Rest { get; private set; }
    public float? Pressed { get; private set; }
    public string ReleasedLabel => Rest.HasValue ? "Released set" : "Set released";
    public string PressedLabel => Pressed.HasValue ? "Full press set" : "Set fully pressed";
    public float TravelPercent { get => _travelPercent; private set => SetProperty(ref _travelPercent, value); }
    public bool IsLive { get => _isLive; private set => SetProperty(ref _isLive, value); }
    public IRelayCommand SetReleasedCommand { get; }
    public IRelayCommand SetPressedCommand { get; }
    public KeyCalibration? Measurement => Rest is { } rest && Pressed is { } pressed
        && MathF.Abs(pressed - rest) >= 100 ? new(Key, rest, pressed, 15) : null;

    internal void Capture(float raw, bool released)
    {
        if (released) Rest = raw;
        else Pressed = raw;
        OnPropertyChanged(nameof(ReleasedLabel));
        OnPropertyChanged(nameof(PressedLabel));
    }

    internal void Update(bool live, float raw)
    {
        IsLive = live;
        TravelPercent = live && Measurement is { } c
            ? MathF.Abs(raw - c.RestValue) <= c.NoiseBand ? 0
                : Math.Clamp((raw - c.RestValue) / (c.MaxPressValue - c.RestValue), 0, 1) * 100
            : 0;
    }
}
