using ApexMapper.Persistence.Devices;

namespace ApexMapper.Input.Abstractions.Backends;

/// <summary>
/// A keyboard arrival or removal seen by the raw-input backend.
/// <see cref="DeviceId"/> is the producing adapter's per-device tag — the
/// same value stamped on <see cref="Pipeline.RawKeyEvent.DeviceId"/> — so
/// consumers can map a device identity to the events it produces; 0 means
/// unknown.
/// </summary>
public sealed record RawInputDeviceChanged(
    DeviceIdentity Device,
    bool Attached,
    string DevicePath,
    int DeviceId = 0);
